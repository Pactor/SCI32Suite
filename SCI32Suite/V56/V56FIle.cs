// V56Reader.cs
// .NET Framework 4.8 - SCI32 V56 View reader (The Realm-style)
// - Reads header, loops, cels (including mirror loops)
// - Decodes cel pixels to 8-bit indices (byte[])
// - Extracts embedded palette (0x0300 tag) if present
// - Exposes per-row link/offset data (control/data/row tables) and basic "rect" (native width/height)
// Notes:
// - This implements the SCI32 V56 RLE scheme you validated:
//     ctrl < 0x40  : literal copy N bytes
//     0x40-0x7F    : long literal: N = ((ctrl & 0x3F) << 8) | nextByte
//     0x80-0xBF    : run of next data byte, N = (ctrl & 0x3F)
//     0xC0-0xFF    : run of transparentIndex, N = (ctrl & 0x3F)
// - Cel header size supported: 0x34 (SCI32 V56) and 0x24 (older); 0x34 is expected for Realm V56.
// - Loop header size expected: 0x10.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
public struct V56Rect
{
    public int Left, Top, Right, Bottom;
    public override string ToString()
    {
        return "(" + Left + ", " + Top + ") - (" + Right + ", " + Bottom + ")";
    }
}

public sealed class V56ViewR
{
    public string FileName { get; set; }
    public int ViewStart { get; set; }

    // "Rect" / logical size from view header (commonly native width/height)
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }

    public V56HeaderR Header { get; set; }
    public V56PaletteR Palette { get; set; }

    public List<V56LoopR> Loops { get; set; }

    public int LoopCount { get { return (Loops != null) ? Loops.Count : 0; } }

    // Total real cels (mirror loops contribute 0 real cels)
    public int TotalCelCount
    {
        get
        {
            if (Loops == null) return 0;
            int n = 0;
            for (int i = 0; i < Loops.Count; i++)
                n += Loops[i].Cels.Count;
            return n;
        }
    }

    internal V56ViewR()
    {
        Loops = new List<V56LoopR>();
        Palette = V56PaletteR.CreateDefaultGrayscale();
        Header = new V56HeaderR();
        FileName = "";
    }

    public V56LoopR GetLoopResolved(int loopIndex)
    {
        if (Loops == null) return null;
        if (loopIndex < 0 || loopIndex >= Loops.Count) return null;

        V56LoopR l = Loops[loopIndex];
        if (l.IsMirror && l.MirrorOf >= 0 && l.MirrorOf < Loops.Count)
            return Loops[l.MirrorOf];

        return l;
    }
}

public sealed class V56HeaderR
{
    public ushort ViewHeaderSize;
    public byte LoopCount;
    public byte StripView;
    public byte SplitView;
    public byte Resolution;
    public ushort CelCount;          // may be 0/unused
    public uint PaletteOffset;       // offset inside view to palette block (tag 0x0300), 0 if none
    public byte LoopHeaderSize;      // expected 0x10
    public byte CelHeaderSize;       // expected 0x34
    public ushort ResX;              // native width
    public ushort ResY;              // native height
    public byte Version;             // >= 0x80
    public byte Future;
}

public sealed class V56LoopR
{
    public int Index { get; internal set; }
    public sbyte AltLoop { get; internal set; }     // for mirrors: source loop index
    public byte Flags { get; internal set; }        // bit0 commonly indicates mirror
    public byte NumCelsRaw { get; internal set; }   // raw header value
    public uint LoopPaletteOffset { get; internal set; }
    public uint CelOffset { get; internal set; }    // offset to this loop's cel headers (relative to view start)

    public bool IsMirror { get { return (Flags & 0x01) != 0 && NumCelsRaw == 0 && AltLoop >= 0; } }
    public int MirrorOf { get { return (IsMirror) ? (int)AltLoop : -1; } }

