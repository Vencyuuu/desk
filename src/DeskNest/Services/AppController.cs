using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DeskNest.Interop;
using DeskNest.Models;
using DeskNest.Views;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeskNest.Services;

public sealed class AppController : IDisposable
{
    private readonly Dictionary<string, FenceWindow> windows = new();
    private readonly Forms.NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly DispatcherTimer repair = new() { Interval = TimeSpan.FromSeconds(2) };
    private DockWindow? dock;
    private bool disposed;
    public StorageService Storage { get; }
    public AppState State { get; }
    public event Action? Changed;
    public int ActiveOperations { get; set; }
    public bool IsQuitting { get; private set; }
    public IReadOnlyList<Window> DesktopWindows => windows.Values
        .Where(card => !card.HasClosed).Cast<Window>()
        .Concat(dock is { HasClosed: false } ? new Window[] { dock } : Array.Empty<Window>()).ToArray();

    public AppController(StorageService storage, bool showTray = true)
    {
        Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        Storage = storage;
        State = storage.Load();
        ThemeService.Apply(State.Theme);
        GlassSurface.RefreshWallpaper();
        trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "DeskNest · 桌面收纳",
            Visible = showTray,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Opening += (_, _) => BuildTrayMenu();
        BuildTrayMenu();
        tray.DoubleClick += (_, _) => ShowCards();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        repair.Tick += (_, _) => RepairDesktop();
    }

    public void Start(bool installShell = true)
    {
        // 常驻程序没有管理窗口；首次启动只显示桌面 Dock，卡片由桌面菜单或托盘创建。
        foreach (var partition in State.Partitions.ToArray()) CreateCard(partition);
        if (!State.DockInitialized)
        {
            // 只在首次初始化放入资源管理器；用户清空 Dock 后，重启不擅自补回。
            if (State.DockItems.Count == 0)
            {
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                State.DockItems.Add(new DockItem { Name = "文件资源管理器", Path = explorer });
            }
            State.DockInitialized = true;
            Persist();
        }
        CreateDock();
        if (installShell && State.ShellMenuEnabled)
        {
            try { ShellIntegration.Register(); }
            catch (Exception error) { Notice("桌面菜单未安装", error.Message); }
        }
        repair.Start();
        if (Storage.RecoveryMessage != null) Notice("已恢复布局", Storage.RecoveryMessage);
    }

