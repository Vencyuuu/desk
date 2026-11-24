using DeskNest.Models;
using DeskNest.Services;
using System.Text.Json;
using System.IO;

// 无第三方测试依赖；所有文件都放在显式指定的项目测试目录中。
var root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/storage-tests");
var run = Path.Combine(root, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(run);
var storage = new StorageService(Path.Combine(run, "data"), Path.Combine(run, "desktop"));
var first = new Partition { Name = "工作文档" };
var second = new Partition { Name = "资料归档" };
var state = new AppState { Partitions = { first, second } };
var passed = 0;

void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
    passed++;
}

Check(storage.Load().Partitions.Count == 0, "首次启动返回空布局");
storage.Save(state);
Check(storage.Load().Partitions[0].Name == "工作文档", "中文布局保存和重载");
state.Partitions[0].Name = "项目文档";
storage.Save(state);
Check(File.Exists(storage.StatePath + ".bak"), "保存时保留上一版备份");

var source = Path.Combine(run, "source");
Directory.CreateDirectory(source);
var document = Path.Combine(source, "报告.txt");
File.WriteAllText(document, "中文文件内容\n不应被覆盖");
var imported = storage.Import(first, new[] { document });
var managed = Path.Combine(storage.GetFolder(first), "报告.txt");
Check(imported.Succeeded == 1 && !File.Exists(document) &&
    File.ReadAllText(managed).Contains("不应被覆盖"), "导入移动源文件并保持内容");
File.WriteAllText(document, "第二份");
storage.Import(first, new[] { document });
Check(File.ReadAllText(managed).Contains("不应被覆盖") &&
    File.ReadAllText(Path.Combine(storage.GetFolder(first), "报告 (2).txt")) == "第二份", "重名自动编号且不覆盖原文件");
Check(storage.Import(first, new[] { managed }).Succeeded == 0, "同分区重复拖入不移动文件");
Check(storage.Import(second, new[] { managed }).Succeeded == 1 &&
    !File.Exists(managed), "跨分区拖动使用真实移动");

var folder = Path.Combine(source, "文件夹");
Directory.CreateDirectory(Path.Combine(folder, "子目录"));
File.WriteAllText(Path.Combine(folder, "子目录", "hello.txt"), "nested");
Check(storage.Import(first, new[] { folder }).Succeeded == 1 &&
    File.Exists(Path.Combine(storage.GetFolder(first), "文件夹", "子目录", "hello.txt")), "同卷文件夹完整移动");
Check(storage.Import(first, new[] { storage.Root }).Errors.Count == 1, "拒绝把数据目录搬进自身");
Check(storage.Import(first, new[] { run }).Errors.Count == 1, "拒绝搬入数据目录祖先");
var partial = Path.Combine(source, "正常.txt");
File.WriteAllText(partial, "ok");
var mixed = storage.Import(first, new[] { Path.Combine(source, "missing.txt"), partial });
Check(mixed.Succeeded == 1 && mixed.Errors.Count == 1, "单项失败不阻断其他文件并报告错误");

state.Partitions.Remove(first);
storage.Save(state);
Check(Directory.GetFileSystemEntries(storage.GetFolder(first)).Length > 0, "移除分区布局后托管文件完整保留");
File.WriteAllText(storage.StatePath, "{ damaged json");
Check(storage.Load().Partitions.Count == 2 && storage.RecoveryMessage != null, "主布局损坏后从上一版备份恢复");
Check(Directory.GetFiles(storage.Root, "layout.json.damaged-*").Length == 1, "恢复时保留损坏原配置");

var invalid = new AppState { Partitions = { new Partition { Id = "../escape" } } };
File.WriteAllText(storage.StatePath, JsonSerializer.Serialize(invalid));
File.Move(storage.StatePath + ".bak", storage.StatePath + ".saved-backup");
var rejected = false;
try { storage.Load(); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "拒绝恶意分区路径，避免目录穿越");

Exception? shellError = null;
var shellThread = new Thread(() =>
{
    try
    {
        var path = Path.Combine(storage.GetFolder(second), "报告.txt");
        var menu = DeskNest.Interop.ShellService.InspectContextMenu(path);
        Check(menu.Count > 3 && menu.Any(item => item.Contains("属性") || item.Contains("Properties")),
            "真实文件的 Windows Shell 菜单可查询且包含系统属性命令");
        if (args.Contains("--shell-installed"))
        {
            Check(menu.Any(item => item.Contains("新建收纳卡片")),
                "注册后真实文件系统菜单包含收纳入口");
        }
        Check(DeskNest.Interop.ShellService.GetIcon(path) != null, "可提取真实关联文件图标");
        Console.WriteLine("Shell menu: " + string.Join(" / ", menu));
    }
    catch (Exception error) { shellError = error; }
});
shellThread.SetApartmentState(ApartmentState.STA);
shellThread.Start();
shellThread.Join();
if (shellError != null) throw shellError;

