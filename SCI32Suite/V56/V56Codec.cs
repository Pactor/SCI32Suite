
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

public static class V56Encoder
{
    private const ushort VIEW_HEADER_SIZE = 0x0012; // 18 bytes after the u16 size field
    private const ushort LOOP_HEADER_SIZE = 0x0010; // 16
    private const ushort CEL_HEADER_SIZE = 0x0034; // 52

    private const byte TRANSPARENT_INDEX = 255;
    private const byte DEFAULT_CEL_FLAGS = 0x8A;

    public sealed class V56Cel
    {
        public readonly List<V56LoopDef> Loops = new List<V56LoopDef>();
        public short DefaultXHot = 0;
        public short DefaultYHot = 0;

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

    // -------------------------------------------------
    // Encode options (header fields)
    // -------------------------------------------------
    public sealed class V56EncodeOptions
    {
        // These match the 18-byte header fields seen in your sample.
        public ushort HeaderFlagsA = 0x0001; // in your good file sometimes 0x0101
        public ushort HeaderFlagsB = 0x0001;
        public ushort Magic3410 = 0x3410;
        public ushort ResX = 0x0280;
        public ushort ResY = 0x01E0;
        public ushort Magic0084 = 0x0084;
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
        HeaderPack headers = BuildHeaders(nv, pal.TransparentIndex);

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
        byte[] payload = new byte[37 + 256 * 3];

        // Header constants (matches real V56 compal header like 0.v56)
        payload[0] = 0x0E;
        for (int i = 1; i <= 9; i++) payload[i] = 0x20; // spaces
        payload[10] = 0x01;
        payload[13] = 0x66;
        payload[14] = 0x01;

        payload[25] = 0;          // startOffset
        payload[29] = 0;          // nColors = 256 (little-endian)
        payload[30] = 1;
        payload[31] = 0x01;       // seen in real file
        payload[32] = 1;          // palType=1 (RGB triples)

        int p = 37;
        for (int i = 0; i < 256; i++)
        {
            Color c = pal256[i];
            payload[p++] = c.R;
            payload[p++] = c.G;
            payload[p++] = c.B;
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

        uint[] rowControl = new uint[h];
        uint[] rowData = new uint[h];

        for (int y = 0; y < h; y++)
        {
            rowControl[y] = (uint)controlAll.Count;
            rowData[y] = (uint)dataAll.Count;
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

    private static void EncodeRow(byte[] idx, int w, int y, byte transparentIndex, List<byte> control, List<byte> data)
    {
        int rowBase = y * w;
        int x = 0;

        while (x < w)
        {
            byte v = idx[rowBase + x];

            if (v == transparentIndex)
            {
                int run = 1;
                while (x + run < w && run < 63 && idx[rowBase + x + run] == transparentIndex) run++;
                control.Add((byte)(0xC0 | (run & 0x3F)));
                x += run;
                continue;
            }

            int solid = 1;
            while (x + solid < w && solid < 63 && idx[rowBase + x + solid] == v) solid++;
            if (solid >= 2)
            {
                control.Add((byte)(0x80 | (solid & 0x3F)));
                data.Add(v);
                x += solid;
                continue;
            }

            int litStart = x;
            int litLen = 1;
            while (litStart + litLen < w && litLen < 63)
            {
                byte nv2 = idx[rowBase + litStart + litLen];
                if (nv2 == transparentIndex) break;
                if (litStart + litLen + 1 < w && idx[rowBase + litStart + litLen + 1] == nv2) break;
                litLen++;
            }

            control.Add((byte)litLen);
            for (int i = 0; i < litLen; i++)
                data.Add(idx[rowBase + litStart + i]);

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

        public int ViewHeaderTotalLen; // 2 + VIEW_HEADER_SIZE
    }

    private static HeaderPack BuildHeaders(NormalizedView nv, byte transparentIndex)
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
        hp.ViewHeaderTotalLen = 2 + VIEW_HEADER_SIZE;
        return hp;
    }

    // This writes the real header prefix: u16 size + 18 bytes header
    private static byte[] BuildViewHeader(V56EncodeOptions opt, ushort loopCount, uint palettePayloadOffset)
    {
        byte[] b = new byte[2 + VIEW_HEADER_SIZE];
        WriteU16(b, 0x00, VIEW_HEADER_SIZE);     // size (18)
        WriteU16(b, 0x02, loopCount);            // loopCount
        WriteU16(b, 0x04, opt.HeaderFlagsA);     // unknown/flags
        WriteU16(b, 0x06, opt.HeaderFlagsB);     // unknown/flags
        WriteU32(b, 0x08, palettePayloadOffset); // palette payload ABS (includes +6)
        WriteU16(b, 0x0C, opt.Magic3410);        // 0x3410
        WriteU16(b, 0x0E, opt.ResX);             // 0x0280
        WriteU16(b, 0x10, opt.ResY);             // 0x01E0
        WriteU16(b, 0x12, opt.Magic0084);        // 0x0084
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