    public Partition? AddPartition(Window? owner = null, bool prompt = true)
    {
        var name = "新收纳卡片";
        var accent = "#007AFF";
        if (prompt)
        {
            var dialog = new PartitionDialog { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (dialog.ShowDialog() != true) return null;
            name = dialog.PartitionName;
            accent = dialog.Accent;
        }
        var number = 2;
        var baseName = name;
        while (State.Partitions.Any(item => item.Name == name)) name = baseName + " " + number++;
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        var partition = new Partition
        {
            Name = name,
            Accent = accent,
            PhysicalCoordinates = true,
            Left = area.Left + 36 + State.Partitions.Count % 6 * 32,
            Top = area.Top + 48 + State.Partitions.Count % 6 * 32,
        };
        Storage.GetFolder(partition);
        State.Partitions.Add(partition);
        try { Persist(); }
        catch { State.Partitions.Remove(partition); throw; }
        CreateCard(partition);
        return partition;
    }

    public async Task HandleRequestAsync(DesktopRequest request)
    {
        try
        {
            if (request.Action == "quit") { Quit(); return; }
            if (request.Action is "shell-enable" or "shell-disable")
            {
                var enabled = request.Action == "shell-enable";
                if (enabled) ShellIntegration.Register(); else ShellIntegration.Unregister();
                State.ShellMenuEnabled = enabled;
                Persist();
                return;
            }
            if (request.Action != "new") { ShowCards(); return; }
            var partition = AddPartition(prompt: false)!;
            if (request.Paths.Length == 0) return;
            ActiveOperations++;
            try
            {
                var result = await Task.Run(() => Storage.Import(partition, request.Paths));
                windows[partition.Id].RefreshFiles();
                Notice("收纳完成", $"已将 {result.Succeeded} 个项目放入“{partition.Name}”。");
                if (result.Errors.Count > 0)
                    MessageBox.Show(string.Join("\n", result.Errors), "部分所选文件未收纳");
            }
            finally { ActiveOperations--; }
        }
        catch (Exception error) { Notice("操作未完成", error.Message); }
    }

    public void EditPartition(Partition partition, Window? owner = null)
    {
        var dialog = new PartitionDialog(partition.Name, partition.Accent)
        { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (dialog.ShowDialog() != true) return;
        partition.Name = dialog.PartitionName;
        partition.Accent = dialog.Accent;
        windows.GetValueOrDefault(partition.Id)?.UpdateHeader();
        Persist();
    }

    public bool RenamePartition(Partition partition, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40) return false;
        if (trimmed == partition.Name) return true;
        // 只修改显示名称；保存失败时恢复内存值，托管目录 ID 和文件路径始终不变。
        var previous = partition.Name;
        partition.Name = trimmed;
        try { Storage.Save(State); }
        catch { partition.Name = previous; throw; }
        windows.GetValueOrDefault(partition.Id)?.UpdateHeader();
        Changed?.Invoke();
        return true;
    }

    public void RemovePartition(Partition partition, Window? owner = null)
    {
        if (ActiveOperations > 0) { Notice("正在收纳", "请等待文件移动完成。 "); return; }
        if (MessageBox.Show($"移除“{partition.Name}”卡片？\n\n文件会保留，可从托盘的“恢复卡片”取回。",
            "移除卡片", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        State.Partitions.Remove(partition);
        try { Persist(); }
        catch { State.Partitions.Add(partition); throw; }
        if (windows.Remove(partition.Id, out var card)) card.Close();
    }

    public void RestorePartition(string directory)
    {
        var id = Path.GetFileName(directory);
        if (!Guid.TryParseExact(id, "N", out _) || State.Partitions.Any(item => item.Id == id)) return;
        var partition = new Partition { Id = id, Name = "恢复的卡片" };
        State.Partitions.Add(partition);
        Persist();
        CreateCard(partition);
    }

    public IEnumerable<string> RemovedFolders() => Directory.EnumerateDirectories(Storage.FilesRoot)
        .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
            State.Partitions.All(item => item.Id != Path.GetFileName(path)));

    private void CreateCard(Partition partition)
    {
        var card = new FenceWindow(this, partition);
        windows[partition.Id] = card;
        card.Show();
    }

    private void CreateDock()
    {
        dock = new DockWindow(this);
        if (State.DockEnabled) dock.Show();
    }

    private void RepairDesktop()
    {
        if (disposed || IsQuitting || DesktopLayer.FindDesktopView() == IntPtr.Zero) return;
        // Explorer 重启会销毁其子窗口，下一轮重新创建；未找到桌面时不回退成普通窗口。
        foreach (var partition in State.Partitions.ToArray())
        {
            if (!windows.TryGetValue(partition.Id, out var card) || card.HasClosed) CreateCard(partition);
            else card.EnsureAttached();
        }
        if (dock == null || dock.HasClosed) CreateDock();
        else dock.EnsureAttached();
    }

    public void FocusCard(Partition partition)
    {
        if (!windows.TryGetValue(partition.Id, out var card)) return;
        card.Reveal();
    }

    public void ShowCards()
    {
        foreach (var card in windows.Values) if (!card.HasClosed) card.Reveal();
        if (State.DockEnabled && dock is { HasClosed: false }) dock.Show();
    }

    public void HideCards()
    {
        foreach (var card in windows.Values) if (!card.HasClosed) card.HideOnDesktop();
        if (dock is { HasClosed: false }) dock.Hide();
    }

    public void AddDockItems(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!File.Exists(path) || extension is not (".exe" or ".lnk" or ".url" or ".appref-ms")) continue;
            if (State.DockItems.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            State.DockItems.Add(new DockItem { Name = Path.GetFileNameWithoutExtension(path), Path = path });
        }
        Persist();
        dock?.RefreshItems();
    }

    public void PickDockItems()
    {
        var picker = new OpenFileDialog
        {
            Title = "添加快捷应用（只建立引用，不移动源文件）",
            Multiselect = true,
            Filter = "应用与快捷方式|*.exe;*.lnk;*.url;*.appref-ms",
        };
        if (picker.ShowDialog() == true) AddDockItems(picker.FileNames);
    }

    public void Persist() { Storage.Save(State); Changed?.Invoke(); }
    public void NotifyFilesChanged() => Changed?.Invoke();
    public void SetTheme(string theme)
    {
        State.Theme = theme;
        ThemeService.Apply(theme);
        Persist();
    }

    public void Notice(string title, string message)
    {
        if (disposed) return;
        tray.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Info);
    }

