using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskNest.Interop;

public static class DesktopLayer
{
    public static IntPtr FindDesktopView()
    {
        // 挂在图标视图内部，而不是壁纸后面的 WorkerW，以保留鼠标、拖放和键盘交互。
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            result = view;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static bool Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var desktop = FindDesktopView();
        if (handle == IntPtr.Zero || desktop == IntPtr.Zero) return false;
        GetWindowRect(handle, out var original);
        var style = GetWindowLongPtr(handle, -16).ToInt64();
        var extended = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -16, new IntPtr((style | 0x40000000L | 0x04000000L) & ~0x80000000L));
        SetWindowLongPtr(handle, -20, new IntPtr((extended | 0x80) & ~0x40000));
        SetLastError(0);
        var previous = SetParent(handle, desktop);
        if (previous == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
        {
            SetWindowLongPtr(handle, -16, new IntPtr(style));
            return false;
        }
        // 仅修改窗口样式 / 父级时刷新非客户区，普通拖动不重复发送框架变化消息。
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x01 | 0x02 | 0x04 | 0x10 | 0x20);
        Place(window, original.Left, original.Top, original.Width, original.Height);
        return GetParent(handle) == desktop;
    }

    public static bool IsAttached(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var parent = GetParent(handle);
        return handle != IntPtr.Zero && IsWindow(handle) && parent != IntPtr.Zero &&
            parent == FindDesktopView();
    }

    public static NativeRect Bounds(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
        return bounds;
    }

    public static NativePoint Cursor()
    {
        GetCursorPos(out var point);
        return point;
    }

    public static void Place(Window window, int x, int y, int width, int height)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        GetWindowRect(handle, out var current);
        if (current.Left == x && current.Top == y && current.Width == width && current.Height == height) return;
        var point = new NativePoint { X = x, Y = y };
        var parent = GetParent(handle);
        if (parent != IntPtr.Zero) ScreenToClient(parent, ref point);
        // 原生移动绕过子窗口不支持的 Window.DragMove，位置使用物理屏幕坐标。
        // 不抢占同级窗口层级，不触发 SWP_FRAMECHANGED，减少透明子窗口闪烁及无关布局反馈。
        if (!SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, width, height, 0x10 | 0x04))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void Resize(Window window, double width, double height)
    {
        var bounds = Bounds(window);
        var dpi = VisualTreeHelper.GetDpi(window);
        window.Width = width;
        window.Height = height;
        if (IsAttached(window))
            Place(window, bounds.Left, bounds.Top, (int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X, Y; }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
