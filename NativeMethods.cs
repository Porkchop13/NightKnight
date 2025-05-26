// NativeMethods.cs - Contains P/Invoke declarations for Windows API functions
// used by the NightKnight application.

using System.Runtime.InteropServices;

namespace NightKnight
{
    internal static partial class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        internal static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;        // x position of upper-left corner
            public int Top;         // y position of upper-left corner
            public int Right;       // x position of lower-right corner
            public int Bottom;      // y position of lower-right corner
        }

        internal const int SW_SHOW = 5;
        internal const int SW_SHOWNORMAL = 1;
    }
}
