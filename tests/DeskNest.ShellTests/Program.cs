using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 测试替身与真正程序同名，扩展启动它后仅回执清单，绝不移动用户文件。
        if (args.Length == 2 && args[0] == "--shell-request")
        {
            File.Copy(args[1], args[1] + ".received", true);
            return 0;
        }
        if (args.Length < 2) throw new ArgumentException("需要原生扩展路径和测试输出目录。");
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var libraryPath = Path.Combine(AppContext.BaseDirectory, "DeskNest.Shell.dll");
        File.Copy(Path.GetFullPath(args[0]), libraryPath, true);
        var first = Path.Combine(output, "中文 file 'one'.txt");
        var second = Path.Combine(output, "second & two.txt");
        File.WriteAllText(first, "native test one");
        File.WriteAllText(second, "native test two");
        var library = NativeLibrary.Load(libraryPath);
        var factoryObject = IntPtr.Zero;
        var commandObject = IntPtr.Zero;
        var selection = IntPtr.Zero;
        var pidls = new IntPtr[2];
        IClassFactory? factory = null;
        IExplorerCommand? command = null;
        try
        {
            var getFactory = Marshal.GetDelegateForFunctionPointer<GetClassObject>(
                NativeLibrary.GetExport(library, "DllGetClassObject"));
            var classId = new Guid("683ABCCE-019F-436B-A287-CAE2A3BFE665");
            var interfaceId = typeof(IClassFactory).GUID;
            Marshal.ThrowExceptionForHR(getFactory(ref classId, ref interfaceId, out factoryObject));
            factory = (IClassFactory)Marshal.GetObjectForIUnknown(factoryObject);
            interfaceId = typeof(IExplorerCommand).GUID;
            Marshal.ThrowExceptionForHR(factory.CreateInstance(IntPtr.Zero, ref interfaceId, out commandObject));
            command = (IExplorerCommand)Marshal.GetObjectForIUnknown(commandObject);
            Marshal.ThrowExceptionForHR(command.GetTitle(IntPtr.Zero, out var title));
            var label = Marshal.PtrToStringUni(title);
            Marshal.FreeCoTaskMem(title);
            Check(label == "新建收纳卡片并收纳所选文件", "原生 COM 类工厂及中文菜单名称");
            Marshal.ThrowExceptionForHR(command.GetState(IntPtr.Zero, false, out var state));
            Check(state == 2, "没有文件选择时隐藏多选菜单");
            Marshal.ThrowExceptionForHR(SHParseDisplayName(first, IntPtr.Zero, out pidls[0], 0, out _));
            Marshal.ThrowExceptionForHR(SHParseDisplayName(second, IntPtr.Zero, out pidls[1], 0, out _));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists(2, pidls, out selection));
            Marshal.ThrowExceptionForHR(command.GetState(selection, true, out state));
            Check(state == 0, "真实 IShellItemArray 多选启用菜单");
            Marshal.ThrowExceptionForHR(command.Invoke(selection, IntPtr.Zero));
            var requestDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest", "Requests");
            string? receipt = null;
            for (var attempt = 0; attempt < 100 && receipt == null; attempt++)
            {
                foreach (var file in Directory.EnumerateFiles(requestDirectory, "*.received"))
                {
                    using var data = JsonDocument.Parse(File.ReadAllText(file));
                    var paths = data.RootElement.GetProperty("Paths").EnumerateArray()
                        .Select(item => item.GetString()).ToArray();
                    if (paths.SequenceEqual(new[] { first, second })) { receipt = file; break; }
                }
                if (receipt == null) Thread.Sleep(50);
            }
            Check(receipt != null, "一次 Invoke 发送完整多选清单并成功启动进程");
            Check(File.Exists(first) && File.Exists(second), "Shell 扩展本身不移动或删除源文件");
            if (receipt != null)
            {
                File.Delete(receipt[..^".received".Length]);
                File.Delete(receipt);
            }
            File.WriteAllText(Path.Combine(output, "result.txt"), "5 native shell checks passed.");
            return 0;
        }
        finally
        {
            if (selection != IntPtr.Zero) Marshal.Release(selection);
            foreach (var pidl in pidls) if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            if (command != null) Marshal.ReleaseComObject(command);
            if (commandObject != IntPtr.Zero) Marshal.Release(commandObject);
            if (factory != null) Marshal.ReleaseComObject(factory);
            if (factoryObject != IntPtr.Zero) Marshal.Release(factoryObject);
            NativeLibrary.Free(library);
        }
    }

    private static void Check(bool condition, string text)
    {
        if (!condition) throw new Exception("FAIL: " + text);
        Console.WriteLine("PASS: " + text);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetClassObject(ref Guid classId, ref Guid interfaceId, out IntPtr result);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid id, out IntPtr instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    [ComImport, Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IntPtr items, out IntPtr title);
        [PreserveSig] int GetIcon(IntPtr items, out IntPtr icon);
        [PreserveSig] int GetToolTip(IntPtr items, out IntPtr tip);
        [PreserveSig] int GetCanonicalName(out Guid id);
        [PreserveSig] int GetState(IntPtr items, [MarshalAs(UnmanagedType.Bool)] bool slow, out uint state);
        [PreserveSig] int Invoke(IntPtr items, IntPtr context);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IntPtr commands);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string path, IntPtr context, out IntPtr pidl, uint mask, out uint attributes);
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(
        uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls, out IntPtr array);
}