    private void BuildTrayMenu()
    {
        var menu = tray.ContextMenuStrip!;
        var previous = menu.Items.Cast<Forms.ToolStripItem>().ToArray();
        menu.Items.Clear();
        foreach (var item in previous) item.Dispose();
        menu.Items.Add("新建收纳卡片…", null, (_, _) => AddPartition());
        var cards = new Forms.ToolStripMenuItem("收纳卡片");
        foreach (var partition in State.Partitions)
        {
            var submenu = new Forms.ToolStripMenuItem(partition.Name);
            submenu.DropDownItems.Add("定位卡片", null, (_, _) => FocusCard(partition));
            submenu.DropDownItems.Add("名称与颜色…", null, (_, _) => EditPartition(partition));
            submenu.DropDownItems.Add(partition.Collapsed ? "展开" : "折叠", null,
                (_, _) => windows.GetValueOrDefault(partition.Id)?.ToggleCollapsed());
            submenu.DropDownItems.Add(partition.Locked ? "解锁布局" : "锁定布局", null, (_, _) =>
            {
                partition.Locked = !partition.Locked;
                windows.GetValueOrDefault(partition.Id)?.UpdateHeader();
                Persist();
            });
            submenu.DropDownItems.Add("打开真实目录", null, (_, _) => ShellService.Open(Storage.GetFolder(partition)));
            submenu.DropDownItems.Add("所选文件移回桌面", null,
                (_, _) => windows.GetValueOrDefault(partition.Id)?.RestoreSelected());
            submenu.DropDownItems.Add("移除卡片…", null, (_, _) => RemovePartition(partition));
            cards.DropDownItems.Add(submenu);
        }
        cards.Enabled = cards.DropDownItems.Count > 0;
        menu.Items.Add(cards);
        var restore = new Forms.ToolStripMenuItem("恢复已移除卡片");
        foreach (var folder in RemovedFolders())
            restore.DropDownItems.Add("恢复 " + Path.GetFileName(folder)[..8], null, (_, _) => RestorePartition(folder));
        restore.Enabled = restore.DropDownItems.Count > 0;
        menu.Items.Add(restore);
        menu.Items.Add("显示全部", null, (_, _) => ShowCards());
        menu.Items.Add("隐藏全部", null, (_, _) => HideCards());
        menu.Items.Add(new Forms.ToolStripSeparator());
        BuildDockMenu(menu);
        var appearance = new Forms.ToolStripMenuItem("外观");
        foreach (var pair in new[] { ("跟随系统", "system"), ("浅色", "light"), ("深色", "dark") })
            appearance.DropDownItems.Add(new Forms.ToolStripMenuItem(pair.Item1, null, (_, _) => SetTheme(pair.Item2))
            { Checked = State.Theme == pair.Item2 });
        appearance.DropDownItems.Add(new Forms.ToolStripSeparator());
        appearance.DropDownItems.Add(new Forms.ToolStripMenuItem("液态玻璃近似效果", null, (_, _) =>
        { State.GlassEnabled = !State.GlassEnabled; Persist(); })
        { Checked = State.GlassEnabled });
        menu.Items.Add(appearance);
        var integration = new Forms.ToolStripMenuItem("桌面右键菜单");
        integration.DropDownItems.Add("安装 / 修复", null, (_, _) =>
        { ShellIntegration.Register(); State.ShellMenuEnabled = true; Persist(); });
        integration.DropDownItems.Add("移除菜单集成", null, (_, _) =>
        { ShellIntegration.Unregister(); State.ShellMenuEnabled = false; Persist(); });
        menu.Items.Add(integration);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开收纳目录", null, (_, _) => ShellService.Open(Storage.FilesRoot));
        menu.Items.Add("使用说明", null, (_, _) => ShowHelp());
        menu.Items.Add("退出 DeskNest", null, (_, _) => Quit());
    }

