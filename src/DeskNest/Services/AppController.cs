using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
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
    private SettingsWindow? settings;
    private bool disposed;
    private readonly DataDirectoryLocation location;
    private DataDirectoryLease? dataLease;
    private readonly Queue<DesktopRequest> pendingRequests = new();
    public StorageService Storage { get; private set; }
    public AppState State { get; private set; }
    public bool IsDataChanging { get; private set; }
    public event Action? Changed;
    public int ActiveOperations { get; set; }
    public bool IsQuitting { get; private set; }
    public IStartupService Startup { get; }
    public IReadOnlyList<Window> DesktopWindows => windows.Values
        .Where(card => !card.HasClosed).Cast<Window>()
        .Concat(dock is { HasClosed: false } ? new Window[] { dock } : Array.Empty<Window>()).ToArray();

    public AppController(StorageService storage, bool showTray = true, IStartupService? startup = null,
        DataDirectoryLocation? location = null, DataDirectoryLease? lease = null)
    {
        Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        Storage = storage;
        this.location = location ?? new DataDirectoryLocation(storage.Root);
        dataLease = lease;
        Startup = startup ?? new StartupService(Environment.ProcessPath!, this.location.ProfileRoot);
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
        dataLease ??= DataDirectoryLease.Acquire(Storage.Root);
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
            var dialog = new PartitionDialog
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                FontFamily = FontCatalog.Resolve(State.Typography.SettingsFont),
            };
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
        if (IsDataChanging) { pendingRequests.Enqueue(request); return; }
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
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = FontCatalog.Resolve(State.Typography.SettingsFont),
        };
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

    public void SetPartitionColors(Partition partition, string? background, string? title)
    {
        if (IsDataChanging || !State.Partitions.Contains(partition))
            throw new InvalidOperationException("当前卡片不可修改。");
        if (!TypographyPreferences.ValidColor(background) || !TypographyPreferences.ValidColor(title))
            throw new ArgumentException("颜色必须为 #RRGGBB。");
        var previousBackground = partition.BackgroundColor;
        var previousTitle = partition.TitleColor;
        partition.BackgroundColor = background;
        partition.TitleColor = title;
        try { Storage.Save(State); }
        catch { partition.BackgroundColor = previousBackground; partition.TitleColor = previousTitle; throw; }
        Changed?.Invoke();
    }

    private void PickPartitionColor(Partition partition, bool background)
    {
        using var picker = new Forms.ColorDialog { FullOpen = true };
        var current = background ? partition.BackgroundColor : partition.TitleColor ?? State.Typography.CardTitleColor;
        if (current != null) picker.Color = ColorTranslator.FromHtml(current);
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        var color = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        SetPartitionColors(partition, background ? color : partition.BackgroundColor,
            background ? partition.TitleColor : color);
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
        if (disposed || IsQuitting || IsDataChanging || DesktopLayer.FindDesktopView() == IntPtr.Zero) return;
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
            var folder = Directory.Exists(path);
            if (!folder && (!File.Exists(path) || extension is not (".exe" or ".lnk" or ".url" or ".appref-ms"))) continue;
            if (State.DockItems.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            var name = folder
                ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))
                : Path.GetFileNameWithoutExtension(path);
            State.DockItems.Add(new DockItem { Name = string.IsNullOrEmpty(name) ? path : name, Path = path });
        }
        Persist();
        dock?.RefreshItems();
    }

    public bool RemoveDockItem(DockItem item)
    {
        var index = State.DockItems.IndexOf(item);
        if (index < 0) return false;
        // Dock 只管理引用。拖出和托盘移除都不调用文件移动或删除接口。
        State.DockItems.RemoveAt(index);
        try { Persist(); }
        catch { State.DockItems.Insert(index, item); throw; }
        dock?.RefreshItems();
        return true;
    }

    public void ReorderDockItem(DockItem item, int target)
    {
        var from = State.DockItems.IndexOf(item);
        if (from < 0) return;
        target = Math.Clamp(target, 0, State.DockItems.Count - 1);
        if (from == target) return;
        State.DockItems.RemoveAt(from);
        State.DockItems.Insert(target, item);
        try { Persist(); }
        catch { State.DockItems.Remove(item); State.DockItems.Insert(from, item); throw; }
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

    public void Persist(bool notify = true)
    {
        if (IsDataChanging) return;
        Storage.Save(State);
        if (notify) Changed?.Invoke();
    }
    public void NotifyFilesChanged() => Changed?.Invoke();
    public AppearancePreferences Preferences => new(
        State.Theme, State.DockHeight, State.CardOpacity, State.GlassEnabled, State.DockEnabled,
        State.DockBottomOffset, State.Typography);

    public void SetPreferences(AppearancePreferences preferences)
    {
        if (!AppearancePreferences.IsValid(preferences.Theme, preferences.DockHeight,
            preferences.CardOpacity, preferences.DockBottomOffset) ||
            (preferences.Typography != null && !preferences.Typography.IsValid()))
            throw new ArgumentOutOfRangeException(nameof(preferences), "外观设置超出允许范围。");
        var previous = Preferences;
        AssignPreferences(preferences);
        try { Storage.Save(State); }
        catch { AssignPreferences(previous); throw; }
        ThemeService.Apply(State.Theme);
        Changed?.Invoke();
        if (dock is { HasClosed: false })
        {
            if (State.DockEnabled) dock.Show(); else dock.Hide();
        }
    }

    private void AssignPreferences(AppearancePreferences preferences)
    {
        State.Theme = preferences.Theme;
        State.DockHeight = preferences.DockHeight;
        State.DockBottomOffset = preferences.DockBottomOffset;
        State.Typography = preferences.Typography ?? new TypographyPreferences();
        State.CardOpacity = preferences.CardOpacity;
        State.GlassEnabled = preferences.GlassEnabled;
        State.DockEnabled = preferences.DockEnabled;
    }

    public void SetTheme(string theme) => SetPreferences(Preferences with { Theme = theme });

    public void SetStartupEnabled(bool enabled)
    {
        Startup.SetEnabled(enabled);
        Changed?.Invoke();
    }

    public void ShowSettings()
    {
        if (settings == null)
        {
            settings = new SettingsWindow(this);
            settings.Closed += (_, _) => settings = null;
        }
        settings.Show();
        settings.WindowState = WindowState.Normal;
        settings.Activate();
    }

    public async Task ChangeDataDirectoryAsync(string directory, bool associate,
        IProgress<DataMoveProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (IsDataChanging || ActiveOperations > 0)
            throw new InvalidOperationException("请等待当前文件操作完成后再更改数据目录。");
        if (settings != null && !settings.FlushChanges())
            throw new IOException("请先处理尚未保存的设置。");
        var target = DataPaths.Canonical(directory);
        if (DataPaths.Same(target, DataPaths.Canonical(Storage.Root)))
            throw new IOException("所选目录已经是当前数据目录。");
        if (!associate)
        {
            foreach (var card in windows.Values) if (!card.HasClosed) card.SaveGeometry();
            Storage.Save(State);
        }
        var previousStorage = Storage;
        var previousState = State;
        var previousLease = dataLease;
        var switched = false;
        IsDataChanging = true;
        ActiveOperations++;
        repair.Stop();
        foreach (var window in DesktopWindows) window.IsEnabled = false;
        Changed?.Invoke();
        try
        {
            using var prepared = associate
                ? PrepareExisting(target)
                : await DataMigrationService.CopyAsync(Storage, target, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new DataMoveProgress("准备切换目录", 1, 1));
            var nextState = DataPaths.ValidateExisting(prepared.Store);
            // 老版本没有目录提示；仍可用切换前的根目录修复内部 Dock 引用。
            DataPaths.RebaseDock(nextState, previousStorage.Root, target);
            await ShortcutRelinker.RebaseAsync(prepared.Store.FilesRoot,
                nextState.DataRootHint ?? previousStorage.Root, target);
            prepared.Store.Save(nextState);
            if (Startup.IsEnabled) Startup.SetEnabled(true);

            // 先完成新视图的创建，最后原子写入定位文件。失败时旧入口与旧数据均可继续使用。
            CloseDesktopViews();
            try
            {
                Storage = prepared.Store;
                State = nextState;
                RecreateDesktopViews();
                prepared.VerifySource();
                location.SetRoot(target);
            }
            catch
            {
                CloseDesktopViews();
                Storage = previousStorage;
                State = previousState;
                RecreateDesktopViews();
                throw;
            }
            dataLease = prepared.TakeLease();
            previousLease?.Dispose();
            switched = true;
        }
        finally
        {
            IsDataChanging = false;
            ActiveOperations--;
            foreach (var window in DesktopWindows) window.IsEnabled = true;
            repair.Start();
            Changed?.Invoke();
            while (pendingRequests.TryDequeue(out var request))
            {
                if (switched && !associate)
                    request.Paths = request.Paths.Select(path =>
                        DataPaths.Rebase(path, previousStorage.Root, Storage.Root)).ToArray();
                await HandleRequestAsync(request);
            }
        }
    }

    private PreparedDataDirectory PrepareExisting(string root)
    {
        if (!File.Exists(Path.Combine(root, "layout.json")) || !Directory.Exists(Path.Combine(root, "Files")))
            throw new IOException("所选目录必须包含 layout.json 和 Files，请选择完整的数据目录。");
        var lease = DataDirectoryLease.Acquire(root);
        try
        {
            var store = new StorageService(root, Storage.DesktopDirectory);
            DataPaths.ValidateExisting(store);
            return new PreparedDataDirectory(store, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    private void CloseDesktopViews()
    {
        foreach (var card in windows.Values.ToArray()) if (!card.HasClosed) card.Close();
        windows.Clear();
        if (dock is { HasClosed: false }) dock.Close();
        dock = null;
    }

    private void RecreateDesktopViews()
    {
        ThemeService.Apply(State.Theme);
        foreach (var partition in State.Partitions) CreateCard(partition);
        CreateDock();
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
        if (IsDataChanging)
        {
            menu.Items.Add("正在切换数据目录…").Enabled = false;
            menu.Items.Add("查看设置", null, (_, _) => ShowSettings());
            return;
        }
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        var startup = new Forms.ToolStripMenuItem("开机自启动");
        try { startup.Checked = Startup.IsEnabled; }
        catch (Exception error) { startup.Enabled = false; startup.ToolTipText = error.Message; }
        startup.Click += (_, _) =>
        {
            try { SetStartupEnabled(!Startup.IsEnabled); }
            catch (Exception error) { Notice("自启动设置未更改", error.Message); }
            startup.Checked = Startup.IsEnabled;
        };
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
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
            submenu.DropDownItems.Add("背景颜色…", null, (_, _) => PickPartitionColor(partition, true));
            submenu.DropDownItems.Add("标题颜色…", null, (_, _) => PickPartitionColor(partition, false));
            submenu.DropDownItems.Add("背景恢复跟随主题", null, (_, _) =>
                SetPartitionColors(partition, null, partition.TitleColor));
            submenu.DropDownItems.Add("标题恢复全局设置", null, (_, _) =>
                SetPartitionColors(partition, partition.BackgroundColor, null));
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
            SetPreferences(Preferences with { GlassEnabled = !State.GlassEnabled }))
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
            SetPreferences(Preferences with { DockEnabled = !State.DockEnabled });
        })
        { Checked = State.DockEnabled });
        section.DropDownItems.Add("添加应用…", null, (_, _) => PickDockItems());
        foreach (var item in State.DockItems)
        {
            var entry = new Forms.ToolStripMenuItem(item.Name);
            entry.DropDownItems.Add("向左移动", null, (_, _) => MoveDockItem(item, -1));
            entry.DropDownItems.Add("向右移动", null, (_, _) => MoveDockItem(item, 1));
            entry.DropDownItems.Add("从应用栏移除", null, (_, _) =>
                RemoveDockItem(item));
            section.DropDownItems.Add(entry);
        }
        menu.Items.Add(section);
    }

    private void MoveDockItem(DockItem item, int direction)
    {
        var from = State.DockItems.IndexOf(item);
        var to = Math.Clamp(from + direction, 0, State.DockItems.Count - 1);
        if (from == to) return;
        ReorderDockItem(item, to);
    }

    private void ShowHelp() => MessageBox.Show(
        "右键桌面空白处 → 新建 DeskNest 收纳卡片。\n" +
        "选中多个文件后右键 → 新建收纳卡片并收纳所选文件。\n" +
        "Windows 11 上这些菜单可能位于“显示更多选项”。\n\n" +
        "点击卡片名称直接编辑，Enter 保存、Esc 取消；拖动标题移动。\n" +
        "右下角调整大小，双击标题空白处或点击箭头折叠。\n" +
        "搜索框可模糊搜索文件名；Ctrl+F 定位搜索，Esc 清空。\n" +
        "文件双击打开，右键保留系统菜单；配置集中在托盘右键。\n" +
        "将应用、文件夹或快捷方式拖到 Dock 可固定；拖出移除固定，源文件保留。\n" +
        "Dock 内拖动调整顺序，Esc 取消拖动；悬停显示名称。\n\n" +
        "托盘“设置”可调整 Dock 高度、卡片透明度、主题和玻璃效果。\n" +
        "“开机自启动”控制当前用户登录后自动运行，可随时取消。\n\n" +
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
        if (settings != null && !settings.FlushChanges()) { settings.Activate(); return; }
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
        settings?.Close();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        foreach (var card in windows.Values.ToArray()) if (!card.HasClosed) card.Close();
        if (dock is { HasClosed: false }) dock.Close();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        trayIcon.Dispose();
        dataLease?.Dispose();
    }
}
