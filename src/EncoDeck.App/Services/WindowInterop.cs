// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using EncoDeck.Platform.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace EncoDeck.App.Services;

/// <summary>Win32 helpers for the tray panel and connection popup windows.</summary>
internal static partial class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_BORDER = 0x00800000;
    private const long WS_DLGFRAME = 0x00400000;
    private const long WS_SYSMENU = 0x00080000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_MINIMIZEBOX = 0x00020000;
    private const long WS_MAXIMIZEBOX = 0x00010000;
    private const long WS_EX_DLGMODALFRAME = 0x00000001;
    private const long WS_EX_WINDOWEDGE = 0x00000100;
    private const long WS_EX_CLIENTEDGE = 0x00000200;
    private const long WS_EX_STATICEDGE = 0x00020000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint GA_ROOT = 2;
    private const uint PW_RENDERFULLCONTENT = 0x2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static nint GetHandle(Window window) => WindowNative.GetWindowHandle(window);

    public static AppWindow GetAppWindow(Window window) =>
        AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(GetHandle(window)));

    /// <summary>Non-resizable, always-on-top window without title bar, hidden from Alt+Tab and the taskbar.</summary>
    /// <param name="border">
    /// Keep the thin window border. Frameless windows have no non-client area at all, which a
    /// layered (click-through) window would otherwise paint black.
    /// </param>
    public static void MakeFlyout(Window window, bool noActivate, bool border = true)
    {
        var appWindow = GetAppWindow(window);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(border, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        appWindow.IsShownInSwitchers = false;

        nint hwnd = GetHandle(window);
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        if (noActivate) style |= WS_EX_NOACTIVATE;
        if (!border) style &= ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)style);

        if (!border)
        {
            // A plain popup: no frame of any kind, so the client area is the whole window.
            long windowStyle = GetWindowLongPtr(hwnd, GWL_STYLE);
            windowStyle &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            windowStyle |= WS_POPUP;
            SetWindowLongPtr(hwnd, GWL_STYLE, (nint)windowStyle);
            _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// Makes the window layered (fully opaque) so mouse click-through can be switched on and off at
    /// run time with <see cref="SetClickThrough"/>. Call once, before the window is shown.
    /// </summary>
    public static void MakeLayered(Window window)
    {
        nint hwnd = GetHandle(window);
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(style | WS_EX_LAYERED));
        _ = SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
    }

    /// <summary>Opacity of a layered window (0 = invisible, 255 = opaque).</summary>
    public static void SetWindowAlpha(Window window, byte alpha) =>
        _ = SetLayeredWindowAttributes(GetHandle(window), 0, alpha, LWA_ALPHA);

    /// <summary>
    /// Click-through: the system skips a layered window with WS_EX_TRANSPARENT when it routes mouse
    /// input, so every click and move goes to the window underneath (e.g. a fullscreen game).
    /// </summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        nint hwnd = GetHandle(window);
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED;
        style = enabled ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)style);
        _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Window rectangle in screen pixels.</summary>
    public static PixelRect GetWindowBounds(Window window)
    {
        _ = GetWindowRect(GetHandle(window), out var w);
        return PixelRect.FromLtrb(w.Left, w.Top, w.Right, w.Bottom);
    }

    /// <summary>Window and client rectangles in screen pixels (diagnostics: a frame shows as a difference).</summary>
    public static (PixelRect Window, PixelRect Client) GetBounds(Window window)
    {
        nint hwnd = GetHandle(window);
        _ = GetWindowRect(hwnd, out var w);
        _ = GetClientRect(hwnd, out var c);
        var origin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(hwnd, ref origin);
        return (PixelRect.FromLtrb(w.Left, w.Top, w.Right, w.Bottom), new PixelRect(origin.X, origin.Y, c.Right - c.Left, c.Bottom - c.Top));
    }

    /// <summary>True when the mouse at <paramref name="point"/> (physical pixels) would reach this window.</summary>
    public static bool ReceivesMouseAt(Window window, PixelPoint point)
    {
        nint hit = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
        return hit != 0 && GetAncestor(hit, GA_ROOT) == GetHandle(window);
    }

    /// <summary>Renders only this window (PW_RENDERFULLCONTENT) into a 32-bit BGRA buffer; for diagnostics.</summary>
    public static (int Width, int Height, byte[] Bgra)? CaptureWindow(Window window)
    {
        nint hwnd = GetHandle(window);
        if (!GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) return null;
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        nint bitmap = CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
        nint old = SelectObject(dc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)) return null;
            var buffer = new byte[width * height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return (width, height, buffer);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            _ = ReleaseDC(0, screen);
        }
    }

    /// <summary>Diagnostics: visible, DWM cloak flags (0 = not cloaked) and layered alpha of the window.</summary>
    public static string DescribeVisibility(Window window)
    {
        nint hwnd = GetHandle(window);
        int cloaked = 0;
        _ = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
        _ = GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _);
        return $"visible={IsWindowVisible(hwnd)} cloaked={cloaked} alpha={alpha} exstyle=0x{GetWindowLongPtr(hwnd, GWL_EXSTYLE):X}";
    }

    /// <summary>
    /// Diagnostics: what is on screen in <paramref name="rect"/> (physical pixels): the desktop as
    /// DWM composed it, exactly what the user sees there, blending and all. (Not CAPTUREBLT: that
    /// makes GDI compose layered windows itself from redirection bitmaps, which WinUI's composition
    /// content does not have, so the pop-up would be missing.)
    /// </summary>
    public static (int Width, int Height, byte[] Bgra)? CaptureScreen(PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = rect.Width, biHeight = -rect.Height, biPlanes = 1, biBitCount = 32 };
        nint bitmap = CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
        nint old = SelectObject(dc, bitmap);
        try
        {
            if (!BitBlt(dc, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, SRCCOPY)) return null;
            var buffer = new byte[rect.Width * rect.Height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return (rect.Width, rect.Height, buffer);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            _ = ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// Brings a window the user just asked for (tray click, "open EncoDeck") to the foreground with
    /// keyboard focus. Windows refuses <c>SetForegroundWindow</c> when another thread owns the
    /// foreground; attaching to that thread's input queue for the call is the documented way around
    /// it for windows opened in direct response to the user.
    /// </summary>
    public static void BringToForeground(Window window)
    {
        nint hwnd = GetHandle(window);
        nint foreground = GetForegroundWindow();
        if (foreground == hwnd) return;
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;

        uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
            _ = SetFocus(hwnd);
        }
        finally
        {
            if (attached) _ = AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    /// <summary>DPI scale of the monitor the window is on.</summary>
    public static double GetScale(Window window) => GetDpiForWindow(GetHandle(window)) / 96.0;

    /// <summary>Moves the window (physical pixels) without resizing, activating or re-laying it out; for animations.</summary>
    public static void MoveTo(Window window, int x, int y) =>
        _ = SetWindowPos(GetHandle(window), 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// Puts the window in the topmost band without activating it. Needed after showing a
    /// non-activating window: the always-on-top state set before it was shown does not always
    /// survive (seen while another app is in the foreground), and the pop-up then opens behind it.
    /// Returns the Win32 error, 0 on success. Windows can still keep the window out of the topmost
    /// band without an error (seen on build 26340 while the user types in another app).
    /// </summary>
    public static int EnsureTopmost(Window window) =>
        SetWindowPosChecked(GetHandle(window), HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) ? 0 : Marshal.GetLastPInvokeError();

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPosChecked(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly nint HWND_TOPMOST = -1;

    /// <summary>True while the window is in the topmost band.</summary>
    public static bool IsTopmost(Window window) => (GetWindowLongPtr(GetHandle(window), GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Moves and sizes the window in physical pixels. SetWindowPos directly: the flyouts are not
    /// resizable by the user, and this is the API that still applies a programmatic size (Quick
    /// Settings can be wider or narrower than when the hidden panel was last shown).
    /// </summary>
    public static void MoveAndResize(Window window, PixelRect rect) =>
        _ = SetWindowPos(GetHandle(window), 0, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// The parts of a framed window that are not drawn, physical pixels per side: the window
    /// rectangle includes invisible resize borders around what DWM shows (9 px each side but the
    /// top at 150 %), and the visible frame keeps a thin border around the client area.
    /// </summary>
    public static (FrameInsets Invisible, FrameInsets Border) GetFrame(Window window)
    {
        nint hwnd = GetHandle(window);
        if (!GetWindowRect(hwnd, out var w) || !GetClientRect(hwnd, out var c)
            || DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var v, Marshal.SizeOf<RECT>()) != 0)
        {
            return (default, default);
        }
        var origin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(hwnd, ref origin);
        var invisible = new FrameInsets(v.Left - w.Left, v.Top - w.Top, w.Right - v.Right, w.Bottom - v.Bottom);
        var border = new FrameInsets(origin.X - v.Left, origin.Y - v.Top, v.Right - origin.X - c.Right, v.Bottom - origin.Y - c.Bottom);
        return (invisible.Clamped(), border.Clamped());
    }

    /// <summary>The tray icon's rectangle (physical pixels), or null. Returns the overflow chevron when the icon is hidden there.</summary>
    public static PixelRect? GetTrayIconRect(Guid iconId)
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), guidItem = iconId };
        if (Shell_NotifyIconGetRect(ref id, out var r) != 0) return null;
        return PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>A width per side, physical pixels.</summary>
    public readonly record struct FrameInsets(int Left, int Top, int Right, int Bottom)
    {
        public int Horizontal => Left + Right;
        public int Vertical => Top + Bottom;

        /// <summary>The rectangle grown by these insets on every side.</summary>
        public PixelRect Around(PixelRect rect) => PixelRect.FromLtrb(rect.X - Left, rect.Y - Top, rect.Right + Right, rect.Bottom + Bottom);

        internal FrameInsets Clamped() => new(Math.Max(0, Left), Math.Max(0, Top), Math.Max(0, Right), Math.Max(0, Bottom));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint dc, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    private const uint SRCCOPY = 0x00CC0020;

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private const int DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowRect(nint hwnd, int attribute, out RECT value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLayeredWindowAttributes(nint hwnd, out uint colorKey, out byte alpha, out uint flags);
}
