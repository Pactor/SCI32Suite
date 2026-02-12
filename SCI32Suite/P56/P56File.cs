using SCI32Suite.Palette;
using SCI32Suite.Utils;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SCI32Suite.P56
{
    /// <summary>
    /// High-level representation of a P56 picture file.
    /// - Load existing P56 (header + CompPal + indexed pixels) -> Bitmap
    /// - Export loaded bitmap to PNG/BMP/etc.
    /// - Save any Bitmap as P56 (quantizes if needed) using CompPal palette block.
    /// </summary>
    public sealed class P56File
    {
        public P56Header Header { get; private set; }
        public PaletteData Palette { get; private set; }
        public Bitmap Image { get; set; }

        public P56File() { }

        // ============================================================
        // LOADING (header + palette + raw 8bpp indices)
        // ============================================================
        public static P56File Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            // Read whole file (converter expects random access to header/blocks)
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 64)
                throw new InvalidDataException("File too small to be a valid SCI32 P56.");

            // -------------------------------
            // 0) Validate SCI32 P56 header
            // -------------------------------
            // Must start with 0x00008181 (little endian -> bytes: 81 81 00 00)
            int pictureType = BitConverter.ToInt32(data, 0);
            if (pictureType != 0x00008181)
                throw new InvalidDataException("Not a valid SCI32 P56 (missing 0x81 0x81 tag).");

            // Cell table offset (always 14 in known good files)
            short cellTableOffset = BitConverter.ToInt16(data, 4);
            if (cellTableOffset != 14)
                throw new InvalidDataException("Unexpected cell table offset; not a standard SCI32 P56.");

            // Palette offset (converter checks for 62)
            int paletteOffset = BitConverter.ToInt32(data, 10);
            if (paletteOffset != 62)
                throw new InvalidDataException("Invalid palette offset; this file does not match expected SCI32 P56 layout.");
            // Canvas size (some files store canvas here; do NOT use for pixelCount)
            ushort canvasW = BitConverter.ToUInt16(data, 14);
            ushort canvasH = BitConverter.ToUInt16(data, 16);
            if (canvasW == 0 || canvasH == 0)
                throw new InvalidDataException("Invalid canvas dimensions in P56.");

            // In many SCI32 P56 files, the actual cel header begins at cellTableOffset (14)
            // and stores celW/celH right after canvasW/canvasH.
            int celHeader = cellTableOffset;

            ushort width = BitConverter.ToUInt16(data, celHeader + 4);  // celW
            ushort height = BitConverter.ToUInt16(data, celHeader + 6);  // celH

            int dataOffset = BitConverter.ToInt32(data, celHeader + 28); // NOT +36
            int dataLen = BitConverter.ToInt32(data, dataOffset);
            int imageOffset = dataOffset + 4;

            int pixelCount = checked((int)width * (int)height);
            if (dataLen < pixelCount) throw new InvalidDataException("P56 pixel data shorter than width*height");
            if (imageOffset < 0 || imageOffset + pixelCount > data.Length) throw new InvalidDataException("Invalid image data offset/length in P56");

            // ----------------------------------------
            // 1) Read palette (exactly like converter)
            // ----------------------------------------
            // Palette block layout (from converter):
            // palOff
            //  + 4   // Length
            //  + 2   // Type
            //  + 11  // reserved
            //  + 2   // DataLen
            //  + 10  // reserved
            //  + 2   // FirstColor
            //  + 2   // Unknown1
            //  + 2   // NumColors
            //  + 1   // ExFour
            //  + 1   // Triple
            //  + 4;  // reserved
            int palBase = paletteOffset;

            int palPtr = palBase + 41;

            ushort firstColor = BitConverter.ToUInt16(data, palBase + 29);
            ushort numColors = BitConverter.ToUInt16(data, palBase + 33);
            byte exFour = data[palBase + 35];
            byte triple = data[palBase + 36];

            // triple!=0 => 3 bytes (RGB). Otherwise 4 bytes ([flag,R,G,B]) when exFour!=0.
            int bytesPer = (triple != 0) ? 3 : 4;

            // Validate range
            int needed = palPtr + (numColors * bytesPer);
            if (palPtr < 0 || needed > data.Length)
                throw new InvalidDataException("Palette block out of range in P56.");

            // Build full 256 palette initialized to black
            var pal256 = new System.Drawing.Color[256];
            for (int i = 0; i < 256; i++) pal256[i] = System.Drawing.Color.Black;

            int p = palPtr;
            for (int i = 0; i < numColors; i++)
            {
                int idx = firstColor + i;
                if (idx < 0 || idx > 255) { p += bytesPer; continue; }

                if (bytesPer == 4)
                {
                    byte flag = data[p++]; // ignore
                    byte r = data[p++], g = data[p++], b = data[p++];
                    pal256[idx] = System.Drawing.Color.FromArgb(r, g, b);
                }
                else
                {
                    byte r = data[p++], g = data[p++], b = data[p++];
                    pal256[idx] = System.Drawing.Color.FromArgb(r, g, b);
                }
            }

            // Also populate PaletteData for the suite (remap = 0)
            var paletteData = PaletteData.CreateDefault();
            for (int i = 0; i < 256; i++)
                paletteData.SetColor(i, pal256[i].R, pal256[i].G, pal256[i].B, 0);

            // ----------------------------------------
            // 2) Read pixels and create 8bpp bitmap
            // ----------------------------------------
            var indices = new byte[pixelCount];
            Buffer.BlockCopy(data, imageOffset, indices, 0, pixelCount);

            var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format8bppIndexed);

            // Apply 256-color palette
            var pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = pal256[i];
            bmp.Palette = pal;

            // Copy indices row-by-row (no unsafe)
            var rect = new System.Drawing.Rectangle(0, 0, width, height);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format8bppIndexed);
            try
            {
                int stride = bd.Stride;
                IntPtr scan0 = bd.Scan0;
                for (int y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(indices, y * width, scan0 + y * stride, width);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            // ----------------------------------------
            // 3) Fill a header object (for UI/debug)
            // ----------------------------------------
            var header = new P56Header
            {
                Signature = new byte[] { 0x81, 0x81 },
                Width = width,
                Height = height,
                PaletteOffset = (uint)paletteOffset,
                ImageOffset = (uint)imageOffset,
                Reserved = new byte[62 - 2 - 2 - 2 - 4 - 4]
            };

            return new P56File
            {
                Header = header,
                Palette = paletteData,
                Image = bmp
            };
        }


        // ============================================================
        // EXPORTING the loaded bitmap to standard image (PNG by default)
        // ============================================================
        public void ExportImage(string outputPath, ImageFormat format)
        {
            if (Image == null) throw new InvalidOperationException("No image loaded.");
            if (format == null) format = ImageFormat.Png; // C# 7.3 compatible
            Image.Save(outputPath, format);
        }
        public static void SaveImageAsP56(System.Drawing.Image source, string outPath, bool fillFrame = true)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentNullException(nameof(outPath));

            const int MAX_W = 640;
            const int MAX_H = 480;
            int CONTROL_OFFSET = 0;

            using (var srcBmp = new System.Drawing.Bitmap(source))
            {
                bool fits = srcBmp.Width <= MAX_W && srcBmp.Height <= MAX_H;
                bool is8bpp = srcBmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format8bppIndexed;

                // ---------- FAST PATH (no pixel modification): only when not filling ----------
                if (!fillFrame && fits && is8bpp)
                {
                    int w = srcBmp.Width, h = srcBmp.Height;

                    // read 8bpp indices honoring stride (no unsafe)
                    var rect = new System.Drawing.Rectangle(0, 0, w, h);
                    var bd = srcBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, srcBmp.PixelFormat);
                    byte[] srcRaw = new byte[bd.Stride * h];
                    try { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, srcRaw, 0, srcRaw.Length); }
                    finally { srcBmp.UnlockBits(bd); }

                    // 640x480 canvas filled with 255 (transparent), then center-blit source rows
                    byte[] pixels = new byte[MAX_W * MAX_H];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;

                    int ox = (MAX_W - w) / 2;
                    int oy = (MAX_H - h) / 2;

                    int maxIdx = 0;
                    for (int y = 0; y < h; y++)
                    {
                        int srcOff = y * bd.Stride;
                        int dstOff = (oy + y) * MAX_W + ox;
                        System.Buffer.BlockCopy(srcRaw, srcOff, pixels, dstOff, w);
                        for (int x = 0; x < w; x++)
                        {
                            int v = pixels[dstOff + x];
                            if (v > maxIdx) maxIdx = v;
                        }
                    }
                    if (maxIdx > 254)
                        throw new InvalidOperationException("8bpp image uses palette index 255; cannot pack without altering data.");

                    // build palette data from source palette (first 255 entries)
                    var entries = srcBmp.Palette.Entries;
                    if (entries.Length < 255) throw new InvalidOperationException("8bpp image palette has fewer than 255 entries.");
                    var palList = new System.Collections.Generic.List<System.Drawing.Color>(256);
                    palList.AddRange(entries);
                    var pal = PaletteData.FromRgbList(palList);

                    using (var fs = new System.IO.FileStream(outPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                    using (var bw = new System.IO.BinaryWriter(fs))
                    {
                        // Resource tag
                        bw.Write(new byte[] { 0x81, 0x81, 0x00, 0x00 });

                        // PicHeader32 (full frame)
                        var pic = new PicHeader32 { picHeaderSize = 14, celCount = 1, splitFlag = 0, celHeaderSize = 42, paletteOffset = 62, resX = (ushort)MAX_W, resY = (ushort)MAX_H };
                        BinaryUtil.WriteStruct(bw, pic);

                        // CelHeaderPic (full frame)
                        var cel = new CelHeaderPic { W = (ushort)MAX_W, H = (ushort)MAX_H, xShift = 0, yShift = 0, transparent = 255, compressType = 0, dataFlags = 0, dataByteCount = MAX_W * MAX_H, controlByteCount = 0, paletteOffsetCell = 0, controlOffset = CONTROL_OFFSET, colorOffset = 0, rowTableOffset = 0, priority = 0, xpos = 0, ypos = 0 };
                        BinaryUtil.WriteStruct(bw, cel);

                        // 2-byte pad to paletteOffset=62
                        bw.Write((byte)0x00); bw.Write((byte)0x00);

                        // Legacy wrapper + PalHeader + index list
                        bw.Write((ushort)0x0322); bw.Write((ushort)0x0000);
                        bw.Write((byte)14); WriteFixedAnsi(bw, "GENPAL", 9); bw.Write((byte)1); bw.Write((short)0);
                        bw.Write((ushort)0);

                        // CompPal header (known-good values)
                        bw.Write(new byte[] { 0x00, 0x34, 0x00, 0x00, 0x02, 0x00, 0x5A, 0x0E, 0x00, 0x00 });
                        bw.Write((byte)0); bw.Write((byte)0); bw.Write((ushort)0);     // firstColor
                        bw.Write((ushort)255);   // numColors
                        bw.Write((byte)1);       // exFour
                        bw.Write((byte)1);       // triple = 1 => 3-byte entries
                        bw.Write(0x00000000);

                        // 255×RGB palette (sv.exe expects this)
                        WriteRgbTriplets(bw, pal, 255);

                        bw.Write((ushort)0); bw.Write((ushort)255);
                        bw.Write((byte)1); bw.Write((byte)1);
                        bw.Write(0x00000000);

                        // 255×RGB palette
                        WriteRgbTriplets(bw, pal, 255);
                        
                        // Pixel block starts right here (NO padding)
                        int pixelCount = MAX_W * MAX_H;

                        int pixelBlockOffset = (int)fs.Position;

                        // Patch the cel header dword at (celHeader+28).
                        // celHeader starts at 14, so celHeader+28 = 42.
                        long returnPos = fs.Position;
                        fs.Position = 14 + 28;
                        bw.Write(pixelBlockOffset);
                        fs.Position = returnPos;

                        // Write length-prefixed pixels
                        bw.Write(pixelCount); // int32 dataLen
                        bw.Write(pixels);
                        bw.Flush();

                    }
                    return;
                }

                // ---------- SLOW PATHS: we will produce a full 640×480 frame ----------
                if (fillFrame)
                {
                    // COVER / FILL: scale to cover 640×480, center-crop (allows upscaling)
                    using (var frameRGB = new System.Drawing.Bitmap(MAX_W, MAX_H))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(frameRGB))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                            double sx = (double)MAX_W / srcBmp.Width;
                            double sy = (double)MAX_H / srcBmp.Height;
                            double s = Math.Max(sx, sy); // cover

                            int scaledW = (int)System.Math.Round(srcBmp.Width * s);
                            int scaledH = (int)System.Math.Round(srcBmp.Height * s);
                            int dx = (MAX_W - scaledW) / 2;
                            int dy = (MAX_H - scaledH) / 2;

                            // draw scaled, centered; any overflow is cropped by the canvas
                            g.DrawImage(srcBmp, new System.Drawing.Rectangle(dx, dy, scaledW, scaledH));
                        }

                        // Quantize full frame to 256, map to indices 0..254
                        var palList = MedianCutQuantizer.ExtractPalette(frameRGB, 256, dedupe: true, mergeRadius: 6);
                        var pal = PaletteData.FromRgbList(palList);
                        byte[] pixels = MapToPalette(frameRGB, pal); // 0..254 only

                        using (var fs = new System.IO.FileStream(outPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                        using (var bw = new System.IO.BinaryWriter(fs))
                        {
                            // Resource tag
                            bw.Write(new byte[] { 0x81, 0x81, 0x00, 0x00 });

                            // PicHeader32 (full frame)
                            var pic = new PicHeader32 { picHeaderSize = 14, celCount = 1, splitFlag = 0, celHeaderSize = 42, paletteOffset = 62, resX = (ushort)MAX_W, resY = (ushort)MAX_H };
                            BinaryUtil.WriteStruct(bw, pic);

                            // CelHeaderPic (full frame)
                            var cel = new CelHeaderPic { W = (ushort)MAX_W, H = (ushort)MAX_H, xShift = 0, yShift = 0, transparent = 255, compressType = 0, dataFlags = 0, dataByteCount = MAX_W * MAX_H, controlByteCount = 0, paletteOffsetCell = 0, controlOffset = CONTROL_OFFSET, colorOffset = 0, rowTableOffset = 0, priority = 0, xpos = 0, ypos = 0 };
                            BinaryUtil.WriteStruct(bw, cel);

                            // 2-byte pad to paletteOffset=62
                            bw.Write((byte)0x00); bw.Write((byte)0x00);

                            // Legacy wrapper + PalHeader + index list
                            bw.Write((ushort)0x0322); bw.Write((ushort)0x0000);
                            bw.Write((byte)14); WriteFixedAnsi(bw, "GENPAL", 9); bw.Write((byte)1); bw.Write((short)0);
                            bw.Write((ushort)0);

                            // CompPal header
                            bw.Write(new byte[] { 0x00, 0x34, 0x00, 0x00, 0x02, 0x00, 0x5A, 0x0E, 0x00, 0x00 });
                            bw.Write((byte)0); bw.Write((byte)0);
                            bw.Write((ushort)0);     // firstColor
                            bw.Write((ushort)255);   // numColors
                            bw.Write((byte)1);       // exFour
                            bw.Write((byte)1);       // triple = 1 => 3-byte entries
                            bw.Write(0x00000000);

                            // 255×RGB palette (sv.exe expects this)
                            WriteRgbTriplets(bw, pal, 255);

                            int pixelCount = MAX_W * MAX_H;

                            // Pixel block starts here
                            int pixelBlockOffset = (int)fs.Position;

                            // Patch celHeader+28 (14+28=42) with pixelBlockOffset
                            long returnPos = fs.Position;
                            fs.Position = 14 + 28;
                            bw.Write(pixelBlockOffset);
                            fs.Position = returnPos;

                            // Write [len][pixels]
                            bw.Write(pixelCount);
                            bw.Write(pixels);
                        }
                    }
                }
                else
                {
                    // CONTAIN / FIT (no upscaling): same as earlier slow path, then center into 640×480 with background=255
                    using (var fitted = ResizeToFit(srcBmp, MAX_W, MAX_H))
                    {
                        int fw = fitted.Width, fh = fitted.Height;
                        var palList = MedianCutQuantizer.ExtractPalette(fitted, 256, dedupe: true, mergeRadius: 6);
                        var pal = PaletteData.FromRgbList(palList);
                        byte[] srcIdx = MapToPalette(fitted, pal);

                        byte[] pixels = new byte[MAX_W * MAX_H];
                        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
                        int ox = (MAX_W - fw) / 2, oy = (MAX_H - fh) / 2;

                        int k = 0;
                        for (int y = 0; y < fh; y++)
                        {
                            int dstOff = (oy + y) * MAX_W + ox;
                            System.Buffer.BlockCopy(srcIdx, k, pixels, dstOff, fw);
                            k += fw;
                        }

                        using (var fs = new System.IO.FileStream(outPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                        using (var bw = new System.IO.BinaryWriter(fs))
                        {
                            // Resource tag
                            bw.Write(new byte[] { 0x81, 0x81, 0x00, 0x00 });

                            // PicHeader32 (full frame)
                            var pic = new PicHeader32 { picHeaderSize = 14, celCount = 1, splitFlag = 0, celHeaderSize = 42, paletteOffset = 62, resX = (ushort)MAX_W, resY = (ushort)MAX_H };
                            BinaryUtil.WriteStruct(bw, pic);

                            // CelHeaderPic (full frame)
                            var cel = new CelHeaderPic { W = (ushort)MAX_W, H = (ushort)MAX_H, xShift = 0, yShift = 0, transparent = 255, compressType = 0, dataFlags = 0, dataByteCount = MAX_W * MAX_H, controlByteCount = 0, paletteOffsetCell = 0, controlOffset = CONTROL_OFFSET, colorOffset = 0, rowTableOffset = 0, priority = 0, xpos = 0, ypos = 0 };
                            BinaryUtil.WriteStruct(bw, cel);

                            // 2-byte pad to paletteOffset=62
                            bw.Write((byte)0x00); bw.Write((byte)0x00);

                            // Legacy wrapper + PalHeader + index list
                            bw.Write((ushort)0x0322); bw.Write((ushort)0x0000);
                            bw.Write((byte)14); WriteFixedAnsi(bw, "GENPAL", 9); bw.Write((byte)1); bw.Write((short)0);
                            bw.Write((ushort)0);

                            // CompPal header
                            bw.Write(new byte[] { 0x00, 0x34, 0x00, 0x00, 0x02, 0x00, 0x5A, 0x0E, 0x00, 0x00 });
                            bw.Write((byte)0); bw.Write((byte)0);
                            bw.Write((ushort)0);     // firstColor
                            bw.Write((ushort)255);   // numColors
                            bw.Write((byte)1);       // exFour
                            bw.Write((byte)1);       // triple = 1 => 3-byte entries
                            bw.Write(0x00000000);

                            // 255×RGB palette (sv.exe expects this)
                            WriteRgbTriplets(bw, pal, 255);

                            int pixelCount = MAX_W * MAX_H;

                            // Pixel block starts here
                            int pixelBlockOffset = (int)fs.Position;

                            // Patch celHeader+28 (14+28=42) with pixelBlockOffset
                            long returnPos = fs.Position;
                            fs.Position = 14 + 28;
                            bw.Write(pixelBlockOffset);
                            fs.Position = returnPos;

                            // Write [len][pixels]
                            bw.Write(pixelCount);
                            bw.Write(pixels);

                        }
                    }
                }
            }
        }

        private static Bitmap ResizeToFit(Bitmap src, int maxW, int maxH)
        {
            double sx = maxW / (double)src.Width;
            double sy = maxH / (double)src.Height;
            double s = Math.Min(1.0, Math.Min(sx, sy)); // do not upscale
            int w = Math.Max(1, (int)Math.Round(src.Width * s));
            int h = Math.Max(1, (int)Math.Round(src.Height * s));
            Bitmap bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return bmp;
        }

        private static void WriteFixedAnsi(BinaryWriter bw, string s, int len)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(s ?? "");
            Array.Resize(ref bytes, len);
            bw.Write(bytes);
        }

        private static void WriteRgbTriplets(BinaryWriter bw, PaletteData pal, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var c = pal.GetColor(i);
                bw.Write(c.R); bw.Write(c.G); bw.Write(c.B);
            }
        }

        private static byte[] MapToPalette(Bitmap bmp, PaletteData pal)
        {
            byte[] buf = new byte[bmp.Width * bmp.Height];

            // flatten palette to arrays for speed
            byte[] pr = new byte[256], pg = new byte[256], pb = new byte[256];
            for (int i = 0; i < 256; i++) { var c = pal.GetColor(i); pr[i] = c.R; pg[i] = c.G; pb[i] = c.B; }

            int k = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++, k++)
                {
                    Color p = bmp.GetPixel(x, y);
                    int best = 0, bestD = int.MaxValue;

                    // restrict to 0..254 so 255 remains available for transparency
                    for (int i = 0; i < 255; i++)
                    {
                        int dr = p.R - pr[i], dg = p.G - pg[i], db = p.B - pb[i];
                        int d = dr * dr + dg * dg + db * db;
                        if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
                    }
                    buf[k] = (byte)best;
                }
            }
            return buf;
        }
    }
}
