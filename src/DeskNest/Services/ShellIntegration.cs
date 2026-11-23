using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskNest.Services;

public static class ShellIntegration
{
    public const string ClassId = "{683ABCCE-019F-436B-A287-CAE2A3BFE665}";
    private const string Classes = @"Software\Classes\";
    private const string SelectionKey = @"AllFilesystemObjects\shell\DeskNest.Create";
    private const string BackgroundKey = @"DesktopBackground\Shell\DeskNest.Create";
    public static string RequestsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest", "Requests");

    public static void Register()
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定程序路径。");
        var directory = Path.GetDirectoryName(executable)!;
        var manifest = Path.Combine(directory, "shell-extension.txt");
        var file = File.Exists(manifest) ? File.ReadAllText(manifest).Trim() : "";
        var library = Path.Combine(directory, Path.GetFileName(file));
        if (file.Length == 0 || !File.Exists(library))
            throw new FileNotFoundException("桌面菜单扩展缺失，请使用完整发布目录。");
        using (var server = Registry.CurrentUser.CreateSubKey(Classes + @"CLSID\" + ClassId + @"\InprocServer32"))
        {
            server.SetValue("", library);
            server.SetValue("ThreadingModel", "Apartment");
        }
        using (var selection = Registry.CurrentUser.CreateSubKey(Classes + SelectionKey))
        {
            selection.SetValue("MUIVerb", "新建收纳卡片并收纳所选文件");
            selection.SetValue("Icon", executable);
            selection.SetValue("ExplorerCommandHandler", ClassId);
            selection.SetValue("MultiSelectModel", "Player");
        }
        using (var background = Registry.CurrentUser.CreateSubKey(Classes + BackgroundKey))
        {
            background.SetValue("MUIVerb", "新建 DeskNest 收纳卡片");
            background.SetValue("Icon", executable);
            using var command = background.CreateSubKey("command");
            command.SetValue("", $"\"{executable}\" --new-card");
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Unregister()
    {
        // 仅移除本程序拥有的键，不修改任何已有系统或第三方菜单。
        Registry.CurrentUser.DeleteSubKeyTree(Classes + SelectionKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(Classes + BackgroundKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(Classes + @"CLSID\" + ClassId, false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint change, uint flags, IntPtr first, IntPtr second);
}
