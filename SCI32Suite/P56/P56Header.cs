using System.Runtime.InteropServices;

namespace SCI32Suite.P56
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct PicHeader32
    {
        public ushort picHeaderSize;   // 14
        public byte celCount;        // 1
        public byte splitFlag;       // 0
        public ushort celHeaderSize;   // 0x2A (42)
        public uint paletteOffset;   // ABS offset to palette block
        public ushort resX;            // 640
        public ushort resY;            // 480
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct CelHeaderPic
    {
        public ushort W;               // 640
        public ushort H;               // 480
        public short xShift;          // 0 (critical for centering)
        public short yShift;          // 0
        public byte transparent;     // 255 ("no transparency" for login images)
        public byte compressType;    // 0  (legacy/uncompressed)
        public ushort dataFlags;       // 0
        public int dataByteCount;   // 307200 (W*H), raw pixel count only
        public int controlByteCount;// 0
        public int paletteOffsetCell; // 0 (unused)
        public int controlOffset;   // ABS offset to RAW PIXEL BUFFER
        public int colorOffset;     // 0
        public int rowTableOffset;  // 0
        public short priority;        // 0
        public short xpos;            // 0
        public short ypos;            // 0
    }
    /// <summary>
    /// 62-byte header at the start of every P56 file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct P56Header
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] Signature;      // usually "P5" (0x50 0x35)

        public ushort Width;          // image width in pixels
        public ushort Height;         // image height in pixels
        public uint PaletteOffset;    // absolute offset of CompPal block
        public uint ImageOffset;      // absolute offset of image data

        // Remaining bytes reserved
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 62 - 2 - 2 - 2 - 4 - 4)]
        public byte[] Reserved;

        public bool IsValid
        {
            get
            {
                return Signature != null
                       && Signature.Length == 2
                       && Signature[0] == (byte)'P'
                       && Signature[1] == (byte)'5';
            }
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct P56ResourceHeader   // 18 bytes after 4-byte tag
        {
            public uint PictureType;       // 0x00008181
            public ushort CellTableOffset; // 14
            public byte NumCells;         // 1
            public byte IsCompressed;     // 0
            public ushort CellRecSize;     // 42
            public uint PaletteOffset;    // 62
            public ushort Width;           // image width
            public ushort Height;          // image height
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct P56CellRecord      // 42 bytes
        {
            public ushort Width;
            public ushort Height;
            public ushort XShift;
            public ushort YShift;
            public byte Transparent;
            public byte Compression;
            public ushort Flags;
            public uint ImageAndPackSize;
            public uint ImageSize;
            public uint PaletteOffset;
            public uint ImageOffset;
            public uint PackedDataOffset;
            public uint ScanLinesTableOffset;
            public short ZDepth;
            public short XPos;
            public short YPos;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct P56PaletteHeader   // 41 bytes
        {
            public uint LengthAfterThisField;   // 802
            public ushort Type;                 // 0x000E
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
            public byte[] Reserved1;            // specific pattern
            public ushort DataLen;              // 787
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
            public byte[] Reserved2;
            public ushort FirstColor;
            public ushort Unknown1;
            public ushort NumColors;            // 255
            public byte ExFour;                 // 1
            public byte Triple;                 // 1
            public uint Reserved3;
        }
        public override string ToString()
        {
            return $"P56Header: {Width}x{Height}, PaletteOffset={PaletteOffset}, ImageOffset={ImageOffset}";
        }
    }
}