    public List<V56CelR> Cels { get; private set; }
    public V56Rect GetCelRect(V56ViewR view, int celIndex)
    {
        V56LoopR src = this;

        bool mirror = false;
        if (IsMirror)
        {
            mirror = true;
            if (MirrorOf < 0 || MirrorOf >= view.Loops.Count) throw new InvalidDataException("Bad mirror loop.");
            src = view.Loops[MirrorOf];
        }

        if (celIndex < 0 || celIndex >= src.Cels.Count) throw new ArgumentOutOfRangeException("celIndex");
        return src.Cels[celIndex].GetRect(mirror);
    }


    internal V56LoopR()
    {
        Cels = new List<V56CelR>();
    }
}

public sealed class V56CelR
{
    public int LoopIndex { get; internal set; }
    public int CelIndex { get; internal set; }

    public ushort Width { get; internal set; }
    public ushort Height { get; internal set; }
    public short XHot { get; internal set; }
    public short YHot { get; internal set; }

    public byte TransparentIndex { get; internal set; }
    public byte CompressionType { get; internal set; }  // commonly 0 (raw) or 0x8A (RLE) in Realm files

    // Link/offset data inside view (relative to view start)
    public uint ControlOffset { get; internal set; }    // +0x18
    public uint DataOffset { get; internal set; }       // +0x1C
    public uint RowTableOffset { get; internal set; }   // +0x20

    // Raw cel header bytes (handy for debugging / future fields)
    public byte[] RawHeader { get; internal set; }

    // Decoded 8-bit indices (Width*Height). TransparentIndex denotes transparency.
    public byte[] Indices { get; internal set; }
    public uint LinkOffset { get; internal set; }   // 0x34 only
    public uint LinkCount { get; internal set; }   // 0x34 only (or link meta)
    public byte[] LinkRaw { get; internal set; }   // raw bytes for inspection

    public int PixelCount
    {
        get
        {
            if (Width == 0 || Height == 0) return 0;
            return (int)Width * (int)Height;
        }
    }
    public V56Rect GetRect(bool mirror)
    {
        int halfW = Width / 2;           // integer division
        int left = XHot - halfW;
        int right = XHot + halfW;        // makes odd widths produce Width-1 span
        int top = YHot - (Height - 1);
        int bottom = YHot;

        if (!mirror)
            return new V56Rect { Left = left, Top = top, Right = right, Bottom = bottom };

        return new V56Rect { Left = -right, Top = top, Right = -left, Bottom = bottom };
    }



    public Bitmap ToBitmap(V56PaletteR palette, bool mirror)
    {
        if (palette == null) palette = V56PaletteR.CreateDefaultGrayscale();
        if (Indices == null || Indices.Length < PixelCount) return null;

        int w = (int)Width;
        int h = (int)Height;

        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bd.Stride;
            int[] argb = new int[(stride / 4) * h]; // stride is bytes

            int rowInts = stride / 4;

            for (int y = 0; y < h; y++)
            {
                int outRow = y * rowInts;
                int inRow = y * w;

                for (int x = 0; x < w; x++)
                {
                    int sx = mirror ? (w - 1 - x) : x;
                    byte idx = Indices[inRow + sx];

                    if (idx == TransparentIndex)
                    {
                        argb[outRow + x] = unchecked((int)0x00000000);
                    }
                    else
                    {
                        Color c = palette.Colors[idx];
                        argb[outRow + x] = (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
                    }
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(argb, 0, bd.Scan0, argb.Length);
        }
        finally
        {
            bmp.UnlockBits(bd);
        }

        return bmp;
    }

    public byte[] ToPngBytes(V56PaletteR palette, bool mirror)
    {
        Bitmap bmp = ToBitmap(palette, mirror);
        if (bmp == null) return null;

        using (bmp)
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}

public sealed class V56PaletteR
{
    public Color[] Colors { get; private set; } // length 256
    private int[] _rgbPacked; // for nearest search

    public V56PaletteR()
    {
        Colors = new Color[256];
        _rgbPacked = new int[256];
    }

    internal void RebuildCache()
    {
        for (int i = 0; i < 256; i++)
        {
            Color c = Colors[i];
            _rgbPacked[i] = (c.R << 16) | (c.G << 8) | c.B;
        }
    }
    public static V56PaletteR CreateDefaultGrayscale()
    {
        V56PaletteR p = new V56PaletteR();
        for (int i = 0; i < 256; i++)
            p.Colors[i] = Color.FromArgb(i, i, i);
        return p;
    }

    public Bitmap ToBitmap16x16()
    {
        Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format24bppRgb);
        for (int i = 0; i < 256; i++)
        {
            int x = i % 16;
            int y = i / 16;
            Color c = Colors[i];
            bmp.SetPixel(x, y, Color.FromArgb(255, c.R, c.G, c.B));
        }
        return bmp;
    }
    public byte FindNearestIndex(byte r, byte g, byte b)
    {
        // Deterministic nearest: squared distance, prefer lower index on tie.
        int best = 0;
        int bestD = int.MaxValue;

        // Skip index 255 (reserved transparency) for opaque pixels.
        for (int i = 0; i < 255; i++)
        {
            Color c = Colors[i];
            int dr = c.R - r;
            int dg = c.G - g;
            int db = c.B - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD)
            {
                bestD = d;
                best = i;
                if (d == 0) break;
            }
        }

        return (byte)best;
    }
}

public static class V56ReaderR
{
    public static V56ViewR Load(string filePath)
    {
        if (filePath == null) throw new ArgumentNullException("filePath");
        byte[] fileData = File.ReadAllBytes(filePath);
        return Load(fileData, Path.GetFileName(filePath));
    }