var restoreSource = Path.Combine(storage.GetFolder(second), "报告.txt");
var restored = storage.RestoreToDesktop(restoreSource);
Check(File.Exists(restored) && !File.Exists(restoreSource), "文件移回隔离桌面且托管目录不保留副本");
var another = Path.Combine(storage.GetFolder(second), "报告.txt");
File.WriteAllText(another, "再次移回桌面");
var restoredAgain = storage.RestoreToDesktop(another);
Check(Path.GetFileName(restoredAgain) == "报告 (2).txt" &&
    File.ReadAllText(restored).Contains("不应被覆盖"), "移回桌面遇到重名时不覆盖已有文件");
var outsideRejected = false;
try { storage.RestoreToDesktop(restored); } catch (IOException) { outsideRejected = true; }
Check(outsideRejected, "移回桌面不能移动托管目录外的文件");

var delivered = new TaskCompletionSource<DesktopRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
var pipeName = "DeskNest.Test." + Guid.NewGuid().ToString("N");
using (var pipe = new DesktopCommandChannel(pipeName,
    request => delivered.TrySetResult(request), message => delivered.TrySetException(new Exception(message))))
{
    var request = new DesktopRequest { Action = "new", Paths = new[] { restored, restoredAgain } };
    DesktopCommandChannel.SendAsync(pipeName, request).GetAwaiter().GetResult();
    var received = delivered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    Check(received.Action == "new" && received.Paths.SequenceEqual(request.Paths),
        "同用户进程间通信完整传递一次多选文件请求");
}

state.DockItems.Add(new DockItem { Name = "资源管理器", Path = @"C:\Windows\explorer.exe" });
state.GlassEnabled = false;
state.DockEnabled = false;
storage.Save(state);
var restoredState = storage.Load();
Check(!restoredState.GlassEnabled && !restoredState.DockEnabled && restoredState.DockItems.Count == 1,
    "Dock 固定项、玻璃和显示设置可持久化");
Check(FileNameSearch.Matches("工作报告.PDF", "工报"), "中文文件名支持间隔字符模糊匹配");
Check(FileNameSearch.Matches("ProjectReport.PDF", "pjt pdf"), "多关键词及大小写不敏感匹配");
Check(FileNameSearch.Matches("报告２０２６.pdf", "2026"), "全角与半角文件名统一匹配");
Check(FileNameSearch.Matches("工作报告.pdf", "   "), "空白搜索恢复全部文件");
Check(!FileNameSearch.Matches("工作报告.pdf", "报工"), "不匹配顺序错误的字符");
state.DockHeight = 128;
state.CardOpacity = 0.2;
storage.Save(state);
Check(storage.Load().DockHeight == 128 && storage.Load().CardOpacity == 0.2, "外观配置边界值持久化");
var oldConfig = Path.Combine(run, "old-config");
Directory.CreateDirectory(oldConfig);
File.WriteAllText(Path.Combine(oldConfig, "layout.json"), "{\"Version\":1,\"Partitions\":[]}");
var migrated = new StorageService(oldConfig).Load();
Check(migrated.DockHeight == 88 && migrated.CardOpacity == 0.65, "旧配置缺少新字段时自动使用兼容默认值");
Check(migrated.DockBottomOffset == 12, "旧配置默认保留 Dock 底部间距");
Check(migrated.Typography == new TypographyPreferences(), "旧配置兼容默认字体及标题样式");
Check(!new TypographyPreferences(CardTitleSize: 0).IsValid() &&
    !new TypographyPreferences(CardTitleColor: "invalid").IsValid(), "拒绝无效标题字号及颜色");
Check(!AppearancePreferences.IsValid("light", 88, 0.65, -1) &&
    !AppearancePreferences.IsValid("light", 88, 0.65, 301) &&
    !AppearancePreferences.IsValid("light", 88, 0.65, double.NaN), "拒绝无效 Dock 底部间距");
