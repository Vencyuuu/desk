using System;
using System.IO;
using Microsoft.Win32;

namespace DeskNest.Services;

public interface IStartupService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class StartupService : IStartupService
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string keyPath;
    private readonly string valueName;
    public string Command { get; }

    public StartupService(string executable, string dataRoot, string keyPath = RunKey, string valueName = "DeskNest")
    {
        this.keyPath = keyPath;
        this.valueName = valueName;
        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest");
        Command = QuotePath(Path.GetFullPath(executable));
        if (!string.Equals(Path.GetFullPath(dataRoot), defaultRoot, StringComparison.OrdinalIgnoreCase))
            Command += " --data-dir " + QuotePath(Path.GetFullPath(dataRoot));
    }

    public bool IsEnabled
    {
        get
        {
            // 注册表是唯一事实来源，避免 JSON 勾选状态与 Windows 启动项不一致。
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (Command.Length > 260) throw new IOException("自启动路径过长，请将程序放到较短的目录。");
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(valueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            // 只移除 DeskNest 自己的值，不删除 Run 键，也不触碰其他软件的启动项。
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static string QuotePath(string path)
    {
        if (path.Contains('"')) throw new ArgumentException("路径不能包含引号。");
        // Windows 参数结尾的反斜杠需要加倍，包含空格的目录与磁盘根目录都能正确启动。
        var trailingSlashes = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailingSlashes) + "\"";
    }
}
