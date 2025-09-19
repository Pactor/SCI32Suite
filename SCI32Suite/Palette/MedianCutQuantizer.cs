using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SCI32Suite.Palette
{
    // Lightweight median-cut quantizer to extract up to 'maxColors' representative colors.
    public static class MedianCutQuantizer
    {
        private sealed class ColorBox
        {
            public List<Color> Colors;
            public byte MinR, MaxR, MinG, MaxG, MinB, MaxB;

            public ColorBox(List<Color> colors)
            {
                Colors = colors;
                RecalcBounds();
            }

            public void RecalcBounds()
            {
                if (Colors.Count == 0)
                {
                    MinR = MinG = MinB = 0;
                    MaxR = MaxG = MaxB = 0;
                    return;
                }
                MinR = Colors.Min(c => c.R);
                MaxR = Colors.Max(c => c.R);
                MinG = Colors.Min(c => c.G);
                MaxG = Colors.Max(c => c.G);
                MinB = Colors.Min(c => c.B);
                MaxB = Colors.Max(c => c.B);
            }

            public int RangeR { get { return MaxR - MinR; } }
            public int RangeG { get { return MaxG - MinG; } }
            public int RangeB { get { return MaxB - MinB; } }
        }

        public static List<Color> ExtractPalette(Bitmap bmp, int maxColors)
        {
            // Sample all pixels; if huge, we can downsample (keep it simple here).
            var pixels = new List<Color>(bmp.Width * bmp.Height);
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    pixels.Add(Color.FromArgb(c.R, c.G, c.B));
                }
            }

            // Initial box contains all colors (we can compress duplicates)
            // Optional: pre-bucket to reduce duplicates
            var unique = new Dictionary<int, int>(pixels.Count);
            for (int i = 0; i < pixels.Count; i++)
            {
                var c = pixels[i];
                int key = (c.R << 16) | (c.G << 8) | c.B;
                if (!unique.ContainsKey(key)) unique[key] = 0;
                unique[key]++;
            }
            var uniqColors = new List<Color>(unique.Keys.Select(k => Color.FromArgb((k >> 16) & 0xFF, (k >> 8) & 0xFF, k & 0xFF)));

            var boxes = new List<ColorBox> { new ColorBox(uniqColors) };

            while (boxes.Count < maxColors)
            {
                // Find the box with largest range to split
                var box = boxes.OrderByDescending(b => Math.Max(b.RangeR, Math.Max(b.RangeG, b.RangeB))).First();
                if (box.Colors.Count <= 1) break;

                // Decide split channel
                Func<Color, int> selector = GetSelector(box);

                // Sort & split at median
                var sorted = box.Colors.OrderBy(selector).ToList();
                int mid = sorted.Count / 2;
                var box1 = new ColorBox(sorted.Take(mid).ToList());
                var box2 = new ColorBox(sorted.Skip(mid).ToList());

                boxes.Remove(box);
                boxes.Add(box1);
                boxes.Add(box2);
            }

            // Representative color of each box = average
            var palette = new List<Color>(boxes.Count);
            foreach (var b in boxes)
            {
                if (b.Colors.Count == 0)
                {
                    palette.Add(Color.Black);
                    continue;
                }
                long r = 0, g = 0, bl = 0;
                foreach (var c in b.Colors)
                {
                    r += c.R;
                    g += c.G;
                    bl += c.B;
                }
                int n = b.Colors.Count;
                palette.Add(Color.FromArgb((int)(r / n), (int)(g / n), (int)(bl / n)));
            }

            // Ensure exactly 256 entries by padding with black if needed
            while (palette.Count < 256) palette.Add(Color.Black);
            if (palette.Count > 256) palette = palette.Take(256).ToList();

            // Conventional: force index 255 to white for preview semantics (optional)
            palette[255] = Color.White;

            return palette;
        }

        private static Func<Color, int> GetSelector(ColorBox box)
        {
            if (box.RangeR >= box.RangeG && box.RangeR >= box.RangeB) return c => c.R;
            if (box.RangeG >= box.RangeR && box.RangeG >= box.RangeB) return c => c.G;
            return c => c.B;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void RgbToYCbCr(byte r, byte g, byte b, out int Y, out int Cb, out int Cr)
        {
            // integer BT.601-ish
            Y = (77 * r + 150 * g + 29 * b) >> 8;                        // 0..255
            Cb = ((-43 * r - 85 * g + 128 * b) >> 8) + 128;                 // ~16..240
            Cr = ((128 * r - 107 * g - 21 * b) >> 8) + 128;                // ~16..240
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double SrgbToLinear(byte v)
        {
            double s = v / 255.0;
            return (s <= 0.04045) ? (s / 12.92) : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte LinearToSrgb(double L)
        {
            if (L <= 0.0) return 0;
            if (L >= 1.0) return 255;
            double s = (L <= 0.0031308) ? (12.92 * L) : (1.055 * Math.Pow(L, 1.0 / 2.4) - 0.055);
            int v = (int)Math.Round(s * 255.0);
            if (v < 0) v = 0; else if (v > 255) v = 255;
            return (byte)v;
        }
        public static System.Collections.Generic.List<System.Drawing.Color> ExtractPalette(
    System.Drawing.Bitmap bmp,
    int desiredCount,
    bool dedupe,
    int mergeRadius = 6)
        {
            // Call your existing implementation:
            var palette = ExtractPalette(bmp, desiredCount);

            if (!dedupe) return palette;

            // If you track bucket populations, pass them instead of null.
            return DedupePaletteYCbCr(palette, populations: null, desiredCount: desiredCount, mergeRadius: mergeRadius);
        }
        public static System.Collections.Generic.List<System.Drawing.Color> DedupePaletteYCbCr(
    System.Collections.Generic.List<System.Drawing.Color> palette,
    System.Collections.Generic.List<int> populations = null,
    int desiredCount = 256,
    int mergeRadius = 6)
        {
            if (palette == null) throw new System.ArgumentNullException(nameof(palette));
            if (palette.Count == 0) return palette;

            // Ensure populations vector
            System.Collections.Generic.List<int> pops;
            if (populations == null || populations.Count != palette.Count)
            {
                pops = new System.Collections.Generic.List<int>(palette.Count);
                for (int i = 0; i < palette.Count; i++) pops.Add(1);
            }
            else
            {
                pops = new System.Collections.Generic.List<int>(populations);
            }

            // Prepare arrays
            int n = palette.Count;
            var y = new int[n];
            var cb = new int[n];
            var cr = new int[n];
            var rLin = new double[n];
            var gLin = new double[n];
            var bLin = new double[n];

            // Fill arrays from palette
            for (int i = 0; i < n; i++)
            {
                var c = palette[i];

                // YCbCr (integer BT.601-ish)
                int Ri = c.R, Gi = c.G, Bi = c.B;
                int Yi = (77 * Ri + 150 * Gi + 29 * Bi) >> 8;
                int Cbi = ((-43 * Ri - 85 * Gi + 128 * Bi) >> 8) + 128;
                int Cri = ((128 * Ri - 107 * Gi - 21 * Bi) >> 8) + 128;
                y[i] = Yi; cb[i] = Cbi; cr[i] = Cri;

                // linear sRGB accumulators (weighted by population)
                double sR = Ri / 255.0, sG = Gi / 255.0, sB = Bi / 255.0;
                double LR = (sR <= 0.04045) ? (sR / 12.92) : System.Math.Pow((sR + 0.055) / 1.055, 2.4);
                double LG = (sG <= 0.04045) ? (sG / 12.92) : System.Math.Pow((sG + 0.055) / 1.055, 2.4);
                double LB = (sB <= 0.04045) ? (sB / 12.92) : System.Math.Pow((sB + 0.055) / 1.055, 2.4);
                int pop = pops[i];
                rLin[i] = pop * LR;
                gLin[i] = pop * LG;
                bLin[i] = pop * LB;
            }

            int radius2 = mergeRadius * mergeRadius;

            // Pairwise merge near-duplicates (O(n^2), fine for n<=256)
            for (int i = 0; i < palette.Count; i++)
            {
                int j = i + 1;
                while (j < palette.Count)
                {
                    int dy = y[i] - y[j];
                    int dcb = cb[i] - cb[j];
                    int dcr = cr[i] - cr[j];
                    int d2 = dy * dy + dcb * dcb + dcr * dcr;

                    if (d2 <= radius2)
                    {
                        // Merge j into i (population-weighted linear RGB)
                        int popI = pops[i], popJ = pops[j];
                        int popSum = popI + popJ; if (popSum <= 0) popSum = 1;

                        rLin[i] += rLin[j];
                        gLin[i] += gLin[j];
                        bLin[i] += bLin[j];
                        pops[i] = popSum;

                        // Recompute representative from linear sums -> sRGB
                        double LRm = rLin[i] / popSum;
                        double LGm = gLin[i] / popSum;
                        double LBm = bLin[i] / popSum;

                        // Linear->sRGB
                        byte Rm, Gm, Bm;
                        {
                            double s = (LRm <= 0.0031308) ? (12.92 * LRm) : (1.055 * System.Math.Pow(LRm, 1.0 / 2.4) - 0.055);
                            int v = (int)System.Math.Round(s * 255.0); if (v < 0) v = 0; else if (v > 255) v = 255; Rm = (byte)v;
                            s = (LGm <= 0.0031308) ? (12.92 * LGm) : (1.055 * System.Math.Pow(LGm, 1.0 / 2.4) - 0.055);
                            v = (int)System.Math.Round(s * 255.0); if (v < 0) v = 0; else if (v > 255) v = 255; Gm = (byte)v;
                            s = (LBm <= 0.0031308) ? (12.92 * LBm) : (1.055 * System.Math.Pow(LBm, 1.0 / 2.4) - 0.055);
                            v = (int)System.Math.Round(s * 255.0); if (v < 0) v = 0; else if (v > 255) v = 255; Bm = (byte)v;
                        }

                        palette[i] = System.Drawing.Color.FromArgb(Rm, Gm, Bm);

                        // Recompute YCbCr for palette[i]
                        {
                            int Ri = Rm, Gi = Gm, Bi = Bm;
                            int Yi = (77 * Ri + 150 * Gi + 29 * Bi) >> 8;
                            int Cbi = ((-43 * Ri - 85 * Gi + 128 * Bi) >> 8) + 128;
                            int Cri = ((128 * Ri - 107 * Gi - 21 * Bi) >> 8) + 128;
                            y[i] = Yi; cb[i] = Cbi; cr[i] = Cri;
                        }

                        // Remove j
                        palette.RemoveAt(j);
                        pops.RemoveAt(j);

                        // Rebuild arrays aligned to new length (simple and robust)
                        int m = palette.Count;
                        var y2 = new int[m];
                        var cb2 = new int[m];
                        var cr2 = new int[m];
                        var r2 = new double[m];
                        var g2 = new double[m];
                        var b2 = new double[m];

                        for (int t = 0; t < m; t++)
                        {
                            var c2 = palette[t];
                            int Rt = c2.R, Gt = c2.G, Bt = c2.B;
                            int Yt = (77 * Rt + 150 * Gt + 29 * Bt) >> 8;
                            int Cbt = ((-43 * Rt - 85 * Gt + 128 * Bt) >> 8) + 128;
                            int Crt = ((128 * Rt - 107 * Gt - 21 * Bt) >> 8) + 128;
                            y2[t] = Yt; cb2[t] = Cbt; cr2[t] = Crt;

                            double sR = Rt / 255.0, sG = Gt / 255.0, sB = Bt / 255.0;
                            double LR = (sR <= 0.04045) ? (sR / 12.92) : System.Math.Pow((sR + 0.055) / 1.055, 2.4);
                            double LG = (sG <= 0.04045) ? (sG / 12.92) : System.Math.Pow((sG + 0.055) / 1.055, 2.4);
                            double LB = (sB <= 0.04045) ? (sB / 12.92) : System.Math.Pow((sB + 0.055) / 1.055, 2.4);
                            int pop = pops[t];
                            r2[t] = pop * LR; g2[t] = pop * LG; b2[t] = pop * LB;
                        }

                        y = y2; cb = cb2; cr = cr2; rLin = r2; gLin = g2; bLin = b2;

                        // stay at the same j (new element now at position j)
                        continue;
                    }

                    j++;
                }
            }

            // If still over target, merge the closest pairs until we hit desiredCount
            while (palette.Count > desiredCount)
            {
                int m = palette.Count;
                int bi = -1, bj = -1, bestD2 = int.MaxValue;

                for (int i = 0; i < m; i++)
                {
                    for (int j = i + 1; j < m; j++)
                    {
                        int dy = y[i] - y[j];
                        int dcb = cb[i] - cb[j];
                        int dcr = cr[i] - cr[j];
                        int d2 = dy * dy + dcb * dcb + dcr * dcr;
                        if (d2 < bestD2) { bestD2 = d2; bi = i; bj = j; }
                    }
                }
                if (bi < 0 || bj < 0) break;

                int popSum = pops[bi] + pops[bj]; if (popSum <= 0) popSum = 1;
                rLin[bi] += rLin[bj]; gLin[bi] += gLin[bj]; bLin[bi] += bLin[bj]; pops[bi] = popSum;

                // linear -> sRGB
                byte Rm2, Gm2, Bm2;
                {
                    double LRm = rLin[bi] / popSum;
                    double LGm = gLin[bi] / popSum;
                    double LBm = bLin[bi] / popSum;

                    double s = (LRm <= 0.0031308) ? (12.92 * LRm) : (1.055 * System.Math.Pow(LRm, 1.0 / 2.4) - 0.055);
                    int v = (int)System.Math.Round(s * 255.0); if (v < 0) v = 0; else if (v > 255) v = 255; Rm2 = (byte)v;
                    s = (LGm <= 0.0031308) ? (12.92 * LGm) : (1.055 * System.Math.Pow(LGm, 1.0 / 2.4) - 0.055);
                    v = (int)System.Math.Round(s * 255.0); if (v < 0) v = 0; else if (v > 255) v = 255; Gm2 = (byte)v;
                    s = (LBm <= 0.0031308) ? (12.92 * LBm) : (1.055 * System.Math.Pow(LBm, 1.0 / 2.4) - 0.055);
                    v = (int)System.Math.Round(s * 255.0); if (v < 0) v = 0; else if (v > 255) v = 255; Bm2 = (byte)v;
                }

                palette[bi] = System.Drawing.Color.FromArgb(Rm2, Gm2, Bm2);

                // Recompute YCbCr at bi
                {
                    int Ri = Rm2, Gi = Gm2, Bi2 = Bm2;
                    int Yi = (77 * Ri + 150 * Gi + 29 * Bi2) >> 8;
                    int Cbi = ((-43 * Ri - 85 * Gi + 128 * Bi2) >> 8) + 128;
                    int Cri = ((128 * Ri - 107 * Gi - 21 * Bi2) >> 8) + 128;
                    y[bi] = Yi; cb[bi] = Cbi; cr[bi] = Cri;
                }

                // remove bj and rebuild arrays
                palette.RemoveAt(bj); pops.RemoveAt(bj);

                m = palette.Count;
                var yN = new int[m];
                var cbN = new int[m];
                var crN = new int[m];
                var rN = new double[m];
                var gN = new double[m];
                var bN = new double[m];

                for (int t = 0; t < m; t++)
                {
                    var c2 = palette[t];
                    int Rt = c2.R, Gt = c2.G, Bt = c2.B;
                    int Yt = (77 * Rt + 150 * Gt + 29 * Bt) >> 8;
                    int Cbt = ((-43 * Rt - 85 * Gt + 128 * Bt) >> 8) + 128;
                    int Crt = ((128 * Rt - 107 * Gt - 21 * Bt) >> 8) + 128;
                    yN[t] = Yt; cbN[t] = Cbt; crN[t] = Crt;

                    double sR = Rt / 255.0, sG = Gt / 255.0, sB = Bt / 255.0;
                    double LR = (sR <= 0.04045) ? (sR / 12.92) : System.Math.Pow((sR + 0.055) / 1.055, 2.4);
                    double LG = (sG <= 0.04045) ? (sG / 12.92) : System.Math.Pow((sG + 0.055) / 1.055, 2.4);
                    double LB = (sB <= 0.04045) ? (sB / 12.92) : System.Math.Pow((sB + 0.055) / 1.055, 2.4);
                    int pop = pops[t];
                    rN[t] = pop * LR; gN[t] = pop * LG; bN[t] = pop * LB;
                }

                y = yN; cb = cbN; cr = crN; rLin = rN; gLin = gN; bLin = bN;
            }

            // Pad back to desiredCount if needed (safe for your writer)
            while (palette.Count < desiredCount)
                palette.Add(palette[palette.Count - 1]);

            return palette;
        }

    }
}
