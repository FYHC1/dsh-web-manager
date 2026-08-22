using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DshWebManager
{
    /// <summary>Native interop used for window icon (WM_SETICON) and window placement.</summary>
    public static class Win32
    {
        public const int WM_SETICON = 0x0080;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;

        public const int IMAGE_ICON = 1;
        public const int LR_LOADFROMFILE = 0x0010;

        public const int SW_RESTORE = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, uint nIconIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        /// <summary>Loads a 32px (or 16px) icon from an .ico file yielding a new HICON (caller must destroy).</summary>
        public static IntPtr LoadAppIcon(string icoPath, bool small)
        {
            int px = small ? 16 : 32;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (System.IO.File.Exists(icoPath))
                    h = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON, px, px, LR_LOADFROMFILE);
            }
            catch { h = IntPtr.Zero; }
            if (h != IntPtr.Zero) return h;
            // Fallback: extract the first icon from our own EXE (win32icon resource).
            try { return ExtractIcon(IntPtr.Zero, AppPaths.ExePath, 0); }
            catch { return IntPtr.Zero; }
        }

        // ---- AppUserModelID support (drives the taskbar icon/grouping) ----
        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pwszVal;
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, out PropVariant pv);
            int SetValue(ref PropertyKey key, ref PropVariant pv);
            int Commit();
        }

        [DllImport("shell32.dll", PreserveSig = true)]
        public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IntPtr ppv);

        public const ushort VT_LPWSTR = 31;
    }
}