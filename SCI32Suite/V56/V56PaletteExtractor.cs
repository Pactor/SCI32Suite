using SCI32Suite.Palette;
using SCI32Suite.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCI32Suite.V56
{
    public static class V56PaletteExtractor
    {
        private const ushort PALETTE_POS = 0x0300;
        private const ushort PALPATCH80 = 0x008B;
        private const ushort PALPATCH = 0x000B;

        public static PaletteData ExtractFirst256(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            PaletteData bestPalette = null;
            int bestCount = 0;

            for (int i = 0; i + 6 < data.Length; i++)
            {
                if (data[i] == 0x00 && data[i + 1] == 0x03) // look for 0x0300
                {
                    try
                    {
                        int nColors;
                        var pd = TryReadAt(data, i, out nColors);
                        if (pd != null && nColors > bestCount)
                        {
                            bestCount = nColors;
                            bestPalette = pd;
                            if (nColors == 256) break; // perfect match
                        }
                    }
                    catch
                    {
                        // ignore malformed chunk
                    }
                }
            }

            if (bestPalette == null)
                throw new InvalidDataException("No usable palette found in V56.");
            return bestPalette;
        }

        private static PaletteData TryReadAt(byte[] buf, int pos, out int nColors)
        {
            nColors = 0;
            using (var ms = new MemoryStream(buf, pos, buf.Length - pos, false))
            using (var br = new BinaryReader(ms, Encoding.ASCII))
            {
                ushort tag = br.ReadUInt16();
                if (tag != 0x0300) return null;

                uint size = br.ReadUInt32();
                long afterTag = ms.Position;

                ushort tcheck = br.ReadUInt16();
                if (tcheck == 0x008B || tcheck == 0x000B)
                {
                    // skip PALPATCH
                }
                else
                {
                    ms.Position = afterTag;
                }

                var comp = BinaryUtil.ReadStruct<Sci32PaletteConverter.CompPal>(br);
                nColors = comp.nColors;
                if (nColors <= 0 || nColors > 256) return null;

                var pd = PaletteData.CreateDefault();
                if (comp.type == 0)
                {
                    for (int i = 0; i < nColors; i++)
                    {
                        var e = BinaryUtil.ReadStruct<Sci32PaletteConverter.PalEntryType0>(br);
                        int idx = comp.startOffset + i;
                        if (idx >= 0 && idx < 256)
                            pd.SetColor(idx, e.red, e.green, e.blue, e.remap);
                    }
                }
                else
                {
                    for (int i = 0; i < nColors; i++)
                    {
                        var e = BinaryUtil.ReadStruct<Sci32PaletteConverter.PalEntryOld>(br);
                        int idx = comp.startOffset + i;
                        if (idx >= 0 && idx < 256)
                            pd.SetColor(idx, e.red, e.green, e.blue, 0);
                    }
                }
                return pd;
            }
        }


    }
}
