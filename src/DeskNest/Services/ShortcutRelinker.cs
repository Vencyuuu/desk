using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskNest.Services;

public static class ShortcutRelinker
{
    // Shell Link 是 STA COM 对象；在独立 STA 线程中读取和保存，绝不执行快捷方式。
    public static Task RebaseAsync(string filesRoot, string oldRoot, string newRoot)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Visit(filesRoot, oldRoot, newRoot); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static void Visit(string directory, string oldRoot, string newRoot)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if ((attributes & FileAttributes.Directory) != 0) { Visit(path, oldRoot, newRoot); continue; }
            if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            var link = (IShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("00021401-0000-0000-C000-000000000046"))!)!;
            try
            {
                var persist = (IPersistFile)link;
                persist.Load(path, 0);
                var buffer = new StringBuilder(32768);
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
                var changed = false;
                string Rebase(string value)
                {
                    var next = DataPaths.Rebase(value, oldRoot, newRoot);
                    if (next != value && (File.Exists(next) || Directory.Exists(next)))
                    { changed = true; return next; }
                    return value;
                }
                var original = buffer.ToString();
                var rebased = Rebase(original);
                if (rebased != original) link.SetPath(rebased);
                link.GetWorkingDirectory(buffer, buffer.Capacity);
                original = buffer.ToString();
                rebased = Rebase(original);
                if (rebased != original) link.SetWorkingDirectory(rebased);
                link.GetIconLocation(buffer, buffer.Capacity, out var iconIndex);
                original = buffer.ToString();
                rebased = Rebase(original);
                if (rebased != original) link.SetIconLocation(rebased, iconIndex);
                if (changed) persist.Save(path, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