var testKey = @"Software\DeskNest\Tests\" + Guid.NewGuid().ToString("N");
try
{
    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey)) key.SetValue("OtherApp", "keep");
    var startup = new StartupService(@"D:\Program Files\DeskNest\DeskNest.exe", @"D:\Desk Nest\Data", testKey);
    Check(!startup.IsEnabled, "自启动默认不创建启动项");
    startup.SetEnabled(true);
    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
        Check(startup.IsEnabled && (string?)key?.GetValue("DeskNest") ==
            "\"D:\\Program Files\\DeskNest\\DeskNest.exe\" --data-dir \"D:\\Desk Nest\\Data\"",
            "自启动注册正确引用含空格的程序及数据目录");
    startup.SetEnabled(false);
    startup.SetEnabled(false);
    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testKey))
        Check(!startup.IsEnabled && (string?)key?.GetValue("OtherApp") == "keep", "关闭自启动仅移除自己的值且可重复执行");
}
finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }
// 使用独立夹具验证迁移；失败和取消都不能更改源文件或定位入口。
var moveSource = new StorageService(Path.Combine(run, "move-source"));
var moveCard = new Partition { Name = "迁移验证" };
var moveState = new AppState { Partitions = { moveCard } };
var moveFile = Path.Combine(moveSource.GetFolder(moveCard), "中文文件.txt");
File.WriteAllText(moveFile, "迁移前后内容一致");
Directory.CreateDirectory(Path.Combine(moveSource.GetFolder(moveCard), "空目录"));
moveState.DockItems.Add(new DockItem { Name = "内部文件", Path = moveFile });
moveSource.Save(moveState);
var moveTarget = Path.Combine(run, "move-target");
var locator = new DataDirectoryLocation(moveSource.Root);
using (var sourceLease = DataDirectoryLease.Acquire(moveSource.Root))
{
    var duplicateRejected = false;
    try { using var duplicate = DataDirectoryLease.Acquire(moveSource.Root); }
    catch (IOException) { duplicateRejected = true; }
    Check(duplicateRejected, "同一数据目录拒绝第二个写入实例");
    using var prepared = await DataMigrationService.CopyAsync(moveSource, moveTarget);
    var copied = DataPaths.ValidateExisting(prepared.Store);
    Check(File.ReadAllText(Path.Combine(prepared.Store.GetFolder(moveCard), "中文文件.txt")) ==
        File.ReadAllText(moveFile) && Directory.Exists(Path.Combine(prepared.Store.GetFolder(moveCard), "空目录")),
        "迁移保留中文文件内容及空子目录");
    Check(copied.DockItems[0].Path == Path.Combine(prepared.Store.GetFolder(moveCard), "中文文件.txt"),
        "加载迁移数据自动修复内部 Dock 引用");
    Check(File.Exists(moveFile) && !File.Exists(Path.Combine(moveTarget, "location.json")),
        "迁移保留源目录且不复制旧入口定位信息");
    locator.SetRoot(moveTarget);
    Check(new DataDirectoryLocation(moveSource.Root).ResolveRoot() == moveTarget,
        "稳定入口重启后定位新数据目录");
}
foreach (var destination in new[] { moveSource.Root, Path.Combine(moveSource.Root, "nested"), moveTarget })
{
    var moveRejected = false;
    try { using var invalidMove = await DataMigrationService.CopyAsync(moveSource, destination); }
    catch (IOException) { moveRejected = true; }
    Check(moveRejected && File.Exists(moveFile), "拒绝相同、嵌套或非空目标且保留源文件");
}
var lockedTarget = Path.Combine(run, "locked-target");
using (var writer = new FileStream(moveFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
{
    var moveRejected = false;
    try { using var invalidMove = await DataMigrationService.CopyAsync(moveSource, lockedTarget); }
    catch (IOException) { moveRejected = true; }
    Check(moveRejected && !Directory.Exists(lockedTarget), "正在写入的托管文件阻止迁移且不发布目标目录");
}
var canceledTarget = Path.Combine(run, "canceled-target");
using (var cancellation = new CancellationTokenSource())
{
    var canceled = false;
    var progress = new ImmediateProgress(value =>
    {
        if (value.Stage == "复制并校验文件") cancellation.Cancel();
    });
    try { using var invalidMove = await DataMigrationService.CopyAsync(moveSource, canceledTarget, progress, cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check(canceled && !Directory.Exists(canceledTarget) && File.Exists(moveFile),
        "复制中取消清理暂存文件并继续保留源数据");
}
var missingLocation = new DataDirectoryLocation(Path.Combine(run, "missing-profile"));
missingLocation.SetRoot(Path.Combine(run, "not-present"));
var missingRejected = false;
try { missingLocation.ResolveRoot(); } catch (DirectoryNotFoundException) { missingRejected = true; }
Check(missingRejected, "已配置目录丢失时拒绝静默创建空数据");
Console.WriteLine($"\n{passed} checks passed. Test data: {run}");

sealed class ImmediateProgress(Action<DataMoveProgress> report) : IProgress<DataMoveProgress>
{
    public void Report(DataMoveProgress value) => report(value);
}
