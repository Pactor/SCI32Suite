using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SCI32Suite.Utils
{
    public static class BinaryUtil
    {
        public static T ReadStruct<T>(BinaryReader br) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] data = br.ReadBytes(size);
            if (data.Length != size) throw new EndOfStreamException();
            return BytesToStruct<T>(data);
        }

        public static void WriteStruct<T>(BinaryWriter bw, T value) where T : struct
        {
            byte[] data = StructToBytes(value);
            bw.Write(data);
        }

        public static T BytesToStruct<T>(byte[] arr) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                return (T)Marshal.PtrToStructure(ptr, typeof(T));
            }
            finally
            {
                handle.Free();
            }
        }

        public static byte[] StructToBytes<T>(T str) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(str, ptr, false);
                Marshal.Copy(ptr, arr, 0, size);
                return arr;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static uint FourCC(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            if (bytes.Length != 4) throw new ArgumentException("FourCC must be 4 chars");
            return BitConverter.ToUInt32(bytes, 0);
        }
    }
}