    public static V56ViewR Load(byte[] fileData, string fileName)
    {
        if (fileData == null) throw new ArgumentNullException("fileData");

        V56HeaderR hdr;
        int viewStart = FindViewStart(fileData, out hdr);
        if (viewStart < 0) throw new InvalidDataException("Could not locate a valid V56 view header.");

        V56ViewR view = new V56ViewR();
        view.FileName = (fileName ?? "");
        view.ViewStart = viewStart;
        view.Header = hdr;
        view.NativeWidth = hdr.ResX;
        view.NativeHeight = hdr.ResY;

        // Parse loops
        int loopsBase = viewStart + hdr.ViewHeaderSize + 2;
        for (int i = 0; i < hdr.LoopCount; i++)
        {
            int lo = loopsBase + i * hdr.LoopHeaderSize;
            V56LoopR loop = ParseLoopHeader(fileData, lo);
            loop.Index = i;
            view.Loops.Add(loop);
        }

        // Parse cels (for mirror loops: no cels stored)
        for (int i = 0; i < view.Loops.Count; i++)
        {
            V56LoopR loop = view.Loops[i];
            if (loop.IsMirror) continue;

            for (int c = 0; c < loop.NumCelsRaw; c++)
            {
                int celHdrAbs = viewStart + (int)loop.CelOffset + c * hdr.CelHeaderSize;
                V56CelR cel = ParseCelHeader(fileData, celHdrAbs, hdr.CelHeaderSize);
                cel.LoopIndex = i;
                cel.CelIndex = c;

                // Decode indices
                // Decode indices
                cel.Indices = DecodeCelIndices(fileData, viewStart, cel);

                // Capture link block bytes (for later rect/collision/linkpoint parsing)
                if (cel.LinkOffset != 0 && hdr.CelHeaderSize >= 0x34)
                {
                    int lp = viewStart + (int)cel.LinkOffset;
                    if (lp >= 0 && lp < fileData.Length)
                    {
                        int take = Math.Min(256, fileData.Length - lp); // enough to inspect in UI/logs
                        cel.LinkRaw = new byte[take];
                        Buffer.BlockCopy(fileData, lp, cel.LinkRaw, 0, take);
                    }
                }

                loop.Cels.Add(cel);

            }
        }

        // Palette: embedded 0x0300 block is typically right after last cel header
        V56PaletteR embedded = TryReadEmbeddedPalette(fileData, viewStart, (int)hdr.PaletteOffset);
        if (embedded != null) view.Palette = embedded;

        return view;
    }
    private static Color[] ParseCompPalPayload(byte[] palPayload)
    {
        if (palPayload == null || palPayload.Length < 37) return null;

        int startOffset = palPayload[25];
        int nColors = palPayload[29] | (palPayload[30] << 8);
        int palType = palPayload[32]; // 0=used+RGB (4 bytes), 1=RGB (3 bytes)

        if (nColors <= 0 || nColors > 256) return null;

        Color[] pal = new Color[256];
        for (int i = 0; i < 256; i++) pal[i] = Color.Black;

        int entryBase = 37;

        for (int i = 0; i < nColors; i++)
        {
            int dst = (startOffset + i) & 255;

            if (palType == 1)
            {
                int e = entryBase + i * 3;
                if (e + 3 > palPayload.Length) break;
                pal[dst] = Color.FromArgb(palPayload[e], palPayload[e + 1], palPayload[e + 2]);
            }
            else
            {
                int e = entryBase + i * 4;
                if (e + 4 > palPayload.Length) break;
                pal[dst] = Color.FromArgb(palPayload[e + 1], palPayload[e + 2], palPayload[e + 3]);
            }
        }

        return pal;
    }

