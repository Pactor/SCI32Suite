using System;
using System.Collections.Generic;
using System.Drawing;

namespace SCI32Suite.Palette
{
    public sealed class PaletteData
    {
        private readonly byte[] _r = new byte[256];
        private readonly byte[] _g = new byte[256];
        private readonly byte[] _b = new byte[256];
        private readonly byte[] _meta = new byte[256]; // RIFF flags or SCI32 remap if desired

        public int Count { get { return 256; } }

        public static PaletteData CreateDefault()
        {
            var p = new PaletteData();
            for (int i = 0; i < 256; i++)
            {
                p._r[i] = (byte)i;
                p._g[i] = (byte)i;
                p._b[i] = (byte)i;
                p._meta[i] = 0;
            }
            return p;
        }

        public static PaletteData FromRgbList(IList<Color> colors)
        {
            var p = new PaletteData();
            int n = Math.Min(256, colors.Count);
            for (int i = 0; i < 256; i++)
            {
                Color c = i < n ? colors[i] : Color.Black;
                p._r[i] = c.R;
                p._g[i] = c.G;
                p._b[i] = c.B;
                p._meta[i] = 0;
            }
            return p;
        }

        public static PaletteData FromRiff(RiffPalette riff)
        {
            var p = new PaletteData();
            int n = Math.Min(256, riff.Entries.Length);
            for (int i = 0; i < 256; i++)
            {
                if (i < n)
                {
                    p._r[i] = riff.Entries[i].Red;
                    p._g[i] = riff.Entries[i].Green;
                    p._b[i] = riff.Entries[i].Blue;
                    p._meta[i] = riff.Entries[i].Flags;
                }
                else
                {
                    p._r[i] = 0; p._g[i] = 0; p._b[i] = 0; p._meta[i] = 0;
                }
            }
            return p;
        }

        public RiffPalette ToRiff()
        {
            var entries = new RiffPalette.PaletteEntry[256];
            for (int i = 0; i < 256; i++)
            {
                var e = new RiffPalette.PaletteEntry
                {
                    Red = _r[i],
                    Green = _g[i],
                    Blue = _b[i],
                    Flags = _meta[i]
                };
                entries[i] = e;
            }
            return new RiffPalette
            {
                Version = 0x0300,
                NumEntries = 256,
                Entries = entries
            };
        }

        public Color GetColor(int index)
        {
            return Color.FromArgb(_r[index], _g[index], _b[index]);
        }

        public void SetColor(int index, byte r, byte g, byte b, byte meta)
        {
            _r[index] = r; _g[index] = g; _b[index] = b; _meta[index] = meta;
        }

        public void CopyFrom(PaletteData other)
        {
            for (int i = 0; i < 256; i++)
            {
                _r[i] = other._r[i];
                _g[i] = other._g[i];
                _b[i] = other._b[i];
                _meta[i] = other._meta[i];
            }
        }
    }
}
