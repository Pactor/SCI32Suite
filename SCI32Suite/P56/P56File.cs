using SCI32Suite.Palette;
using SCI32Suite.Utils;      // BinaryUtil(ReadStruct/WriteStruct)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using static SCI32Suite.P56.P56Header;
using static SCI32Suite.Palette.Sci32PaletteConverter;

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

            // Width / Height are at 14/16
            ushort width = (ushort)BitConverter.ToInt16(data, 14);
            ushort height = (ushort)BitConverter.ToInt16(data, 16);
            if (width == 0 || height == 0)
                throw new InvalidDataException("Invalid image dimensions in P56.");

            // Packed (raw) pixel data offset at (14 + 28) = 42 per converter
            int imageOffset = BitConverter.ToInt32(data, 14 + 28);
            int pixelCount = width * height;
            if (imageOffset < 0 || imageOffset + pixelCount > data.Length)
                throw new InvalidDataException("Invalid image data offset/length in P56.");

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
            int palPtr = paletteOffset + 41; // 4+2+11+2+10+2+2+2+1+1+4 = 41

            // The converter reads 255 * 3 (RGB) bytes
            if (palPtr < 0 || palPtr + 255 * 3 > data.Length)
                throw new InvalidDataException("Palette block out of range in P56.");

            // Build 255-color palette from file
            var pal255 = new System.Drawing.Color[255];
            int p = palPtr;
            for (int i = 0; i < 255; i++)
            {
                byte r = data[p++], g = data[p++], b = data[p++];
                pal255[i] = System.Drawing.Color.FromArgb(r, g, b);
            }

            // Expand to 256 by duplicating entry 0 (this mirrors the working code)
            var pal256 = new System.Drawing.Color[256];
            for (int i = 0; i < 255; i++) pal256[i] = pal255[i];
            pal256[255] = pal255.Length > 0 ? pal255[0] : System.Drawing.Color.Black;

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
                // This struct is just for your app; store useful values
                Signature = new byte[] { 0x81, 0x81 }, // informational
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
        public static void SaveImageAsV56(System.Drawing.Image source, string outPath, bool fillFrame = true)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentNullException(nameof(outPath));

            const int MAX_W = 640;
            const int MAX_H = 480;
            const int CONTROL_OFFSET = 870; // 103 + 255*3 + 2 pad

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
                        bw.Write((byte)0); bw.Write((byte)0);
                        bw.Write((ushort)0); bw.Write((ushort)255);
                        bw.Write((byte)1); bw.Write((byte)1);
                        bw.Write(0x00000000);

                        // 255×RGB palette
                        WriteRgbTriplets(bw, pal, 255);

                        // pad to CONTROL_OFFSET
                        long toPad = CONTROL_OFFSET - fs.Position;
                        if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                        if (toPad > 0) bw.Write(new byte[toPad]);

                        // pixels (centered 640×480)
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
                            bw.Write((ushort)0); bw.Write((ushort)255);
                            bw.Write((byte)1); bw.Write((byte)1);
                            bw.Write(0x00000000);

                            // 255×RGB palette
                            WriteRgbTriplets(bw, pal, 255);

                            // pad to CONTROL_OFFSET
                            long toPad = CONTROL_OFFSET - fs.Position;
                            if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                            if (toPad > 0) bw.Write(new byte[toPad]);

                            // pixels (full 640×480 filled)
                            bw.Write(pixels);
                            bw.Flush();
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
                            bw.Write((ushort)0); bw.Write((ushort)255);
                            bw.Write((byte)1); bw.Write((byte)1);
                            bw.Write(0x00000000);

                            // 255×RGB palette
                            WriteRgbTriplets(bw, pal, 255);

                            // pad to CONTROL_OFFSET
                            long toPad = CONTROL_OFFSET - fs.Position;
                            if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                            if (toPad > 0) bw.Write(new byte[toPad]);

                            // pixels (centered 640×480)
                            bw.Write(pixels);
                            bw.Flush();
                        }
                    }
                }
            }
        }

        /* GOod full screen some black dots
        public static void SaveImageAsV56(System.Drawing.Image source, string outPath, bool fillFrame = true)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentNullException(nameof(outPath));

            const int MAX_W = 640;
            const int MAX_H = 480;
            const int CONTROL_OFFSET = 870; // 103 + 255*3 + 2 pad

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
                        bw.Write((byte)0); bw.Write((byte)0);
                        bw.Write((ushort)0); bw.Write((ushort)255);
                        bw.Write((byte)1); bw.Write((byte)1);
                        bw.Write(0x00000000);

                        // 255×RGB palette
                        WriteRgbTriplets(bw, pal, 255);

                        // pad to CONTROL_OFFSET
                        long toPad = CONTROL_OFFSET - fs.Position;
                        if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                        if (toPad > 0) bw.Write(new byte[toPad]);

                        // pixels (centered 640×480)
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
                        var palList = MedianCutQuantizer.ExtractPalette(frameRGB, 256);
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
                            bw.Write((ushort)0); bw.Write((ushort)255);
                            bw.Write((byte)1); bw.Write((byte)1);
                            bw.Write(0x00000000);

                            // 255×RGB palette
                            WriteRgbTriplets(bw, pal, 255);

                            // pad to CONTROL_OFFSET
                            long toPad = CONTROL_OFFSET - fs.Position;
                            if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                            if (toPad > 0) bw.Write(new byte[toPad]);

                            // pixels (full 640×480 filled)
                            bw.Write(pixels);
                            bw.Flush();
                        }
                    }
                }
                else
                {
                    // CONTAIN / FIT (no upscaling): same as earlier slow path, then center into 640×480 with background=255
                    using (var fitted = ResizeToFit(srcBmp, MAX_W, MAX_H))
                    {
                        int fw = fitted.Width, fh = fitted.Height;
                        var palList = MedianCutQuantizer.ExtractPalette(fitted, 256);
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
                            bw.Write((ushort)0); bw.Write((ushort)255);
                            bw.Write((byte)1); bw.Write((byte)1);
                            bw.Write(0x00000000);

                            // 255×RGB palette
                            WriteRgbTriplets(bw, pal, 255);

                            // pad to CONTROL_OFFSET
                            long toPad = CONTROL_OFFSET - fs.Position;
                            if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                            if (toPad > 0) bw.Write(new byte[toPad]);

                            // pixels (centered 640×480)
                            bw.Write(pixels);
                            bw.Flush();
                        }
                    }
                }
            }
        }
        */
        /* Works backup
         *  public static void SaveImageAsV56(System.Drawing.Image source, string outPath)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentNullException(nameof(outPath));

            const int MAX_W = 640;
            const int MAX_H = 480;
            const int CONTROL_OFFSET = 870; // 103 + 255*3 + 2 pad

            using (var srcBmp = new System.Drawing.Bitmap(source))
            {
                bool fits = srcBmp.Width <= MAX_W && srcBmp.Height <= MAX_H;
                bool is8bpp = srcBmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format8bppIndexed;

                // ---------- FAST PATH: already 8bpp indexed and fits -> center into 640x480 with background=255 ----------
                if (fits && is8bpp)
                {
                    int w = srcBmp.Width, h = srcBmp.Height;

                    // read source indices with stride (no unsafe)
                    var rect = new System.Drawing.Rectangle(0, 0, w, h);
                    var bd = srcBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, srcBmp.PixelFormat);
                    byte[] srcRaw = new byte[bd.Stride * h];
                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, srcRaw, 0, srcRaw.Length);
                    }
                    finally
                    {
                        srcBmp.UnlockBits(bd);
                    }

                    // build destination 640x480 index buffer, fill with 255 (transparent), then center-copy the src
                    byte[] pixels = new byte[MAX_W * MAX_H];
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = (byte)255;
                    //System.Array.Fill(pixels, (byte)255);
                    int ox = (MAX_W - w) / 2;
                    int oy = (MAX_H - h) / 2;

                    int maxIdx = 0;
                    for (int y = 0; y < h; y++)
                    {
                        int srcOff = y * bd.Stride;
                        int dstOff = (oy + y) * MAX_W + ox;

                        // copy exactly w bytes from the src row (8bpp => 1 byte per pixel)
                        System.Buffer.BlockCopy(srcRaw, srcOff, pixels, dstOff, w);

                        // track max index encountered so we can enforce 0..254 rule
                        for (int x = 0; x < w; x++)
                        {
                            int v = pixels[dstOff + x];
                            if (v > maxIdx) maxIdx = v;
                        }
                    }

                    // we only store 255×RGB; disallow pixel value 255
                    if (maxIdx > 254)
                        throw new InvalidOperationException("8bpp image uses palette index 255; cannot pack without altering data.");

                    // palette → first 255 entries
                    var entries = srcBmp.Palette.Entries;
                    if (entries.Length < 255) throw new InvalidOperationException("8bpp image palette has fewer than 255 entries.");
                    var palList = new System.Collections.Generic.List<System.Drawing.Color>(256);
                    palList.AddRange(entries);
                    var pal = PaletteData.FromRgbList(palList);

                    using (var fs = new System.IO.FileStream(outPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                    using (var bw = new System.IO.BinaryWriter(fs))
                    {
                        // resource tag
                        bw.Write(new byte[] { 0x81, 0x81, 0x00, 0x00 });

                        // PicHeader32 (full frame 640x480)
                        var pic = new PicHeader32
                        {
                            picHeaderSize = 14,
                            celCount = 1,
                            splitFlag = 0,
                            celHeaderSize = 42,
                            paletteOffset = 62,
                            resX = (ushort)MAX_W,
                            resY = (ushort)MAX_H
                        };
                        BinaryUtil.WriteStruct(bw, pic);

                        // CelHeaderPic (full frame)
                        var cel = new CelHeaderPic
                        {
                            W = (ushort)MAX_W,
                            H = (ushort)MAX_H,
                            xShift = 0,
                            yShift = 0,
                            transparent = 255,
                            compressType = 0,
                            dataFlags = 0,
                            dataByteCount = MAX_W * MAX_H,
                            controlByteCount = 0,
                            paletteOffsetCell = 0,
                            controlOffset = CONTROL_OFFSET,
                            colorOffset = 0,
                            rowTableOffset = 0,
                            priority = 0,
                            xpos = 0,
                            ypos = 0
                        };
                        BinaryUtil.WriteStruct(bw, cel);

                        // 2-byte pad to paletteOffset=62
                        bw.Write((byte)0x00);
                        bw.Write((byte)0x00);

                        // legacy wrapper + PalHeader + index list
                        bw.Write((ushort)0x0322); bw.Write((ushort)0x0000);
                        bw.Write((byte)14); WriteFixedAnsi(bw, "GENPAL", 9); bw.Write((byte)1); bw.Write((short)0);
                        bw.Write((ushort)0);

                        // CompPal header (known-good values)
                        bw.Write(new byte[] { 0x00, 0x34, 0x00, 0x00, 0x02, 0x00, 0x5A, 0x0E, 0x00, 0x00 });
                        bw.Write((byte)0);
                        bw.Write((byte)0);
                        bw.Write((ushort)0);
                        bw.Write((ushort)255);
                        bw.Write((byte)1);
                        bw.Write((byte)1);
                        bw.Write(0x00000000);

                        // 255×RGB palette
                        WriteRgbTriplets(bw, pal, 255);

                        // pad to CONTROL_OFFSET (usually 2 bytes)
                        long toPad = CONTROL_OFFSET - fs.Position;
                        if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                        if (toPad > 0) bw.Write(new byte[toPad]);

                        // pixels (full 640x480 with centered content)
                        bw.Write(pixels);
                        bw.Flush();
                    }

                    return; // fast path done
                }

                // ---------- SLOW PATH: resize+quantize -> center into 640x480 with background=255 ----------
                using (var fitted = ResizeToFit(srcBmp, MAX_W, MAX_H))
                {
                    int fw = fitted.Width, fh = fitted.Height;

                    // quantize to 256, then map to 0..254
                    var palList = MedianCutQuantizer.ExtractPalette(fitted, 256);
                    var pal = PaletteData.FromRgbList(palList);
                    byte[] srcIdx = MapToPalette(fitted, pal);

                    // build destination 640x480 filled with 255, then center-blit
                    byte[] pixels = new byte[MAX_W * MAX_H];

                    for(int i = 0;i<pixels.Length;i++)
                        pixels[i] = (byte)255;
                    //System.Array.Fill(pixels, (byte)255);
                    int ox = (MAX_W - fw) / 2;
                    int oy = (MAX_H - fh) / 2;

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
                        // resource tag
                        bw.Write(new byte[] { 0x81, 0x81, 0x00, 0x00 });

                        // PicHeader32 (full frame 640x480)
                        var pic = new PicHeader32
                        {
                            picHeaderSize = 14,
                            celCount = 1,
                            splitFlag = 0,
                            celHeaderSize = 42,
                            paletteOffset = 62,
                            resX = (ushort)MAX_W,
                            resY = (ushort)MAX_H
                        };
                        BinaryUtil.WriteStruct(bw, pic);

                        // CelHeaderPic (full frame)
                        var cel = new CelHeaderPic
                        {
                            W = (ushort)MAX_W,
                            H = (ushort)MAX_H,
                            xShift = 0,
                            yShift = 0,
                            transparent = 255,
                            compressType = 0,
                            dataFlags = 0,
                            dataByteCount = MAX_W * MAX_H,
                            controlByteCount = 0,
                            paletteOffsetCell = 0,
                            controlOffset = CONTROL_OFFSET,
                            colorOffset = 0,
                            rowTableOffset = 0,
                            priority = 0,
                            xpos = 0,
                            ypos = 0
                        };
                        BinaryUtil.WriteStruct(bw, cel);

                        // 2-byte pad to paletteOffset=62
                        bw.Write((byte)0x00);
                        bw.Write((byte)0x00);

                        // legacy wrapper + PalHeader + index list
                        bw.Write((ushort)0x0322); bw.Write((ushort)0x0000);
                        bw.Write((byte)14); WriteFixedAnsi(bw, "GENPAL", 9); bw.Write((byte)1); bw.Write((short)0);
                        bw.Write((ushort)0);

                        // CompPal header
                        bw.Write(new byte[] { 0x00, 0x34, 0x00, 0x00, 0x02, 0x00, 0x5A, 0x0E, 0x00, 0x00 });
                        bw.Write((byte)0);
                        bw.Write((byte)0);
                        bw.Write((ushort)0);
                        bw.Write((ushort)255);
                        bw.Write((byte)1);
                        bw.Write((byte)1);
                        bw.Write(0x00000000);

                        // 255×RGB palette
                        WriteRgbTriplets(bw, pal, 255);

                        // pad to CONTROL_OFFSET
                        long toPad = CONTROL_OFFSET - fs.Position;
                        if (toPad < 0) throw new InvalidOperationException("Palette overran pixel area.");
                        if (toPad > 0) bw.Write(new byte[toPad]);

                        // pixels (full 640x480 with centered content)
                        bw.Write(pixels);
                        bw.Flush();
                    }
                }
            }
        }

         */

        // --- helpers (you said helpers are OK) ---

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
