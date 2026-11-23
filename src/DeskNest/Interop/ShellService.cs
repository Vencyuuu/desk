using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskNest.Interop;

public static class ShellService
{
    public static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("文件已被移动或删除，请刷新分区。", path);
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path),
        });
    }

    public static void Reveal(string path) => Process.Start(new ProcessStartInfo("explorer.exe")
    {
        Arguments = $"/select,\"{path}\"",
        UseShellExecute = true,
    });

    public static ImageSource? GetIcon(string path)
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        try { return ExtractIcon(path); }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    private static ImageSource? ExtractIcon(string path)
    {
        // 从系统提取真实关联图标，保留快捷方式/文件类型识别；立即释放原生 HICON。
        var handle = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
        if (handle == IntPtr.Zero || info.Icon == IntPtr.Zero)
            return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(40, 40));
            image.Freeze();
            return image;
        }
        finally { DestroyIcon(info.Icon); }
    }

    public static void ShowContextMenu(Window window, string path, Point screenPoint)
    {
        var owner = new WindowInteropHelper(window).Handle;
        var itemId = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item));
        object? menuObject = null;
        var menu = IntPtr.Zero;
        HwndSourceHook? hook = null;
        var source = HwndSource.FromHwnd(owner);
        try
        {
            // 直接向 Shell 索取文件的菜单，保留第三方扩展，而不是仿造常用命令列表。
            var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5");
            var interfaceId = typeof(IContextMenu).GUID;
            item.BindToHandler(IntPtr.Zero, ref handler, ref interfaceId, out menuObject);
            var context = (IContextMenu)menuObject;
            var context3 = menuObject as IContextMenu3;
            var context2 = menuObject as IContextMenu2;
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                throw new InvalidOperationException("无法创建系统菜单。");
            Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, 1, 0x7FFF, 0));
            hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                // 转发子菜单和自绘消息，否则“发送到”等动态菜单及部分压缩扩展无法工作。
                if (message is 0x117 or 0x2B or 0x2C or 0x120)
                {
                    if (context3 != null && context3.HandleMenuMsg2((uint)message, wParam, lParam, out var result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                    if (context2 != null && message != 0x120 && context2.HandleMenuMsg((uint)message, wParam, lParam) == 0)
                        handled = true;
                }
                return IntPtr.Zero;
            };
            source?.AddHook(hook);
            SetForegroundWindow(owner);
            var command = TrackPopupMenuEx(menu, 0x100 | 0x2,
                (int)screenPoint.X, (int)screenPoint.Y, owner, IntPtr.Zero);
            if (command == 0)
                return;
            var invoke = new CommandInfo
            {
                Size = Marshal.SizeOf<CommandInfo>(),
                Owner = owner,
                Verb = new IntPtr(command - 1),
                Show = 1,
            };
            Marshal.ThrowExceptionForHR(context.InvokeCommand(ref invoke));
        }
        finally
        {
            if (hook != null) source?.RemoveHook(hook);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (menuObject != null) Marshal.ReleaseComObject(menuObject);
            Marshal.ReleaseComObject(item);
        }
    }

    public static IReadOnlyList<string> InspectContextMenu(string path)
    {
        // 诊断入口只查询菜单，不显示窗口、也不执行命令，便于隔离验证 COM 接口布局。
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        object? contextObject = null;
        var menu = CreatePopupMenu();
        try
        {
            var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5");
            iid = typeof(IContextMenu).GUID;
            item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out contextObject);
            Marshal.ThrowExceptionForHR(((IContextMenu)contextObject).QueryContextMenu(menu, 0, 1, 0x7FFF, 0));
            var labels = new List<string>();
            for (var index = 0; index < GetMenuItemCount(menu); index++)
            {
                var text = new StringBuilder(512);
                GetMenuString(menu, (uint)index, text, text.Capacity, 0x400);
                if (text.Length > 0) labels.Add(text.ToString());
            }
            return labels;
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (contextObject != null) Marshal.ReleaseComObject(contextObject);
            Marshal.ReleaseComObject(item);
        }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(
            IntPtr context, ref Guid handler, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint name, out IntPtr text);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint max);
    }

    // COM 接口按原生虚表顺序声明，不能使用普通 C# 接口继承省略前三个槽位。
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
}
