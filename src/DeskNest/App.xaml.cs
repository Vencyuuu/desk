using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DeskNest.Models;
using DeskNest.Services;

namespace DeskNest;

public partial class App : Application
{
    private Mutex? instance;
    private AppController? controller;
    private DesktopCommandChannel? channel;
    private DataDirectoryLease? startupLease;
    public bool StartDesktopOnStartup { get; set; } = true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!StartDesktopOnStartup) return;
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            var index = Array.IndexOf(e.Args, "--data-dir");
            var directory = index >= 0 && e.Args.Length > index + 1 ? e.Args[index + 1] : null;
            // 实例标识固定在入口目录，迁移活动数据后桌面菜单、自启动和 IPC 仍能找到同一实例。
            var location = new DataDirectoryLocation(directory);
            var identity = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(location.ProfileRoot.ToUpperInvariant())))[..16];
            var pipeName = "DeskNest.Desktop." + identity;
            var request = new DesktopRequest { Action = e.Args.Contains("--new-card") ? "new" : "show" };
            if (e.Args.Contains("--quit")) request.Action = "quit";
            if (e.Args.Contains("--install-shell")) request.Action = "shell-enable";
            if (e.Args.Contains("--uninstall-shell")) request.Action = "shell-disable";
            string? manifest = null;
            index = Array.IndexOf(e.Args, "--shell-request");
            if (index >= 0)
            {
                if (e.Args.Length <= index + 1) throw new ArgumentException("缺少桌面选择清单。");
                manifest = Path.GetFullPath(e.Args[index + 1]);
                if (!StorageService.IsInside(manifest, ShellIntegration.RequestsDirectory) ||
                    new FileInfo(manifest).Length > 4_000_000)
                    throw new InvalidDataException("桌面选择清单的位置或大小无效。");
                request = JsonSerializer.Deserialize<DesktopRequest>(File.ReadAllText(manifest))
                    ?? throw new InvalidDataException("桌面选择清单无效。");
                if (request.Paths == null || request.Paths.Length > 10_000 || request.Action != "new")
                    throw new InvalidDataException("桌面选择清单格式不正确。");
            }
            instance = new Mutex(true, @"Local\DeskNest.Desktop." + identity, out var created);
            if (!created)
            {
                // 第二次启动只投递命令，不弹出“已运行”窗口，也不重复创建托盘图标。
                await DesktopCommandChannel.SendAsync(pipeName, request);
                if (manifest != null) File.Delete(manifest);
                Shutdown();
                return;
            }
            if (request.Action == "quit") { Shutdown(); return; }
            string root;
            var recovered = false;
            try { root = location.ResolveRoot(); }
            catch (Exception error) when (error is IOException or JsonException)
            {
                MessageBox.Show(error.Message + "\n请选择移动后的完整数据目录重新关联。",
                    "重新关联数据目录", MessageBoxButton.OK, MessageBoxImage.Information);
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = "重新关联 DeskNest 数据目录" };
                if (picker.ShowDialog() != true) { Shutdown(); return; }
                root = DataPaths.Canonical(picker.FolderName);
                if (!File.Exists(Path.Combine(root, "layout.json")) || !Directory.Exists(Path.Combine(root, "Files")))
                    throw new InvalidDataException("请选择包含 layout.json 和 Files 的完整数据目录。");
                recovered = true;
            }
            startupLease = DataDirectoryLease.Acquire(root);
            var storage = new StorageService(root);
            var loaded = recovered ? DataPaths.ValidateExisting(storage) : storage.Load();
            if (loaded.DataRootHint is { } oldRoot && !DataPaths.Same(oldRoot, root))
            {
                await ShortcutRelinker.RebaseAsync(storage.FilesRoot, oldRoot, root);
                storage.Save(loaded);
            }
            if (recovered) location.SetRoot(root);
            if (request.Action is "shell-enable" or "shell-disable")
            {
                var enabled = request.Action == "shell-enable";
                if (enabled) ShellIntegration.Register(); else ShellIntegration.Unregister();
                var state = storage.Load();
                state.ShellMenuEnabled = enabled;
                storage.Save(state);
                Shutdown();
                return;
            }
            controller = new AppController(storage, location: location, lease: startupLease);
            startupLease = null;
            channel = new DesktopCommandChannel(pipeName,
                command => Dispatcher.BeginInvoke(() => _ = controller.HandleRequestAsync(command)),
                message => Dispatcher.BeginInvoke(() => controller.Notice("桌面命令未完成", message)));
            controller.Start(installShell: directory == null);
            if (request.Action == "new") await controller.HandleRequestAsync(request);
            if (manifest != null) File.Delete(manifest);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "DeskNest 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        channel?.Dispose();
        controller?.Dispose();
        startupLease?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
