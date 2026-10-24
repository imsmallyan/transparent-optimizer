using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace PcOptimizer
{
    public interface IFileDropTarget
    {
        void AcceptFiles(string[] paths);
    }

    static class AdminDrop
    {
        const int WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049;
        const uint MSGFLT_ALLOW = 1;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int message, uint action, IntPtr pChangeFilterStruct);

        [DllImport("shell32.dll")]
        static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        static extern void DragFinish(IntPtr hDrop);

        [DllImport("ole32.dll")]
        static extern int RevokeDragDrop(IntPtr hwnd);

        public static void RevokeOle(Window window) =>
            RevokeDragDrop(((HwndSource)PresentationSource.FromVisual(window)).Handle);

        public static void Enable(Window window, Action<string[]> onDrop)
        {
            var src = (HwndSource)PresentationSource.FromVisual(window);
            IntPtr hwnd = src.Handle;
            RevokeDragDrop(hwnd);
            foreach (int msg in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
                ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero);
            DragAcceptFiles(hwnd, true);
            src.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_DROPFILES) return IntPtr.Zero;
                handled = true;
                try
                {
                    uint n = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
                    var paths = new string[n];
                    for (uint i = 0; i < n; i++)
                    {
                        uint len = DragQueryFile(wParam, i, null, 0);
                        var sb = new StringBuilder((int)len + 1);
                        DragQueryFile(wParam, i, sb, (uint)sb.Capacity);
                        paths[i] = sb.ToString();
                    }
                    onDrop(paths);
                }
                finally { DragFinish(wParam); }
                return IntPtr.Zero;
            });
        }
    }
}
