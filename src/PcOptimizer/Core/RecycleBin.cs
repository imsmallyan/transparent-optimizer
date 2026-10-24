using System;
using System.Runtime.InteropServices;

namespace PcOptimizer.Core
{
    public static class RecycleBin
    {
        public static (long Count, long Bytes)? Query()
        {
            try
            {
                var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
                if (NativeMethods.SHQueryRecycleBin(null, ref info) != 0) return null;
                return (info.i64NumItems, info.i64Size);
            }
            catch (Exception) { return null; }
        }

        public static bool Empty()
        {
            int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
            OpLog.Write("EMPTY_RECYCLE_BIN", $"hr=0x{hr:X8}");
            return hr == 0 || hr == unchecked((int)0x8000FFFF);
        }

        public static void Send(string path)
        {
            var op = new NativeMethods.SHFILEOPSTRUCT
            {
                wFunc = NativeMethods.FO_DELETE,
                pFrom = System.IO.Path.GetFullPath(path) + "\0\0",
                fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION |
                                  NativeMethods.FOF_SILENT | NativeMethods.FOF_WANTNUKEWARNING),
            };
            int rc = NativeMethods.SHFileOperation(ref op);
            if (rc != 0) throw new System.IO.IOException(L.F("移到回收站失败（错误码 0x{0:X}），可能有文件正在使用。", rc));
            if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                throw new System.IO.IOException(L.T("没有全部移到回收站（可能有文件正在使用，或已取消）"));
        }
    }
}