    private static int FindViewStart(byte[] fileData, out V56HeaderR header)
    {
        header = null;

        for (int off = 0; off < fileData.Length - 64; off++)
        {
            V56HeaderR h = TryParseHeaderAt(fileData, off);
            if (h == null) continue;

            int loopsBase = off + h.ViewHeaderSize + 2;
            int loopsEnd = loopsBase + h.LoopHeaderSize * h.LoopCount;
            if (loopsEnd > fileData.Length) continue;

            // Validate: each loop either mirror or has at least 1 cel header in range with plausible dims
            bool ok = true;
            for (int i = 0; i < h.LoopCount; i++)
            {
                int lo = loopsBase + i * h.LoopHeaderSize;
                sbyte altLoop = (sbyte)fileData[lo + 0];
                byte flags = fileData[lo + 1];
                byte numCels = fileData[lo + 2];
                uint celOffset = ReadU32(fileData, lo + 12);

                bool isMirror = ((flags & 0x01) != 0) && (numCels == 0) && (altLoop >= 0);
                if (isMirror) continue;

                if (numCels > 64) { ok = false; break; }

                int celHdrAbs = off + (int)celOffset;
                if (celHdrAbs + h.CelHeaderSize > fileData.Length) { ok = false; break; }

                ushort w = ReadU16(fileData, celHdrAbs + 0);
                ushort hh = ReadU16(fileData, celHdrAbs + 2);
                if (w == 0 || hh == 0 || w > 4096 || hh > 4096) { ok = false; break; }
            }

            if (ok)
            {
                header = h;
                return off;
            }
        }

        return -1;
    }

    private static V56HeaderR TryParseHeaderAt(byte[] fileData, int off)
    {
        if (off + 20 > fileData.Length) return null;

        ushort viewHeaderSize = ReadU16(fileData, off + 0);
        byte loopCount = fileData[off + 2];
        byte loopHeaderSize = fileData[off + 12];
        byte celHeaderSize = fileData[off + 13];
        byte version = fileData[off + 18];

        if (viewHeaderSize < 10 || viewHeaderSize > 64) return null;
        if (loopCount < 1 || loopCount > 64) return null;
        if (loopHeaderSize != 0x10) return null;
        if (celHeaderSize != 0x34 && celHeaderSize != 0x24) return null;
        if (version < 0x80) return null;

        V56HeaderR h = new V56HeaderR();
        h.ViewHeaderSize = viewHeaderSize;
        h.LoopCount = loopCount;
        h.StripView = fileData[off + 3];
        h.SplitView = fileData[off + 4];
        h.Resolution = fileData[off + 5];
        h.CelCount = ReadU16(fileData, off + 6);
        h.PaletteOffset = ReadU32(fileData, off + 8);
        h.LoopHeaderSize = loopHeaderSize;
        h.CelHeaderSize = celHeaderSize;
        h.ResX = ReadU16(fileData, off + 14);
        h.ResY = ReadU16(fileData, off + 16);
        h.Version = version;
        h.Future = fileData[off + 19];
        return h;
    }

