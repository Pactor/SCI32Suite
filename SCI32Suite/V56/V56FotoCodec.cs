
// After the 26-byte file stamp is consumed, the V56 body begins with:
//   u16 viewHeaderSize (0x0012)
//   then viewHeaderSize bytes of header (18 bytes total):
//     u16 loopCount
//     u16 headerFlagsA   (commonly 0x0101 or 0x0001 depending on file)
//     u16 headerFlagsB   (commonly 0x0001)
//     u32 palettePayloadOffset   (ABS from start-of-body, includes +6 tag header)
//     u16 magic3410      (0x3410)
//     u16 resX           (e.g., 0x0280 = 640)
//     u16 resY           (e.g., 0x01E0 = 480)
//     u16 magic0084      (0x0084)
//
// Loops begin at: 2 + viewHeaderSize.  (matches your extractor baseline: loopsBase = base + viewHeaderSize + 2)
//
// The encoder API is the same flow:
//   - Create V56Cel
//   - Add loops (image or mirror) to each cel
//   - Add cels to List<V56Cel>
//   - Palette is CompPal built from cel0.loop0 image
//   - Writes CompPal (0x0300) and Images (0x0400 + 0x0500) and patches offsets.
//
// IMPORTANT: The 26-byte file stamp is written ONLY for compatibility, and offsets are computed from the body start.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

public static class V56Encoder
{
    private const ushort VIEW_HEADER_SIZE_A = 0x0012; // 18 bytes after the u16 size field (Variant A)
    private const ushort VIEW_HEADER_SIZE_B = 0x0010; // 16 bytes after the u16 size field (Variant B)
    private const ushort LOOP_HEADER_SIZE = 0x0010; // 16


    private static ushort GetViewHeaderSize(V56EncodeOptions opt)
    {
        if (opt == null) return VIEW_HEADER_SIZE_A;
        return (opt.HeaderVariant == V56ViewHeaderVariant.B_0x10_No0084) ? VIEW_HEADER_SIZE_B : VIEW_HEADER_SIZE_A;
    }

    private const ushort CEL_HEADER_SIZE = 0x0034; // 52

    private const byte TRANSPARENT_INDEX = 255;
    private const byte DEFAULT_CEL_FLAGS = 0x8A;

    public sealed class V56Cel
    {
        public readonly List<V56LoopDef> Loops = new List<V56LoopDef>();
        public short DefaultXHot = 0;
        public short DefaultYHot = 0;

        public V56ImageLoop AddImageLoop(Image bmp)
        {
            Bitmap b = bmp as Bitmap;
            return AddImageLoop(b);
        }
        public V56ImageLoop AddImageLoop(Image bmp, short xHot, short yHot)
        {
            Bitmap b = bmp as Bitmap;
            return AddImageLoop(b, xHot, yHot);
        }
        public V56ImageLoop AddImageLoop(Bitmap bmp)
        {
            if (bmp == null) throw new ArgumentNullException("bmp");
            V56ImageLoop l = new V56ImageLoop(bmp, DefaultXHot, DefaultYHot);
            Loops.Add(l);
            return l;
        }

        public V56ImageLoop AddImageLoop(Bitmap bmp, short xHot, short yHot)
        {
            if (bmp == null) throw new ArgumentNullException("bmp");

            V56ImageLoop l = new V56ImageLoop(bmp, xHot, yHot);
            Loops.Add(l);
            return l;
        }

        public V56MirrorLoop AddMirrorLoop(int mirrorOfLoopIndex)
        {
            V56MirrorLoop l = new V56MirrorLoop(mirrorOfLoopIndex);
            Loops.Add(l);
            return l;
        }
    }

    public abstract class V56LoopDef { }

    public sealed class V56MirrorLoop : V56LoopDef
    {
        public int MirrorOf;
        public V56MirrorLoop(int mirrorOf) { MirrorOf = mirrorOf; }
    }
    public sealed class V56ImageLoop : V56LoopDef
    {
        public int Width;
        public int Height;
        public short XHot;
        public short YHot;

        public int[] Rgba;     // ARGB 0xAARRGGBB, row-major
        public byte[] Indices; // indexed after palette build