    private void BuildDockMenu(Forms.ContextMenuStrip menu)
    {
        var section = new Forms.ToolStripMenuItem("底部快捷应用栏");
        section.DropDownItems.Add(new Forms.ToolStripMenuItem("显示应用栏", null, (_, _) =>
        {
            State.DockEnabled = !State.DockEnabled;
            if (State.DockEnabled) dock?.Show(); else dock?.Hide();
            Persist();
        })
        { Checked = State.DockEnabled });
        section.DropDownItems.Add("添加应用…", null, (_, _) => PickDockItems());
        foreach (var item in State.DockItems)
        {
            var entry = new Forms.ToolStripMenuItem(item.Name);
            entry.DropDownItems.Add("向左移动", null, (_, _) => MoveDockItem(item, -1));
            entry.DropDownItems.Add("向右移动", null, (_, _) => MoveDockItem(item, 1));
            entry.DropDownItems.Add("从应用栏移除", null, (_, _) =>
            { State.DockItems.Remove(item); Persist(); dock?.RefreshItems(); });
            section.DropDownItems.Add(entry);
        }
        menu.Items.Add(section);
    }

    private void MoveDockItem(DockItem item, int direction)
    {
        var from = State.DockItems.IndexOf(item);
        var to = Math.Clamp(from + direction, 0, State.DockItems.Count - 1);
        if (from == to) return;
        State.DockItems.RemoveAt(from);
        State.DockItems.Insert(to, item);
        Persist();
        dock?.RefreshItems();
    }

    private void ShowHelp() => MessageBox.Show(
        "右键桌面空白处 → 新建 DeskNest 收纳卡片。\n" +
        "选中多个文件后右键 → 新建收纳卡片并收纳所选文件。\n" +
        "Windows 11 上这些菜单可能位于“显示更多选项”。\n\n" +
        "点击卡片名称直接编辑，Enter 保存、Esc 取消；拖动标题移动。\n" +
        "右下角调整大小，双击标题空白处或点击箭头折叠。\n" +
        "搜索框可模糊搜索文件名；Ctrl+F 定位搜索，Esc 清空。\n" +
        "文件双击打开，右键保留系统菜单；配置集中在托盘右键。\n" +
        "将应用或快捷方式拖到 Dock 可固定，源文件不移动。\n\n" +
        "玻璃效果采用本地壁纸模糊采样与高光近似，不是苹果原生材质。\n" +
        "文件夹跨磁盘移动暂不支持。\n\n数据目录：" + Storage.Root, "DeskNest 使用说明");

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            GlassSurface.RefreshWallpaper();
            ThemeService.Apply(State.Theme);
            Changed?.Invoke();
        });

    private void OnDisplayChanged(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.BeginInvoke(() => dock?.Reposition());

    public void Quit()
    {
        if (ActiveOperations > 0) { Notice("正在移动文件", "请等待操作完成后退出。"); return; }
        foreach (var card in windows.Values) if (!card.HasClosed) card.SaveGeometry();
        Storage.Save(State);
        IsQuitting = true;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        IsQuitting = true;
        repair.Stop();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        foreach (var card in windows.Values.ToArray()) if (!card.HasClosed) card.Close();
        if (dock is { HasClosed: false }) dock.Close();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        trayIcon.Dispose();
    }
}