    private static V56LoopR ParseLoopHeader(byte[] fileData, int off)
    {
        V56LoopR l = new V56LoopR();
        l.AltLoop = (sbyte)fileData[off + 0];
        l.Flags = fileData[off + 1];
        l.NumCelsRaw = fileData[off + 2];
        l.LoopPaletteOffset = ReadU32(fileData, off + 8);
        l.CelOffset = ReadU32(fileData, off + 12);
        return l;
    }

    private static V56CelR ParseCelHeader(byte[] fileData, int off, int celHeaderSize)
    {
        if (off + celHeaderSize > fileData.Length) throw new InvalidDataException("Cel header out of range.");

        V56CelR c = new V56CelR();
        c.Width = ReadU16(fileData, off + 0);
        c.Height = ReadU16(fileData, off + 2);
        c.XHot = ReadS16(fileData, off + 4);
        c.YHot = ReadS16(fileData, off + 6);

        c.TransparentIndex = fileData[off + 0x08];
        c.CompressionType = fileData[off + 0x09];

        if (celHeaderSize >= 0x24)
        {
            c.ControlOffset = ReadU32(fileData, off + 0x18);
            c.DataOffset = ReadU32(fileData, off + 0x1C);
            c.RowTableOffset = ReadU32(fileData, off + 0x20);
        }
        else
        {
            c.ControlOffset = 0;
            c.DataOffset = 0;
            c.RowTableOffset = 0;
        }

        // Link fields exist only on 0x34 headers
        if (celHeaderSize >= 0x34)
        {
            c.LinkOffset = ReadU32(fileData, off + 0x24);
            c.LinkCount = ReadU32(fileData, off + 0x28);
        }
        else
        {
            c.LinkOffset = 0;
            c.LinkCount = 0;
        }

        c.RawHeader = new byte[celHeaderSize];
        Buffer.BlockCopy(fileData, off, c.RawHeader, 0, celHeaderSize);
        return c;
    }


