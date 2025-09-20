using SCI32Suite.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SCI32Suite.Palette
{
    public static class Sci32PaletteConverter
    {
        // Tag and constants from your SCI32 palette layout
        private const ushort PALETTE_POS = 0x0300;
        private const ushort PALPATCH80 = 0x008B;
        private const ushort PALPATCH = 0x000B;

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        public struct PalHeader
        {
            public short palID;
            public byte hdSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
            public string palName;
            public byte palCount;
            public short reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        public struct CompPal
        {
            // PalHeader (15 bytes)
            public short palID;
            public byte hdSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 9)]
            public string palName;
            public byte palCount;
            public short reserved;

            // Tail (22 bytes)
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 10)]
            public string title;
            public byte startOffset;
            public byte nCycles;
            public ushort fe;
            public ushort nColors;
            public byte def;
            public byte type;
            public uint valid;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct PalEntryType0 // [remap, R, G, B]
        {
            public byte remap;
            public byte red;
            public byte green;
            public byte blue;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct PalEntryOld // [R, G, B]
        {
            public byte red;
            public byte green;
            public byte blue;
        }

        public static PaletteData FromRiff(PaletteData riff, byte defaultRemap)
        {
            var p = PaletteData.CreateDefault();
            for (int i = 0; i < 256; i++)
            {
                var c = riff.GetColor(i);
                p.SetColor(i, c.R, c.G, c.B, defaultRemap);
            }
            return p;
        }

        public static PaletteData ReadSci32Block(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs, Encoding.ASCII))
            {
                ushort tag = br.ReadUInt16();
                if (tag != PALETTE_POS) throw new InvalidDataException("Missing PALETTE_POS (0x0300)");

                uint size = br.ReadUInt32();

                long afterTagPos = fs.Position;
                ushort tcheck = br.ReadUInt16();
                if (tcheck == PALPATCH80 || tcheck == PALPATCH)
                {
                    // okay, CompPal starts after this
                }
                else
                {
                    fs.Position = afterTagPos; // no PALPATCH, roll back
                }

                var comp = BinaryUtil.ReadStruct<CompPal>(br);
                int n = comp.nColors;
                var pd = PaletteData.CreateDefault();

                if (comp.type == 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        var e = BinaryUtil.ReadStruct<PalEntryType0>(br);
                        int idx = comp.startOffset + i;
                        if (idx >= 0 && idx < 256)
                        {
                            pd.SetColor(idx, e.red, e.green, e.blue, e.remap);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        var e = BinaryUtil.ReadStruct<PalEntryOld>(br);
                        int idx = comp.startOffset + i;
                        if (idx >= 0 && idx < 256)
                        {
                            pd.SetColor(idx, e.red, e.green, e.blue, 0);
                        }
                    }
                }

                return pd;
            }
        }

        public static void WriteSci32Block(string path, PaletteData sciPal)
        {
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs, Encoding.ASCII))
            {
                // Build CompPal
                var comp = new CompPal
                {
                    palID = 0,
                    hdSize = 15,
                    palName = "Custom",
                    palCount = 1,
                    reserved = 0,
                    title = "CustomPal",
                    startOffset = 0,
                    nCycles = 0,
                    fe = 0,
                    nColors = 256,
                    def = 1,
                    type = 0,   // we will write [remap,R,G,B]
                    valid = 0
                };

                // Compute block size: header + entries
                int compSize = Marshal.SizeOf(typeof(CompPal));
                int entrySize = Marshal.SizeOf(typeof(PalEntryType0));
                uint blockSize = (uint)(compSize + 256 * entrySize);

                // Tag
                bw.Write((ushort)PALETTE_POS);
                bw.Write(blockSize);

                // No PALPATCH tag for simplicity; write CompPal immediately
                BinaryUtil.WriteStruct(bw, comp);

                // Entries
                for (int i = 0; i < 256; i++)
                {
                    var c = sciPal.GetColor(i);
                    byte remap = 0; // simple/safe; set non-zero if you understand runtime remapping
                    var e = new PalEntryType0 { remap = remap, red = c.R, green = c.G, blue = c.B };
                    BinaryUtil.WriteStruct(bw, e);
                }
            }
        }

        public static PaletteData ToRiff(PaletteData sci)
        {
            var p = PaletteData.CreateDefault();
            for (int i = 0; i < 256; i++)
            {
                var c = sci.GetColor(i);
                p.SetColor(i, c.R, c.G, c.B, 0);
            }
            return p;
        }
    }
}