        public V56ImageLoop(Bitmap bmp, short xHot, short yHot)
        {
            Width = bmp.Width;
            Height = bmp.Height;
            XHot = xHot;
            YHot = yHot;
            Rgba = BitmapToRgba(bmp);
            Indices = null;
        }
    }
    public static Bitmap RgbaToBitmap(V56Encoder.V56ImageLoop img)
    {
        Bitmap bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, img.Width, img.Height);
        var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            // img.Rgba is int[] ARGB; bitmap wants BGRA bytes, but we can copy as ints on little-endian.
            System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, bd.Scan0, img.Rgba.Length);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }
    // -------------------------------------------------
    // Encode options (header fields)
    // -------------------------------------------------

    public enum V56ViewHeaderVariant
    {
        // Variant A: viewHeaderSize=0x12 (18 bytes after size field) and includes trailing u16 (usually 0x0084)
        A_0x12_With0084 = 0,

        // Variant B: viewHeaderSize=0x10 (16 bytes after size field) and omits trailing u16
        B_0x10_No0084 = 1,
    }

    public sealed class V56EncodeOptions
    {
        public V56ViewHeaderVariant HeaderVariant = V56ViewHeaderVariant.A_0x12_With0084;

        // These match the 18-byte header fields seen in your sample.
        public ushort HeaderFlagsA = 0xFF01; // in your good file sometimes 0x0101
        public ushort HeaderFlagsB = 0x0030;
        public ushort Magic3410 = 0x3410;
        public ushort ResX = 0x0280;
        public ushort ResY = 0x01E0;
        public ushort Magic0084 = 0x0084;
    }
    public static List<V56Cel> Decode(byte[] fileData, out V56ViewHeaderVariant headerVariant)
    {
        if (fileData == null) throw new ArgumentNullException("fileData");
        if (fileData.Length < 26 + 2) throw new InvalidDataException("File too small.");

        int stampLen = 26;
        int bodyBase = stampLen; // offsets inside file are ABS from start-of-body
        int p = bodyBase;

        // --- View header ---
        ushort viewHeaderSize = (ushort)(fileData[p] | (fileData[p + 1] << 8)); p += 2;

        if (viewHeaderSize == 0x0012) headerVariant = V56ViewHeaderVariant.A_0x12_With0084;
        else if (viewHeaderSize == 0x0010) headerVariant = V56ViewHeaderVariant.B_0x10_No0084;
        else throw new InvalidDataException("Unsupported viewHeaderSize: 0x" + viewHeaderSize.ToString("X"));

        int viewHeaderTotalLen = 2 + viewHeaderSize;

        ushort loopCountU16 = (ushort)(fileData[p] | (fileData[p + 1] << 8)); p += 2;
        int loopCount = loopCountU16;

        // flags (kept for completeness; not used)
        ushort flagsA = (ushort)(fileData[p] | (fileData[p + 1] << 8)); p += 2;
        ushort flagsB = (ushort)(fileData[p] | (fileData[p + 1] << 8)); p += 2;

        uint palettePayloadOffsetAbs = (uint)(fileData[p] | (fileData[p + 1] << 8) | (fileData[p + 2] << 16) | (fileData[p + 3] << 24)); p += 4;

        ushort sizesWord = (ushort)(fileData[p] | (fileData[p + 1] << 8)); p += 2;
        int loopHeaderSize = (sizesWord & 0x00FF);
        int celHeaderSize = (sizesWord >> 8) & 0x00FF;

        ushort resX = (ushort)(fileData[p] | (fileData[p + 1] << 8)); p += 2;
        ushort resY = (ushort)(fileData[p] | (fileData[p + 1] << 8)); p += 2;

        if (headerVariant == V56ViewHeaderVariant.A_0x12_With0084)
        {
            // trailing u16 (often 0x0084)
            p += 2;
        }

        if (loopHeaderSize != LOOP_HEADER_SIZE) throw new InvalidDataException("Unsupported loopHeaderSize: " + loopHeaderSize);
        if (celHeaderSize != 0x24 && celHeaderSize != CEL_HEADER_SIZE) throw new InvalidDataException("Unsupported celHeaderSize: 0x" + celHeaderSize.ToString("X"));

        int loopHeadersStart = bodyBase + viewHeaderTotalLen;
        int loopHeadersLen = loopCount * LOOP_HEADER_SIZE;
        int celHeadersStart = loopHeadersStart + loopHeadersLen;

        if (celHeadersStart > fileData.Length) throw new InvalidDataException("Header overruns file.");

        // --- Read loop headers ---
        byte[] loopNumCels = new byte[loopCount];
        int[] loopCelTableOffsetAbs = new int[loopCount];
        bool[] loopIsMirror = new bool[loopCount];
        int[] loopMirrorOf = new int[loopCount];

        int totalCels = 0;
        for (int li = 0; li < loopCount; li++)
        {
            int o = loopHeadersStart + (li * LOOP_HEADER_SIZE);

            int b0 = fileData[o + 0x00] & 0xFF;
            int b1 = fileData[o + 0x01] & 0xFF;
            int b2 = fileData[o + 0x02] & 0xFF;

            bool isMirror = (b1 == 0x01) && (b2 == 0x00) && (b0 != 0xFF);
            loopIsMirror[li] = isMirror;

            if (isMirror)
            {
                loopMirrorOf[li] = b0;
                loopNumCels[li] = 0;
                loopCelTableOffsetAbs[li] = 0;
            }
            else
            {
                loopMirrorOf[li] = -1;
                loopNumCels[li] = (byte)b2;
                int off = o + 0x0C;
                int celOffAbs = fileData[off] | (fileData[off + 1] << 8) | (fileData[off + 2] << 16) | (fileData[off + 3] << 24);
                loopCelTableOffsetAbs[li] = celOffAbs;
                totalCels += loopNumCels[li];
            }
        }

        // --- Read cel headers ---
        int celHeadersBytes = totalCels * celHeaderSize;
        int afterCelHeaders = celHeadersStart + celHeadersBytes;
        if (afterCelHeaders > fileData.Length) throw new InvalidDataException("Cel headers overrun file.");

        // --- Read palette chunk (0x0300) ---
        int chunkP = afterCelHeaders;
        ushort palTag = (ushort)(fileData[chunkP] | (fileData[chunkP + 1] << 8));
        if (palTag != 0x0300) throw new InvalidDataException("Expected 0x0300 palette tag.");
        uint palLen = (uint)(fileData[chunkP + 2] | (fileData[chunkP + 3] << 8) | (fileData[chunkP + 4] << 16) | (fileData[chunkP + 5] << 24));
        int palPayloadStart = chunkP + 6;
        int palPayloadEnd = palPayloadStart + (int)palLen;
        if (palPayloadEnd > fileData.Length) throw new InvalidDataException("Palette payload out of range.");

        // Palette payload: 37-byte header + 256*4 entries [flag,R,G,B]
        if (palLen < 37 + 256 * 4) throw new InvalidDataException("Palette payload too small.");
        int palEntriesStart = palPayloadStart + 37;

        int[] palArgb = new int[256];
        for (int i = 0; i < 256; i++)
        {
            int o = palEntriesStart + (i * 4);
            // byte flag = fileData[o+0]; // flag is meaningful to SV, but for rendering we use RGB
            int r = fileData[o + 1] & 0xFF;
            int g = fileData[o + 2] & 0xFF;
            int b = fileData[o + 3] & 0xFF;
            palArgb[i] = unchecked((int)0xFF000000u | (r << 16) | (g << 8) | b);
        }

        // --- Read 0x0400 image chunk header ---
        chunkP = palPayloadEnd;
        ushort imgTag = (ushort)(fileData[chunkP] | (fileData[chunkP + 1] << 8));
        if (imgTag != 0x0400) throw new InvalidDataException("Expected 0x0400 image tag.");
        uint imgLen = (uint)(fileData[chunkP + 2] | (fileData[chunkP + 3] << 8) | (fileData[chunkP + 4] << 16) | (fileData[chunkP + 5] << 24));
        int imgPayloadStart = chunkP + 6;
        int imgPayloadEnd = imgPayloadStart + (int)imgLen;
        if (imgPayloadEnd > fileData.Length) throw new InvalidDataException("Image payload out of range.");

        // --- Next is 0x0500 header (len=0), then rowTables blob until 0x0600 ---
        int tag0500 = imgPayloadEnd;
        ushort tag = (ushort)(fileData[tag0500] | (fileData[tag0500 + 1] << 8));
        if (tag != 0x0500) throw new InvalidDataException("Expected 0x0500 tag.");
        uint len0500 = (uint)(fileData[tag0500 + 2] | (fileData[tag0500 + 3] << 8) | (fileData[tag0500 + 4] << 16) | (fileData[tag0500 + 5] << 24));
        if (len0500 != 0u) throw new InvalidDataException("Expected 0x0500 length 0.");
        int rowTablesStartAbs = (tag0500 + 6) - bodyBase; // ABS-from-body for cel headers (so we can compare)
        int rowTablesStart = tag0500 + 6;

        // find 0x0600 header
        int tag0600 = -1;
        for (int scan = rowTablesStart; scan + 6 <= fileData.Length; scan++)
        {
            if ((fileData[scan] | (fileData[scan + 1] << 8)) == 0x0600)
            {
                tag0600 = scan;
                break;
            }
        }
        if (tag0600 < 0) throw new InvalidDataException("Missing 0x0600 terminator.");
        int rowTablesEnd = tag0600; // exclusive

        // --- Determine celCount in "List<V56Cel>" sense ---
        int celCount = 0;
        for (int li = 0; li < loopCount; li++)
        {
            if (!loopIsMirror[li] && loopNumCels[li] > celCount) celCount = loopNumCels[li];
        }
        if (celCount <= 0) throw new InvalidDataException("No cels found.");

        List<V56Cel> outCels = new List<V56Cel>();
        for (int ci = 0; ci < celCount; ci++) outCels.Add(new V56Cel());

        // --- Walk loops and decode each cel's image ---
        for (int li = 0; li < loopCount; li++)
        {
            if (loopIsMirror[li])
            {
                for (int ci = 0; ci < celCount; ci++)
                    outCels[ci].Loops.Add(new V56MirrorLoop(loopMirrorOf[li]));
                continue;
            }

            int loopNum = loopNumCels[li];
            int celTableAbs = loopCelTableOffsetAbs[li];
            int celTablePos = bodyBase + celTableAbs;

            // celTableAbs points to the FIRST cel header for this loop
            for (int ci = 0; ci < loopNum; ci++)
            {
                int chPos = celTablePos + (ci * celHeaderSize);
                if (chPos + 0x24 > fileData.Length) throw new InvalidDataException("Cel header out of range.");

                int w = fileData[chPos + 0x00] | (fileData[chPos + 0x01] << 8);
                int h = fileData[chPos + 0x02] | (fileData[chPos + 0x03] << 8);
                short xHot = unchecked((short)(fileData[chPos + 0x04] | (fileData[chPos + 0x05] << 8)));
                short yHot = unchecked((short)(fileData[chPos + 0x06] | (fileData[chPos + 0x07] << 8)));
                byte transparentIndex = fileData[chPos + 0x08];

                // CelBase-ish counts (we write these now)
                int dataByteCount = fileData[chPos + 0x0C] | (fileData[chPos + 0x0D] << 8) | (fileData[chPos + 0x0E] << 16) | (fileData[chPos + 0x0F] << 24);
                int controlByteCount = fileData[chPos + 0x10] | (fileData[chPos + 0x11] << 8) | (fileData[chPos + 0x12] << 16) | (fileData[chPos + 0x13] << 24);

                int controlOffAbs = fileData[chPos + 0x18] | (fileData[chPos + 0x19] << 8) | (fileData[chPos + 0x1A] << 16) | (fileData[chPos + 0x1B] << 24);
                int dataOffAbs = fileData[chPos + 0x1C] | (fileData[chPos + 0x1D] << 8) | (fileData[chPos + 0x1E] << 16) | (fileData[chPos + 0x1F] << 24);
                int rowOffAbs = fileData[chPos + 0x20] | (fileData[chPos + 0x21] << 8) | (fileData[chPos + 0x22] << 16) | (fileData[chPos + 0x23] << 24);

                int controlBase = bodyBase + controlOffAbs;
                int dataBase = bodyBase + dataOffAbs;
                int rowBase = bodyBase + rowOffAbs;

                if (controlBase < 0 || controlBase > fileData.Length) throw new InvalidDataException("ControlOffset out of range.");
                if (dataBase < 0 || dataBase > fileData.Length) throw new InvalidDataException("ColorOffset out of range.");
                if (rowBase < 0 || rowBase > fileData.Length) throw new InvalidDataException("RowTableOffset out of range.");

                // Validate control/data byte count if present
                if (controlByteCount < 0) throw new InvalidDataException("Invalid controlByteCount.");
                if (dataByteCount < 0) throw new InvalidDataException("Invalid dataByteCount.");

                // row tables: u32[h] tagOff + u32[h] dataOff (relative to cel stream bases)
                int tagsPos = rowBase;
                int colsPos = rowBase + (h * 4);
                int tableBytes = h * 8;
                if (rowBase + tableBytes > fileData.Length) throw new InvalidDataException("Row tables overrun file.");

                byte[] outIdx = new byte[w * h];

                for (int y = 0; y < h; y++)
                {
                    int tagRel = fileData[tagsPos + y * 4] | (fileData[tagsPos + y * 4 + 1] << 8) | (fileData[tagsPos + y * 4 + 2] << 16) | (fileData[tagsPos + y * 4 + 3] << 24);
                    int colRel = fileData[colsPos + y * 4] | (fileData[colsPos + y * 4 + 1] << 8) | (fileData[colsPos + y * 4 + 2] << 16) | (fileData[colsPos + y * 4 + 3] << 24);

                    int cp = controlBase + tagRel;
                    int dp = dataBase + colRel;

                    int x = 0;
                    int rowStart = y * w;

                    while (x < w)
                    {
                        if (cp < 0 || cp >= fileData.Length) throw new InvalidDataException("Control stream out of range.");
                        int cb = fileData[cp++] & 0xFF;

                        if (cb < 0x80)
                        {
                            int n = cb;
                            if (n == 0) throw new InvalidDataException("Zero-length literal.");
                            if (dp < 0 || dp + n > fileData.Length) throw new InvalidDataException("Color stream out of range.");

                            for (int k = 0; k < n; k++)
                            {
                                if (x >= w) break;
                                outIdx[rowStart + x] = fileData[dp++];
                                x++;
                            }
                        }
                        else if (cb < 0xC0)
                        {
                            int n = cb - 0x80;
                            if (n <= 0) throw new InvalidDataException("Zero-length run.");
                            if (dp < 0 || dp >= fileData.Length) throw new InvalidDataException("Color stream out of range.");
                            byte v = fileData[dp++];

                            for (int k = 0; k < n; k++)
                            {
                                if (x >= w) break;
                                outIdx[rowStart + x] = v;
                                x++;
                            }
                        }
                        else
                        {
                            int n = cb - 0xC0;
                            if (n <= 0) throw new InvalidDataException("Zero-length transparent run.");
                            for (int k = 0; k < n; k++)
                            {
                                if (x >= w) break;
                                outIdx[rowStart + x] = transparentIndex;
                                x++;
                            }
                        }
                    }
                }

                // Build a 32bpp ARGB bitmap so we can reuse the existing V56ImageLoop(Bitmap,...) ctor
                Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, w, h);
                var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                try
                {
                    int stride = bd.Stride;
                    byte[] row = new byte[stride];

                    for (int y = 0; y < h; y++)
                    {
                        int rowStart = y * w;
                        int rp = 0;

                        for (int x = 0; x < w; x++)
                        {
                            byte ix = outIdx[rowStart + x];
                            if (ix == transparentIndex)
                            {
                                row[rp++] = 0; // B
                                row[rp++] = 0; // G
                                row[rp++] = 0; // R
                                row[rp++] = 0; // A
                            }
                            else
                            {
                                int argb = palArgb[ix];
                                row[rp++] = (byte)(argb & 0xFF);         // B
                                row[rp++] = (byte)((argb >> 8) & 0xFF);  // G
                                row[rp++] = (byte)((argb >> 16) & 0xFF); // R
                                row[rp++] = 0xFF;                        // A
                            }
                        }

                        System.Runtime.InteropServices.Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * stride), stride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                V56ImageLoop img = new V56ImageLoop(bmp, xHot, yHot);
                img.Indices = outIdx;
                bmp.Dispose();

                // Ensure the per-cel list has loopCount entries in order
                // We add loops in loop order, so just append.
                outCels[ci].Loops.Add(img);
            }

            // If some loops have fewer cels than celCount, pad the missing frames with mirrors to itself (safe)
            for (int ci = loopNum; ci < celCount; ci++)
            {
                outCels[ci].Loops.Add(new V56MirrorLoop(li));
            }
        }

        return outCels;
    }

    // fileStamp26 is optionally prepended LAST and is NOT included in any internal offsets.
    public static byte[] Encode(List<V56Cel> cels, byte[] fileStamp26, V56EncodeOptions opt)
    {
        if (cels == null) throw new ArgumentNullException("cels");
        if (cels.Count <= 0) throw new InvalidOperationException("No cels.");
        if (opt == null) opt = new V56EncodeOptions();
        if (fileStamp26 != null && fileStamp26.Length != 26) throw new ArgumentException("fileStamp26 must be 26 bytes.");

        NormalizedView nv = NormalizeCelsToView(cels);

        // Palette source = cel0.loop0 image
        V56ImageLoop palSource = GetPaletteSource(cels);
        PaletteBuild pal = BuildPaletteFromImage(palSource);

        // Index all images to palette
        IndexAllImages(nv, pal);

        // Encode image streams (control/rowTables/data) in loop-major then cel-major order
        StreamsAggregate streams = BuildStreams(nv, pal.TransparentIndex);

        // Build loop headers + cel headers
        HeaderPack headers = BuildHeaders(nv, pal.TransparentIndex, opt);

        // Build palette chunk
        byte[] palChunk = BuildCompPalChunk(pal.CompPalPayload);

        // Compute layout using the *real* header base:
        // body layout:
        //   u16 viewHeaderSize + viewHeaderSize bytes
        //   loop headers
        //   cel headers
        //   palette chunk (tag+len+payload)
        //   image chunk 0x0400 (tag+len+control)
        //   end chunk 0x0500 (tag+len=0)
        //   rowTables blob
        //   data blob
        Layout layout = ComputeLayout(
            viewHeaderTotalLen: headers.ViewHeaderTotalLen,
            loopHeadersLen: headers.LoopHeaders.Length,
            celHeadersLen: headers.CelHeaders.Length,
            palChunkLen: palChunk.Length,
            controlLen: streams.Control.Length,
            rowTablesLen: streams.RowTables.Length,
            dataLen: streams.Data.Length
        );

        // Build view header bytes (u16 size + 18 bytes)
        byte[] viewHeader = BuildViewHeader(opt, (ushort)nv.LoopCount, layout.PalettePayloadOffset);

        // Patch loop celTableOffset (ABS) and mirrors to target celTable
        PatchLoopCelTableOffsets(headers, viewHeader.Length);

        // Patch cel header offsets (ABS) to control/data/rowTable
        PatchCelHeaderOffsets(headers.CelHeaders, streams, layout);

        byte[] imageSection = BuildImageSection(streams.Control, streams.Data, streams.RowTables);

        // Full body
        byte[] body = Concat(viewHeader, headers.LoopHeaders, headers.CelHeaders, palChunk, imageSection);

        if (fileStamp26 == null) return body;
        return Concat(fileStamp26, body);
    }
    private static byte[] Concat(byte[] a, byte[] b, byte[] c, byte[] d, byte[] e)
    {
        int la = (a == null) ? 0 : a.Length;
        int lb = (b == null) ? 0 : b.Length;
        int lc = (c == null) ? 0 : c.Length;
        int ld = (d == null) ? 0 : d.Length;
        int le = (e == null) ? 0 : e.Length;

        byte[] r = new byte[la + lb + lc + ld + le];
        int o = 0;
        CopyBytes(a, r, ref o);
        CopyBytes(b, r, ref o);
        CopyBytes(c, r, ref o);
        CopyBytes(d, r, ref o);
        CopyBytes(e, r, ref o);
        return r;
    }

    private static void CopyBytes(byte[] src, byte[] dst, ref int offset)
    {
        if (src == null || src.Length == 0) return;
        Buffer.BlockCopy(src, 0, dst, offset, src.Length);
        offset += src.Length;
    }

    public static byte[] Encode(List<V56Cel> cels, byte[] fileStamp26)
    {
        return Encode(cels, fileStamp26, new V56EncodeOptions());
    }

    // -------------------------------------------------
    // Normalization: (cels -> view)
    // -------------------------------------------------
    private sealed class NormalizedView
    {
        public int LoopCount;
        public int CelCount;

        public bool[] IsMirror;
        public int[] MirrorOf;

        public V56ImageLoop[][] Frames; // [loop][cel] for non-mirror loops
    }

    private static NormalizedView NormalizeCelsToView(List<V56Cel> cels)
    {
        int celCount = cels.Count;

        int maxLoops = 0;
        for (int ci = 0; ci < celCount; ci++)
        {
            if (cels[ci] == null) throw new InvalidOperationException("Null cel.");
            if (cels[ci].Loops == null) throw new InvalidOperationException("Null cel.Loops.");
            if (cels[ci].Loops.Count > maxLoops) maxLoops = cels[ci].Loops.Count;
        }
        if (maxLoops <= 0) throw new InvalidOperationException("Cels contain no loops.");

        bool[] isMirror = new bool[maxLoops];
        int[] mirrorOf = new int[maxLoops];
        for (int i = 0; i < maxLoops; i++) mirrorOf[i] = -1;

        // Mirror status must be consistent across cels for each loop index.
        for (int li = 0; li < maxLoops; li++)
        {
            bool anyMirror = false;
            bool anyImage = false;
            int mirrorTarget = -1;

            for (int ci = 0; ci < celCount; ci++)
            {
                V56Cel cel = cels[ci];
                if (li >= cel.Loops.Count)
                    throw new InvalidOperationException("Cel " + ci + " missing loop index " + li + ". All cels must have same loop count (pad with mirrors/images).");

                V56LoopDef def = cel.Loops[li];
                if (def is V56MirrorLoop)
                {
                    anyMirror = true;
                    int tgt = ((V56MirrorLoop)def).MirrorOf;
                    if (mirrorTarget < 0) mirrorTarget = tgt;
                    else if (mirrorTarget != tgt) throw new InvalidOperationException("Loop " + li + " mirror target differs across cels.");
                }
                else if (def is V56ImageLoop)
                {
                    anyImage = true;
                }
                else
                {
                    throw new InvalidOperationException("Unknown loop def type.");
                }
            }

            if (anyMirror && anyImage)
                throw new InvalidOperationException("Loop " + li + " is mirror in some cels and image in others. Must be consistent.");

            if (anyMirror)
            {
                if (mirrorTarget < 0) throw new InvalidOperationException("Loop " + li + " mirror target not set.");
                if (mirrorTarget < 0 || mirrorTarget >= maxLoops) throw new InvalidOperationException("Loop " + li + " mirror target out of range.");
                isMirror[li] = true;
                mirrorOf[li] = mirrorTarget;
            }
            else
            {
                isMirror[li] = false;
                mirrorOf[li] = -1;
            }
        }

        V56ImageLoop[][] frames = new V56ImageLoop[maxLoops][];
        for (int li = 0; li < maxLoops; li++)
            frames[li] = isMirror[li] ? null : new V56ImageLoop[celCount];

        for (int li = 0; li < maxLoops; li++)
        {
            if (isMirror[li]) continue;

            for (int ci = 0; ci < celCount; ci++)
            {
                V56LoopDef def = cels[ci].Loops[li];
                V56ImageLoop img = def as V56ImageLoop;
                if (img == null) throw new InvalidOperationException("Loop " + li + " expected image loop.");
                if (img.Width <= 0 || img.Height <= 0) throw new InvalidOperationException("Bad image dims.");
                if (img.Rgba == null || img.Rgba.Length != img.Width * img.Height) throw new InvalidOperationException("Bad image buffer.");
                frames[li][ci] = img;
            }
        }

        NormalizedView nv = new NormalizedView();
        nv.LoopCount = maxLoops;
        nv.CelCount = celCount;
        nv.IsMirror = isMirror;
        nv.MirrorOf = mirrorOf;
        nv.Frames = frames;
        return nv;
    }

    private static V56ImageLoop GetPaletteSource(List<V56Cel> cels)
    {
        if (cels[0].Loops.Count <= 0) throw new InvalidOperationException("Cel0 has no loops.");
        V56LoopDef def = cels[0].Loops[0];
        V56ImageLoop img = def as V56ImageLoop;
        if (img == null) throw new InvalidOperationException("Palette source must be an image: cel0.loop0 cannot be a mirror.");
        return img;
    }

    // -------------------------------------------------
    // Palette build + indexing (CompPal only)
    // -------------------------------------------------
    private sealed class PaletteBuild
    {
        public byte TransparentIndex;
        public Color[] Palette256;
        public byte[] CompPalPayload;
    }

    private static PaletteBuild BuildPaletteFromImage(V56ImageLoop src)
    {
        Dictionary<int, int> seen = new Dictionary<int, int>();
        List<int> unique = new List<int>();

        int[] px = src.Rgba;
        for (int i = 0; i < px.Length; i++)
        {
            int argb = px[i];
            int a = (argb >> 24) & 0xFF;
            if (a < 128) continue;
            int rgb = argb & 0x00FFFFFF;
            if (!seen.ContainsKey(rgb))
            {
                seen.Add(rgb, 1);
                unique.Add(rgb);
            }
        }

        Color[] pal = new Color[256];
        for (int i = 0; i < 256; i++) pal[i] = Color.Black;
        pal[TRANSPARENT_INDEX] = Color.White;

        Color[] opaque;
        if (unique.Count <= 255)
        {
            opaque = new Color[unique.Count];
            for (int i = 0; i < unique.Count; i++)
            {
                int rgb = unique[i];
                opaque[i] = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            }
        }
        else
        {
            opaque = QuantizeMedianCut(unique, 255);
        }

        for (int i = 0; i < opaque.Length && i < 255; i++)
            pal[i] = opaque[i];

        PaletteBuild pb = new PaletteBuild();
        pb.TransparentIndex = TRANSPARENT_INDEX;
        pb.Palette256 = pal;
        pb.CompPalPayload = BuildCompPalPayload_RGBTriples(pal);
        return pb;
    }

    private static void IndexAllImages(NormalizedView nv, PaletteBuild pal)
    {
        Dictionary<int, byte> exact = new Dictionary<int, byte>();
        for (int i = 0; i < 255; i++)
        {
            Color c = pal.Palette256[i];
            int rgb = (c.R << 16) | (c.G << 8) | c.B;
            if (!exact.ContainsKey(rgb)) exact.Add(rgb, (byte)i);
        }

        for (int li = 0; li < nv.LoopCount; li++)
        {
            if (nv.IsMirror[li]) continue;

            for (int ci = 0; ci < nv.CelCount; ci++)
            {
                V56ImageLoop img = nv.Frames[li][ci];
                byte[] idx = new byte[img.Width * img.Height];

                for (int p = 0; p < img.Rgba.Length; p++)
                {
                    int argb = img.Rgba[p];
                    int a = (argb >> 24) & 0xFF;
                    if (a < 128)
                    {
                        idx[p] = pal.TransparentIndex;
                        continue;
                    }

                    int rgb = argb & 0x00FFFFFF;
                    byte mapped;
                    if (!exact.TryGetValue(rgb, out mapped))
                        mapped = FindNearestPaletteIndex(pal.Palette256, 255, rgb);
                    idx[p] = mapped;
                }

                img.Indices = idx;
            }
        }
    }

    private static byte FindNearestPaletteIndex(Color[] palette, int count, int rgb)
    {
        int r = (rgb >> 16) & 0xFF;
        int g = (rgb >> 8) & 0xFF;
        int b = rgb & 0xFF;

        int best = 0;
        int bestD = int.MaxValue;

        for (int i = 0; i < count && i < 255; i++)
        {
            Color c = palette[i];
            int dr = c.R - r;
            int dg = c.G - g;
            int db = c.B - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return (byte)best;
    }

    // Matches your reader's ParseCompPalPayload palType==1 path (RGB triples at offset 37).
    private static byte[] BuildCompPalPayload_RGBTriples(Color[] pal256)
    {
        // Stock/FotSCIhop compal: 37-byte header + 256 * 4 bytes (flag,R,G,B)
        byte[] payload = new byte[37 + 256 * 4];

        // Header constants (keep exactly as you already had)
        payload[0] = 0x0E;
        for (int i = 1; i <= 9; i++) payload[i] = 0x20; // spaces
        payload[10] = 0x01;
        payload[13] = 0x16;//0x66;
        payload[14] = 0x04;//0x01;

        payload[25] = 0; // startOffset

        // nColors = 256 (little-endian) in your existing header layout
        payload[29] = 0;
        payload[30] = 1;
        payload[31] = 1;
        // Entries start at 37: [flag,R,G,B] * 256
        int o = 37;
        for (int i = 0; i < 256; i++)
        {
            Color c = pal256[i];
            payload[o + 0] = 1;    // flag
            payload[o + 1] = c.R;
            payload[o + 2] = c.G;
            payload[o + 3] = c.B;
            o += 4;
        }

        return payload;
    }

    // -------------------------------------------------
    // Streams (order = loop-major then cel-major)
    // -------------------------------------------------
    private sealed class FrameSlice
    {
        public int ControlStart;
        public int RowTableStart;
        public int DataStart;
    }

    private sealed class StreamsAggregate
    {
        public byte[] Control;
        public byte[] RowTables;
        public byte[] Data;
        public readonly List<FrameSlice> Slices = new List<FrameSlice>();
    }

    private static StreamsAggregate BuildStreams(NormalizedView nv, byte transparentIndex)
    {
        List<byte> controlAll = new List<byte>();
        List<byte> rowAll = new List<byte>();
        List<byte> dataAll = new List<byte>();

        StreamsAggregate agg = new StreamsAggregate();

        for (int li = 0; li < nv.LoopCount; li++)
        {
            if (nv.IsMirror[li]) continue;

            for (int ci = 0; ci < nv.CelCount; ci++)
            {
                V56ImageLoop img = nv.Frames[li][ci];
                FrameSlice s = EncodeOneFrame(img, transparentIndex, controlAll, rowAll, dataAll);
                agg.Slices.Add(s);
            }
        }

        agg.Control = controlAll.ToArray();
        agg.RowTables = rowAll.ToArray();
        agg.Data = dataAll.ToArray();
        return agg;
    }

    private static FrameSlice EncodeOneFrame(V56ImageLoop img, byte transparentIndex, List<byte> controlAll, List<byte> rowAll, List<byte> dataAll)
    {
        if (img.Indices == null) throw new InvalidOperationException("Image not indexed.");

        int controlStart = controlAll.Count;
        int rowStart = rowAll.Count;
        int dataStart = dataAll.Count;

        int w = img.Width;
        int h = img.Height;
        byte[] idx = img.Indices;

        // IMPORTANT:
        // FotoSCIhop LINES table values are offsets RELATIVE to the start of this cel’s
        // control stream (cp) and pack/data stream (dp), not absolute file offsets.
        uint[] rowControl = new uint[h];
        uint[] rowData = new uint[h];

        for (int y = 0; y < h; y++)
        {
            rowControl[y] = (uint)(controlAll.Count - controlStart);
            rowData[y] = (uint)(dataAll.Count - dataStart);
            EncodeRow(idx, w, y, transparentIndex, controlAll, dataAll);
        }

        for (int y = 0; y < h; y++) WriteU32ToList(rowAll, rowControl[y]);
        for (int y = 0; y < h; y++) WriteU32ToList(rowAll, rowData[y]);

        FrameSlice s = new FrameSlice();
        s.ControlStart = controlStart;
        s.RowTableStart = rowStart;
        s.DataStart = dataStart;
        return s;
    }
    private static void EncodeRow(byte[] idx, int w, int y, byte transparentIndex, List<byte> controlAll, List<byte> dataAll)
    {
        int rowStart = y * w;
        int x = 0;

        while (x < w)
        {
            byte v = idx[rowStart + x];

            // Transparent run
            if (v == transparentIndex)
            {
                int n = 1;
                while (x + n < w && n < 0x3F && idx[rowStart + x + n] == transparentIndex) n++;
                controlAll.Add((byte)(0xC0 + n));
                x += n;
                continue;
            }

            // Solid-color run (only worth it if >= 3; matches common heuristics and avoids bloating)
            int run = 1;
            while (x + run < w && run < 0x3F && idx[rowStart + x + run] == v) run++;

            if (run >= 3)
            {
                controlAll.Add((byte)(0x80 + run));
                dataAll.Add(v);
                x += run;
                continue;
            }

            // Literal block (max 0x3F). Stop early if we see an upcoming compressible run.
            int litStart = x;
            int litLen = 1;

            while (litStart + litLen < w && litLen < 0x3F)
            {
                byte c = idx[rowStart + litStart + litLen];

                // stop before transparent
                if (c == transparentIndex) break;

                // stop before a solid run >= 3
                if (litStart + litLen + 2 < w)
                {
                    byte c1 = idx[rowStart + litStart + litLen];
                    byte c2 = idx[rowStart + litStart + litLen + 1];
                    byte c3 = idx[rowStart + litStart + litLen + 2];
                    if (c1 == c2 && c2 == c3) break;
                }

                litLen++;
            }

            controlAll.Add((byte)litLen);
            for (int i = 0; i < litLen; i++)
                dataAll.Add(idx[rowStart + litStart + i]);

            x += litLen;
        }
    }
    private static void WriteU32ToList(List<byte> dst, uint v)
    {
        dst.Add((byte)(v & 0xFF));
        dst.Add((byte)((v >> 8) & 0xFF));
        dst.Add((byte)((v >> 16) & 0xFF));
        dst.Add((byte)((v >> 24) & 0xFF));
    }

    // -------------------------------------------------
    // Headers + layout
    // -------------------------------------------------
    private sealed class HeaderPack
    {
        public byte[] LoopHeaders;
        public byte[] CelHeaders;
        public int[] LoopFirstCelHeaderIndex;

        public int ViewHeaderTotalLen; // 2 + VIEW_HEADER_SIZE_A
    }

    private static HeaderPack BuildHeaders(NormalizedView nv, byte transparentIndex, V56EncodeOptions opt)
    {
        byte[] loopHeaders = new byte[nv.LoopCount * LOOP_HEADER_SIZE];

        int nonMirrorLoops = 0;
        for (int li = 0; li < nv.LoopCount; li++) if (!nv.IsMirror[li]) nonMirrorLoops++;

        int totalCelHeaders = nonMirrorLoops * nv.CelCount;
        byte[] celHeaders = new byte[totalCelHeaders * CEL_HEADER_SIZE];

        int[] loopFirst = new int[nv.LoopCount];
        for (int i = 0; i < loopFirst.Length; i++) loopFirst[i] = -1;

        int celHeaderIndex = 0;
        int celWrite = 0;

        for (int li = 0; li < nv.LoopCount; li++)
        {
            if (nv.IsMirror[li])
            {
                WriteLoopHeader(loopHeaders, li, 0, (sbyte)nv.MirrorOf[li], 0);
                continue;
            }

            loopFirst[li] = celHeaderIndex;
            WriteLoopHeader(loopHeaders, li, (byte)nv.CelCount, (sbyte)-1, 0);

            for (int ci = 0; ci < nv.CelCount; ci++)
            {
                V56ImageLoop img = nv.Frames[li][ci];
                byte[] ch = BuildCelHeader52(img.Width, img.Height, img.XHot, img.YHot, transparentIndex, DEFAULT_CEL_FLAGS);
                Buffer.BlockCopy(ch, 0, celHeaders, celWrite, CEL_HEADER_SIZE);
                celWrite += CEL_HEADER_SIZE;
                celHeaderIndex++;
            }
        }

        HeaderPack hp = new HeaderPack();
        hp.LoopHeaders = loopHeaders;
        hp.CelHeaders = celHeaders;
        hp.LoopFirstCelHeaderIndex = loopFirst;
        hp.ViewHeaderTotalLen = 2 + GetViewHeaderSize(opt);
        return hp;
    }

    // This writes the view header: u16 viewHeaderSize + that many bytes
    private static byte[] BuildViewHeader(V56EncodeOptions opt, ushort loopCount, uint palettePayloadOffset)
    {
        ushort vhs = GetViewHeaderSize(opt);
        byte[] b = new byte[2 + vhs];
        WriteU16(b, 0x00, vhs);     // viewHeaderSize (bytes after this u16 size field)
        WriteU16(b, 0x02, loopCount);            // loopCount
        WriteU16(b, 0x04, opt.HeaderFlagsA);     // unknown/flags
        WriteU16(b, 0x06, opt.HeaderFlagsB);     // unknown/flags
        WriteU32(b, 0x08, palettePayloadOffset); // palette payload ABS (includes +6)
        WriteU16(b, 0x0C, opt.Magic3410);        // 0x3410
        WriteU16(b, 0x0E, opt.ResX);             // 0x0280
        WriteU16(b, 0x10, opt.ResY);             // 0x01E0
        if (vhs == VIEW_HEADER_SIZE_A)
            WriteU16(b, 0x12, opt.Magic0084);        // trailing field (usually 0x0084)
        return b;
    }

    private static void WriteLoopHeader(byte[] loopHeaders, int loopIndex, byte numCels, sbyte mirrorOf, uint celTableOffsetAbs)
    {
        int o = loopIndex * LOOP_HEADER_SIZE;

        bool isMirror = (numCels == 0) && (mirrorOf >= 0);

        if (isMirror)
        {
            unchecked { loopHeaders[o + 0x00] = (byte)mirrorOf; }
            loopHeaders[o + 0x01] = 0x01;
            loopHeaders[o + 0x02] = 0x00;
            loopHeaders[o + 0x03] = 0xFF;
        }
        else
        {
            loopHeaders[o + 0x00] = 0xFF;
            loopHeaders[o + 0x01] = 0x00;
            loopHeaders[o + 0x02] = numCels;
            loopHeaders[o + 0x03] = 0xFF;
        }

        WriteU32(loopHeaders, o + 0x04, 0x03FFFFFFu);
        WriteU32(loopHeaders, o + 0x08, 0u);
        WriteU32(loopHeaders, o + 0x0C, celTableOffsetAbs); // 0 at first; patched later
    }


    private static byte[] BuildCelHeader52(int w, int h, short xHot, short yHot, byte transparentIndex, byte flags)
    {
        byte[] b = new byte[CEL_HEADER_SIZE];
        WriteU16(b, 0x00, (ushort)w);
        WriteU16(b, 0x02, (ushort)h);
        WriteS16(b, 0x04, xHot);
        WriteS16(b, 0x06, yHot);
        b[0x08] = transparentIndex;
        b[0x09] = flags;
        return b;
    }

    private sealed class Layout
    {
        public uint PalettePayloadOffset;
        public uint ImagePayloadOffset;
        public uint RowTableOffset;
        public uint DataOffset;
    }

    private static Layout ComputeLayout(int viewHeaderTotalLen, int loopHeadersLen, int celHeadersLen,
                                    int palChunkLen, int controlLen, int rowTablesLen, int dataLen)
    {
        // Body layout:
        //   [view header][loop headers][cel headers]
        //   0x0300 + palette payload
        //   0x0400 + (control+data payload)
        //   0x0500 + 0
        //   [rowTables]
        //   0x0600 + 0

        int palTagStart = viewHeaderTotalLen + loopHeadersLen + celHeadersLen;
        uint palPayload = (uint)(palTagStart + 6);

        int imgTagStart = palTagStart + palChunkLen;
        uint imgPayload = (uint)(imgTagStart + 6);

        // DATA base is immediately after CONTROL inside the 0x0400 payload
        uint dataBaseAbs = imgPayload + (uint)controlLen;

        // 0x0500 tag starts immediately after the 0x0400 payload (control+data)
        uint tag0500Start = imgPayload + (uint)(controlLen + dataLen);
        uint rowAbs = tag0500Start + 6;

        Layout l = new Layout();
        l.PalettePayloadOffset = palPayload;
        l.ImagePayloadOffset = imgPayload; // CONTROL base
        l.DataOffset = dataBaseAbs;        // DATA base (name kept for minimal changes)
        l.RowTableOffset = rowAbs;
        return l;
    }

    private static void PatchLoopCelTableOffsets(HeaderPack headers, int viewHeaderLen)
    {
        int celHeadersStart = viewHeaderLen + headers.LoopHeaders.Length;

        // Non-mirror loops point at their first cel header (ABS from start-of-body).
        for (int li = 0; li < headers.LoopFirstCelHeaderIndex.Length; li++)
        {
            int first = headers.LoopFirstCelHeaderIndex[li];
            if (first >= 0)
            {
                uint celTableAbs = (uint)(celHeadersStart + first * CEL_HEADER_SIZE);
                WriteU32(headers.LoopHeaders, li * LOOP_HEADER_SIZE + 0x0C, celTableAbs);
            }
        }

        // Mirror loops: keep celTableAbs monotonic (many viewers derive ranges from offsets).
        // Set to the next NON-mirror loop's celTableAbs, or end-of-cel-headers if none.
        int endOfCelHeaders = celHeadersStart + headers.CelHeaders.Length;

        for (int li = 0; li < headers.LoopFirstCelHeaderIndex.Length; li++)
        {
            int o = li * LOOP_HEADER_SIZE;

            bool isMirror = (headers.LoopHeaders[o + 0x01] == 0x01);
            if (!isMirror) continue;

            int nextReal = -1;
            for (int j = li + 1; j < headers.LoopFirstCelHeaderIndex.Length; j++)
            {
                if (headers.LoopFirstCelHeaderIndex[j] >= 0)
                {
                    nextReal = j;
                    break;
                }
            }

            uint celTableAbs = (uint)endOfCelHeaders;
            if (nextReal >= 0)
            {
                int first = headers.LoopFirstCelHeaderIndex[nextReal];
                celTableAbs = (uint)(celHeadersStart + first * CEL_HEADER_SIZE);
            }

            WriteU32(headers.LoopHeaders, o + 0x0C, celTableAbs);
        }
    }
    private static void PatchCelHeaderOffsets(byte[] celHeaders, StreamsAggregate streams, Layout layout)
    {
        if (streams.Slices.Count * CEL_HEADER_SIZE != celHeaders.Length)
            throw new InvalidOperationException("Cel header count mismatch to encoded frame count.");

        for (int i = 0; i < streams.Slices.Count; i++)
        {
            FrameSlice s = streams.Slices[i];
            int baseOff = i * CEL_HEADER_SIZE;

            int nextControlStart = (i + 1 < streams.Slices.Count) ? streams.Slices[i + 1].ControlStart : streams.Control.Length;
            int nextDataStart = (i + 1 < streams.Slices.Count) ? streams.Slices[i + 1].DataStart : streams.Data.Length;

            int controlLen = nextControlStart - s.ControlStart;
            int dataLen = nextDataStart - s.DataStart;

            // FotoSCIhop-compatible counts (CelBase layout)
            // dataByteCount = control + data(pack)
            WriteU32(celHeaders, baseOff + 0x0C, (uint)(controlLen + dataLen));
            WriteU32(celHeaders, baseOff + 0x10, (uint)controlLen);

            // Absolute offsets (these you already do)
            uint controlAbs = layout.ImagePayloadOffset + (uint)s.ControlStart;
            uint rowAbs = layout.RowTableOffset + (uint)s.RowTableStart;
            uint dataAbs = layout.DataOffset + (uint)s.DataStart;

            WriteU32(celHeaders, baseOff + 0x18, controlAbs);
            WriteU32(celHeaders, baseOff + 0x1C, dataAbs);
            WriteU32(celHeaders, baseOff + 0x20, rowAbs);
        }
    }

    // -------------------------------------------------
    // Chunks
    // -------------------------------------------------
    private static byte[] BuildCompPalChunk(byte[] compPalPayload)
    {
        byte[] head = new byte[6];
        WriteU16(head, 0, 0x0300);
        WriteU32(head, 2, (uint)compPalPayload.Length);
        return Concat(head, compPalPayload);
    }

    private static byte[] BuildImageSection(byte[] controlStream, byte[] dataStream, byte[] rowTables)
    {
        if (controlStream == null) controlStream = new byte[0];
        if (dataStream == null) dataStream = new byte[0];
        if (rowTables == null) rowTables = new byte[0];

        int len0400 = controlStream.Length + dataStream.Length;

        // Layout:
        //   0x0400 + len0400 + [control][data]
        //   0x0500 + 0
        //   [rowTables]
        //   0x0600 + 0
        byte[] b = new byte[6 + len0400 + 6 + rowTables.Length + 6];

        int p = 0;

        WriteU16(b, p + 0, 0x0400);
        WriteU32(b, p + 2, (uint)len0400);
        p += 6;

        if (controlStream.Length > 0)
        {
            Buffer.BlockCopy(controlStream, 0, b, p, controlStream.Length);
            p += controlStream.Length;
        }

        if (dataStream.Length > 0)
        {
            Buffer.BlockCopy(dataStream, 0, b, p, dataStream.Length);
            p += dataStream.Length;
        }

        WriteU16(b, p + 0, 0x0500);
        WriteU32(b, p + 2, 0u);
        p += 6;

        if (rowTables.Length > 0)
        {
            Buffer.BlockCopy(rowTables, 0, b, p, rowTables.Length);
            p += rowTables.Length;
        }

        WriteU16(b, p + 0, 0x0600);
        WriteU32(b, p + 2, 0u);
        return b;
    }


    // -------------------------------------------------
    // Median-cut quantizer
    // -------------------------------------------------
    private sealed class Box
    {
        public List<int> Colors;
        public int RMin, RMax, GMin, GMax, BMin, BMax;
    }

    private static Color[] QuantizeMedianCut(List<int> uniqueRgb, int targetCount)
    {
        List<Box> boxes = new List<Box>();
        Box root = new Box();
        root.Colors = new List<int>(uniqueRgb);
        ComputeBounds(root);
        boxes.Add(root);

        while (boxes.Count < targetCount)
        {
            int pick = -1;
            int bestRange = -1;

            for (int i = 0; i < boxes.Count; i++)
            {
                Box b = boxes[i];
                int r = b.RMax - b.RMin;
                int g = b.GMax - b.GMin;
                int bb = b.BMax - b.BMin;
                int range = r;
                if (g > range) range = g;
                if (bb > range) range = bb;

                if (range > bestRange && b.Colors.Count > 1)
                {
                    bestRange = range;
                    pick = i;
                }
            }
            if (pick < 0) break;

            Box box = boxes[pick];

            int rRange = box.RMax - box.RMin;
            int gRange = box.GMax - box.GMin;
            int bRange = box.BMax - box.BMin;

            if (rRange >= gRange && rRange >= bRange)
                box.Colors.Sort(CompareR);
            else if (gRange >= rRange && gRange >= bRange)
                box.Colors.Sort(CompareG);
            else
                box.Colors.Sort(CompareB);

            int mid = box.Colors.Count / 2;
            if (mid <= 0 || mid >= box.Colors.Count) break;

            Box b1 = new Box();
            Box b2 = new Box();
            b1.Colors = box.Colors.GetRange(0, mid);
            b2.Colors = box.Colors.GetRange(mid, box.Colors.Count - mid);
            ComputeBounds(b1);
            ComputeBounds(b2);

            boxes[pick] = b1;
            boxes.Add(b2);
        }

        List<Color> outPal = new List<Color>();
        for (int i = 0; i < boxes.Count && outPal.Count < targetCount; i++)
        {
            Box b = boxes[i];
            long rs = 0, gs = 0, bs = 0;
            for (int k = 0; k < b.Colors.Count; k++)
            {
                int rgb = b.Colors[k];
                rs += (rgb >> 16) & 0xFF;
                gs += (rgb >> 8) & 0xFF;
                bs += rgb & 0xFF;
            }
            int n = b.Colors.Count;
            if (n == 0) continue;
            outPal.Add(Color.FromArgb((int)(rs / n), (int)(gs / n), (int)(bs / n)));
        }

        while (outPal.Count < targetCount) outPal.Add(Color.Black);
        return outPal.ToArray();
    }

    private static void ComputeBounds(Box b)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        for (int i = 0; i < b.Colors.Count; i++)
        {
            int rgb = b.Colors[i];
            int r = (rgb >> 16) & 0xFF;
            int g = (rgb >> 8) & 0xFF;
            int bb = rgb & 0xFF;
            if (r < rMin) rMin = r;
            if (r > rMax) rMax = r;
            if (g < gMin) gMin = g;
            if (g > gMax) gMax = g;
            if (bb < bMin) bMin = bb;
            if (bb > bMax) bMax = bb;
        }
        b.RMin = rMin; b.RMax = rMax;
        b.GMin = gMin; b.GMax = gMax;
        b.BMin = bMin; b.BMax = bMax;
    }

    private static int CompareR(int a, int b) { return ((a >> 16) & 0xFF) - ((b >> 16) & 0xFF); }
    private static int CompareG(int a, int b) { return ((a >> 8) & 0xFF) - ((b >> 8) & 0xFF); }
    private static int CompareB(int a, int b) { return (a & 0xFF) - (b & 0xFF); }

    // -------------------------------------------------
    // Bitmap -> RGBA (deterministic)
    // -------------------------------------------------
    private static int[] BitmapToRgba(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int[] rgba = new int[w * h];

        Rectangle rect = new Rectangle(0, 0, w, h);
        using (Bitmap clone = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(clone))
            {
                g.DrawImage(bmp, 0, 0, w, h);
            }

            BitmapData bd = clone.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] buf = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);

                int di = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int p = row + x * 4;
                        byte b = buf[p + 0];
                        byte g2 = buf[p + 1];
                        byte r = buf[p + 2];
                        byte a = buf[p + 3];
                        rgba[di++] = (a << 24) | (r << 16) | (g2 << 8) | b;
                    }
                }
            }
            finally
            {
                clone.UnlockBits(bd);
            }
        }

        return rgba;
    }

    // -------------------------------------------------
    // Byte helpers
    // -------------------------------------------------
    private static byte[] Concat(byte[] a, byte[] b)
    {
        if (a == null) return b;
        if (b == null) return a;
        byte[] r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o + 0] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteS16(byte[] b, int o, short v)
    {
        unchecked
        {
            b[o + 0] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o + 0] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF);
        b[o + 3] = (byte)((v >> 24) & 0xFF);
    }
}
