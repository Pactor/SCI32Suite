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
    public sealed class RiffPalette
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RiffHeader
        {
            public uint Riff;     // 'RIFF'
            public uint Size;     // file size - 8
            public uint Pal;      // 'PAL '
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DataHeader
        {
            public uint Data;     // 'data'
            public uint Size;     // chunk size
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct LogPaletteHeader
        {
            public ushort Version;     // 0x0300
            public ushort NumEntries;  // number of entries
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct PaletteEntry
        {
            public byte Red;
            public byte Green;
            public byte Blue;
            public byte Flags;
        }

        public ushort Version { get; set; }
        public ushort NumEntries { get; set; }
        public PaletteEntry[] Entries { get; set; }

        public static RiffPalette Read(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs, Encoding.ASCII))
            {
                var rh = BinaryUtil.ReadStruct<RiffHeader>(br);
                if (rh.Riff != BinaryUtil.FourCC("RIFF")) throw new InvalidDataException("Not RIFF");
                if (rh.Pal != BinaryUtil.FourCC("PAL ")) throw new InvalidDataException("Not RIFF PAL");

                var dh = BinaryUtil.ReadStruct<DataHeader>(br);
                if (dh.Data != BinaryUtil.FourCC("data")) throw new InvalidDataException("Missing 'data' chunk");

                var lph = BinaryUtil.ReadStruct<LogPaletteHeader>(br);
                var n = lph.NumEntries;
                if (n <= 0 || n > 256) throw new InvalidDataException("Unsupported entry count");

                var entries = new PaletteEntry[256];
                for (int i = 0; i < n; i++)
                {
                    entries[i] = BinaryUtil.ReadStruct<PaletteEntry>(br);
                }
                for (int i = n; i < 256; i++) entries[i] = default(PaletteEntry);

                return new RiffPalette
                {
                    Version = lph.Version,
                    NumEntries = 256,
                    Entries = entries
                };
            }
        }

        public static void Write(string path, RiffPalette pal)
        {
            if (pal.Entries == null || pal.Entries.Length < 1) throw new InvalidDataException("No entries");

            ushort n = 256;
            uint dataSize = (uint)(Marshal.SizeOf(typeof(LogPaletteHeader)) + n * Marshal.SizeOf(typeof(PaletteEntry)));
            uint riffSize = 4 + 8 + dataSize; // 'PAL ' + 'data'+size + payload

            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs, Encoding.ASCII))
            {
                var rh = new RiffHeader { Riff = BinaryUtil.FourCC("RIFF"), Size = riffSize, Pal = BinaryUtil.FourCC("PAL ") };
                BinaryUtil.WriteStruct(bw, rh);

                var dh = new DataHeader { Data = BinaryUtil.FourCC("data"), Size = dataSize };
                BinaryUtil.WriteStruct(bw, dh);

                var lph = new LogPaletteHeader { Version = pal.Version == 0 ? (ushort)0x0300 : pal.Version, NumEntries = n };
                BinaryUtil.WriteStruct(bw, lph);

                for (int i = 0; i < n; i++)
                {
                    var e = i < pal.Entries.Length ? pal.Entries[i] : default(PaletteEntry);
                    BinaryUtil.WriteStruct(bw, e);
                }
            }
        }
    }
}