    private static byte[] DecodeCelIndices(byte[] fileData, int viewStart, V56CelR cel)
    {
        int w = cel.Width;
        int h = cel.Height;
        int expected = w * h;

        if (w <= 0 || h <= 0) return new byte[0];

        byte[] outIdx = new byte[expected];

        if (cel.CompressionType == 0)
        {
            int src = viewStart + (int)cel.ControlOffset; // for uncompressed, ControlOffset is the pixel data pointer in these files
            if (src < 0 || src >= fileData.Length) return Fill(cel.TransparentIndex, expected);

            int n = expected;
            if (src + n > fileData.Length) n = Math.Max(0, fileData.Length - src);

            Buffer.BlockCopy(fileData, src, outIdx, 0, n);
            if (n < expected)
            {
                for (int i = n; i < expected; i++) outIdx[i] = cel.TransparentIndex;
            }
            return outIdx;
        }

        if (cel.CompressionType != 0x8A)
            return Fill(cel.TransparentIndex, expected);

        int rowBase = viewStart + (int)cel.RowTableOffset;
        int tableBytes = h * 8;
        if (cel.RowTableOffset == 0 || rowBase < 0 || rowBase + tableBytes > fileData.Length)
        {
            // No row table: fall back to sequential decode using current offsets
            int controlBase0 = viewStart + (int)cel.ControlOffset;
            int dataBase0 = viewStart + (int)cel.DataOffset;
            return DecodeRleNoRowTable(fileData, controlBase0, dataBase0, w, h, cel.TransparentIndex);
        }

        uint[] ctrlTable = new uint[h];
        uint[] dataTable = new uint[h];

        for (int i = 0; i < h; i++)
            ctrlTable[i] = ReadU32(fileData, rowBase + i * 4);
        for (int i = 0; i < h; i++)
            dataTable[i] = ReadU32(fileData, rowBase + (h * 4) + i * 4);

        // Compute both candidate stream bases (some 0x24 files have control/data swapped in meaning).
        int controlBaseA = viewStart + (int)cel.ControlOffset;
        int dataBaseA = viewStart + (int)cel.DataOffset;

        int controlBaseB = viewStart + (int)cel.DataOffset;
        int dataBaseB = viewStart + (int)cel.ControlOffset;

        // Decide which interpretation fills rows correctly (cheap probe on up to 4 rows).
        bool useB = ShouldSwapStreams(fileData, w, h, cel.TransparentIndex, controlBaseA, dataBaseA, controlBaseB, dataBaseB, ctrlTable, dataTable);

        int controlBase = useB ? controlBaseB : controlBaseA;
        int dataBase = useB ? dataBaseB : dataBaseA;

        for (int row = 0; row < h; row++)
        {
            int cp = controlBase + (int)ctrlTable[row];
            int dp = dataBase + (int)dataTable[row];

            int rowStart = row * w;
            int j = 0;

            while (j < w)
            {
                if (cp < 0 || cp >= fileData.Length) break;

                byte control = fileData[cp++];
                if (control == 0) break;

                // SCI32 V56 (Realm): range-based control bytes
                if (control < 0x40)
                {
                    int n = control;
                    if (dp < 0 || dp >= fileData.Length) break;

                    if (dp + n > fileData.Length) n = Math.Max(0, fileData.Length - dp);
                    if (j + n > w) n = Math.Max(0, w - j);

                    Buffer.BlockCopy(fileData, dp, outIdx, rowStart + j, n);
                    dp += n;
                    j += n;
                }
                else if (control < 0x80)
                {
                    // Long literal: ((control & 0x3F) << 8) | nextByte
                    if (cp < 0 || cp >= fileData.Length) break;
                    int n = ((control & 0x3F) << 8) | fileData[cp++];

                    if (n <= 0) continue;
                    if (dp < 0 || dp >= fileData.Length) break;

                    if (dp + n > fileData.Length) n = Math.Max(0, fileData.Length - dp);
                    if (j + n > w) n = Math.Max(0, w - j);

                    Buffer.BlockCopy(fileData, dp, outIdx, rowStart + j, n);
                    dp += n;
                    j += n;
                }
                else if (control < 0xC0)
                {
                    int n = control & 0x3F;
                    if (dp < 0 || dp >= fileData.Length) break;

                    byte col = fileData[dp++];
                    int end = j + n;
                    if (end > w) end = w;

                    for (int x = j; x < end; x++) outIdx[rowStart + x] = col;
                    j = end;
                }
                else
                {
                    int n = control & 0x3F;
                    int end = j + n;
                    if (end > w) end = w;

                    for (int x = j; x < end; x++) outIdx[rowStart + x] = cel.TransparentIndex;
                    j = end;
                }
            }

            if (j < w)
            {
                for (int x = j; x < w; x++) outIdx[rowStart + x] = cel.TransparentIndex;
            }
        }

        return outIdx;
    }
    private static bool RowFillsWidth(byte[] fileData, int w, byte transparentIndex, int cp, int dp)
    {
        int x = 0;
        int steps = 0;

        while (x < w && steps < 20000)
        {
            if (cp < 0 || cp >= fileData.Length) return false;

            byte control = fileData[cp++];
            if (control == 0) break;

            if (control < 0x40)
            {
                int n = control;
                dp += n;
                x += n;
            }
            else if (control < 0x80)
            {
                if (cp < 0 || cp >= fileData.Length) return false;
                int n = ((control & 0x3F) << 8) | fileData[cp++];
                dp += n;
                x += n;
            }
            else if (control < 0xC0)
            {
                int n = control & 0x3F;
                dp += 1;
                x += n;
            }
            else
            {
                int n = control & 0x3F;
                x += n;
            }

            if (dp < 0 || dp > fileData.Length) return false;
            if (x > w) return false;

            steps++;
        }

        return x == w;
    }

    private static bool ShouldSwapStreams(
    byte[] fileData,
    int w,
    int h,
    byte transparentIndex,
    int controlBaseA,
    int dataBaseA,
    int controlBaseB,
    int dataBaseB,
    uint[] ctrlTable,
    uint[] dataTable)
    {
        int probeRows = h;
        if (probeRows > 4) probeRows = 4;

        int okA = 0;
        int okB = 0;

        for (int row = 0; row < probeRows; row++)
        {
            if (RowFillsWidth(fileData, w, transparentIndex, controlBaseA + (int)ctrlTable[row], dataBaseA + (int)dataTable[row])) okA++;
            if (RowFillsWidth(fileData, w, transparentIndex, controlBaseB + (int)ctrlTable[row], dataBaseB + (int)dataTable[row])) okB++;
        }

        return okB > okA;
    }

    private static byte[] DecodeRleNoRowTable(byte[] fileData, int controlBase, int dataBase, int w, int h, byte tIndex)
    {
        // Best-effort sequential decode (rare for the Realm's V56, but keeps reader resilient)
        byte[] outIdx = new byte[w * h];
        int cp = controlBase;
        int dp = dataBase;

        for (int row = 0; row < h; row++)
        {
            int rowStart = row * w;
            int j = 0;
            while (j < w)
            {
                if (cp < 0 || cp >= fileData.Length) break;
                byte ctrl = fileData[cp++];
                if (ctrl == 0) break;

                if (ctrl < 0x40)
                {
                    int n = ctrl;
                    if (dp < 0 || dp >= fileData.Length) break;
                    if (dp + n > fileData.Length) n = Math.Max(0, fileData.Length - dp);
                    Buffer.BlockCopy(fileData, dp, outIdx, rowStart + j, n);
                    dp += n;
                    j += n;
                }
                else if (ctrl < 0x80)
                {
                    if (cp < 0 || cp >= fileData.Length) break;
                    byte lo = fileData[cp++];
                    int n = ((ctrl & 0x3F) << 8) | lo;
                    if (n <= 0) continue;

                    if (dp < 0 || dp >= fileData.Length) break;
                    if (dp + n > fileData.Length) n = Math.Max(0, fileData.Length - dp);
                    Buffer.BlockCopy(fileData, dp, outIdx, rowStart + j, n);
                    dp += n;
                    j += n;
                }
                else if (ctrl < 0xC0)
                {
                    int n = ctrl & 0x3F;
                    if (dp < 0 || dp >= fileData.Length) break;
                    byte val = fileData[dp++];
                    int end = Math.Min(w, j + n);
                    for (int x = j; x < end; x++)
                        outIdx[rowStart + x] = val;
                    j = end;
                }
                else
                {
                    int n = ctrl & 0x3F;
                    int end = Math.Min(w, j + n);
                    for (int x = j; x < end; x++)
                        outIdx[rowStart + x] = tIndex;
                    j = end;
                }
            }

            if (j < w)
                for (int x = j; x < w; x++) outIdx[rowStart + x] = tIndex;
        }

        return outIdx;
    }

    private static V56PaletteR TryReadEmbeddedPalette(byte[] file, int viewStart, int paletteOffset)
    {
        // paletteOffset points to payload start; tag is at (paletteOffset - 6)
        int palTagPos = viewStart + paletteOffset - 6;
        if (palTagPos < 0 || palTagPos + 6 > file.Length) return null;

        ushort tag = ReadU16(file, palTagPos + 0);
        if (tag != 0x0300) return null;

        int palSize = (int)ReadU32(file, palTagPos + 2);
        int payloadPos = palTagPos + 6;

        if (palSize <= 0 || payloadPos + palSize > file.Length) return null;

        byte[] payload = new byte[palSize];
        Buffer.BlockCopy(file, payloadPos, payload, 0, palSize);

        Color[] colors = ParseCompPalPayload(payload);
        if (colors == null) return null;

        V56PaletteR p = new V56PaletteR();
        for (int i = 0; i < 256; i++) p.Colors[i] = colors[i];
        p.RebuildCache();
        return p;
    }

    private static byte[] Fill(byte value, int count)
    {
        byte[] b = new byte[count];
        for (int i = 0; i < count; i++) b[i] = value;
        return b;
    }

    private static ushort ReadU16(byte[] b, int off)
    {
        return (ushort)(b[off] | (b[off + 1] << 8));
    }

    private static short ReadS16(byte[] b, int off)
    {
        return (short)(b[off] | (b[off + 1] << 8));
    }

    private static uint ReadU32(byte[] b, int off)
    {
        return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
    }
}
