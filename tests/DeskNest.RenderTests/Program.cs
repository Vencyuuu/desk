using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskNest;
using DeskNest.Models;
using DeskNest.Services;
using DeskNest.Views;
using DeskNest.Interop;
using System.Linq;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/previews");
        Directory.CreateDirectory(output);
        var storage = new StorageService(Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")));
        var state = new AppState();
        string[] names = { "工作文档", "灵感素材", "常用工具" };
        string[] colors = { "#007AFF", "#32A86D", "#FF9F0A" };
        for (var index = 0; index < names.Length; index++)
        {
            var partition = new Partition { Name = names[index], Accent = colors[index] };
            state.Partitions.Add(partition);
            // 预览使用真实的隔离测试文件；不在正式用户数据目录中预置演示内容。
            File.WriteAllText(Path.Combine(storage.GetFolder(partition), "阅读说明.txt"), "DeskNest 界面测试文件");
            File.WriteAllText(Path.Combine(storage.GetFolder(partition), "待整理笔记.md"), "测试内容");
        }
        storage.Save(state);
        // 测试仍加载真实 App 资源，但 Dispatcher 不得启动正式常驻逻辑。
        var app = new App { StartDesktopOnStartup = false };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var startup = args.Contains("--settings") || args.Contains("--data-directory") || args.Contains("--typography")
            ? new MemoryStartupService() : null;
        using var controller = new AppController(storage, showTray: false, startup: startup);
        if (args.Contains("--drag-stability"))
        {
            controller.Start(installShell: false);
            var cards = controller.DesktopWindows.OfType<FenceWindow>().ToArray();
            cards[0].ToggleCollapsed();
            cards[1].ToggleCollapsed();
            PumpDispatcher();
            var stableDock = controller.DesktopWindows.OfType<DockWindow>().Single();
            var unrelatedMoves = 0;
            var notifications = 0;
            controller.Changed += () => notifications++;
            HwndSourceHook observe = (IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled) =>
            { if (message == 0x0046) unrelatedMoves++; return IntPtr.Zero; };
            var dockSource = HwndSource.FromHwnd(new WindowInteropHelper(stableDock).Handle);
            var otherSource = HwndSource.FromHwnd(new WindowInteropHelper(cards[1]).Handle);
            dockSource.AddHook(observe);
            otherSource.AddHook(observe);
            var before = DesktopLayer.Bounds(cards[0]);
            for (var index = 1; index <= 12; index++)
            {
                DesktopLayer.Place(cards[0], before.Left + index * 3, before.Top + index * 2, before.Width, before.Height);
                cards[0].SaveGeometry();
                PumpDispatcher();
            }
            dockSource.RemoveHook(observe);
            otherSource.RemoveHook(observe);
            Console.WriteLine($"Geometry notifications={notifications}; unrelated window placements={unrelatedMoves}");
            if (notifications != 0 || unrelatedMoves != 0)
                throw new Exception("移动卡片触发了无关卡片 / stableDock 的外观刷新或原生定位。");
            var saved = controller.Storage.Load().Partitions[0];
            if (saved.Left != before.Left + 36 || saved.Top != before.Top + 24)
                throw new Exception("移动后的卡片坐标未正确保存。");
            Console.WriteLine("PASS: Repeated collapsed-card movement saves geometry without refreshing or repositioning unrelated windows.");
            return;
        }
        if (args.Contains("--typography"))
        {
            ValidateTypography(controller, output);
            return;
        }
        if (args.Contains("--data-directory"))
        {
            ValidateDataDirectory(controller, startup!, output);
            return;
        }
        if (startup != null)
        {
            ValidateSettings(controller, startup, output);
            return;
        }
        if (args.Contains("--visual-probe"))
        {
            ProbeVisuals(controller, output, args.Contains("--assert-visuals"));
            return;
        }
        if (args.Contains("--pipeline"))
        {
            ValidatePipeline(storage.Root, output, args.Contains("--relocated"));
            return;
        }
        if (args.Contains("--desktop"))
        {
            ValidateDesktop(controller, output);
            return;
        }
        if (args.Contains("--card-interactions"))
        {
            ValidateCardInteractions(controller);
            return;
        }
        controller.State.DockItems.Add(new DockItem
        {
            Name = "资源管理器",
            Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
        });
        var fence = new FenceWindow(controller, controller.State.Partitions[0]);
        PumpDispatcher();
        Render((FrameworkElement)fence.Content, Path.Combine(output, "fence-light.png"), 328, 320);
        controller.SetTheme("dark");
        Render((FrameworkElement)fence.Content, Path.Combine(output, "fence-dark.png"), 328, 320);
        controller.SetTheme("light");
        var dock = new DockWindow(controller);
        PumpDispatcher();
        Render((FrameworkElement)dock.Content, Path.Combine(output, "dock-light.png"), 328, 92);
        dock.Close();
        fence.Close();
        Console.WriteLine("PASS: Desktop cards and Dock rendered without a management window.");
        Console.WriteLine(output);
    }

    private sealed class MemoryStartupService : IStartupService
    {
        public bool IsEnabled { get; private set; }
        public bool FailNextWrite { get; set; }
        public void SetEnabled(bool enabled)
        {
            if (FailNextWrite) { FailNextWrite = false; throw new UnauthorizedAccessException("测试写入失败"); }
            IsEnabled = enabled;
        }
    }

    private static void ValidateDataDirectory(AppController controller, MemoryStartupService startup, string output)
    {
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + label);
            Console.WriteLine("PASS: " + label);
        }
        void Complete(System.Threading.Tasks.Task task)
        {
            while (!task.IsCompleted) PumpDispatcher();
            task.GetAwaiter().GetResult();
            PumpDispatcher();
        }
        var sourceRoot = controller.Storage.Root;
        var partition = controller.State.Partitions[0];
        var sourceFile = Path.Combine(controller.Storage.GetFolder(partition), "阅读说明.txt");
        var shortcutName = "内部文件快捷方式.lnk";
        var shortcutFolder = controller.Storage.GetFolder(controller.State.Partitions[1]);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(Path.Combine(shortcutFolder, shortcutName));
        shortcut.TargetPath = sourceFile;
        shortcut.WorkingDirectory = Path.GetDirectoryName(sourceFile);
        shortcut.Save();
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
        controller.AddDockItems(new[] { Path.GetDirectoryName(sourceFile)! });
        controller.SetPreferences(controller.Preferences with { Theme = "dark", DockHeight = 100 });
        controller.Start(installShell: false);
        controller.ShowSettings();
        PumpDispatcher();
        var oldCard = controller.DesktopWindows.OfType<FenceWindow>().First();
        var target = Path.Combine(output, "migrated-" + Guid.NewGuid().ToString("N"));
        startup.SetEnabled(true);
        Complete(controller.ChangeDataDirectoryAsync(target, associate: false));
        Check(controller.Storage.Root == target && oldCard.HasClosed && controller.DesktopWindows.Count == 4,
            "迁移后重建全部桌面卡片和 Dock");
        Check(controller.State.Theme == "dark" && controller.State.DockHeight == 100 && startup.IsEnabled,
            "迁移保留外观配置和自启动状态");
        var targetFile = Path.Combine(controller.State.DockItems[0].Path, "阅读说明.txt");
        Check(File.Exists(sourceFile) && File.ReadAllText(targetFile) == File.ReadAllText(sourceFile),
            "迁移保留旧文件并重新关联可读取的 Dock 内部目标");
        dynamic movedShortcut = shell.CreateShortcut(Path.Combine(
            controller.Storage.GetFolder(controller.State.Partitions[1]), shortcutName));
        Check((string)movedShortcut.TargetPath == targetFile,
            "迁移重写托管快捷方式的内部目标");
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(movedShortcut);
        Check(new DataDirectoryLocation(sourceRoot).ResolveRoot() == target,
            "入口目录持久化指向迁移后的活动数据");
        var page = Application.Current.Windows.OfType<SettingsWindow>().Single();
        Check(((TextBox)page.FindName("DataDirectoryText")).Text == target && page.IsEnabled,
            "设置页显示新目录且切换完成后可继续编辑");
        var board = (FileBoard)controller.DesktopWindows.OfType<FenceWindow>().First().FindName("Board");
        var files = (ListBox)board.FindName("Files");
        var newFile = Path.Combine(controller.Storage.GetFolder(controller.State.Partitions[0]), "迁移后新增.txt");
        File.WriteAllText(newFile, "新目录监听");
        var elapsed = Stopwatch.StartNew();
        while (files.Items.Count != 3 && elapsed.Elapsed < TimeSpan.FromSeconds(5)) PumpDispatcher();
        Check(files.Items.Count == 3, "文件监听已切换到新目录");

        var failedTarget = Path.Combine(output, "failed-" + Guid.NewGuid().ToString("N"));
        startup.FailNextWrite = true;
        var failed = false;
        try { Complete(controller.ChangeDataDirectoryAsync(failedTarget, associate: false)); }
        catch (UnauthorizedAccessException) { failed = true; }
        Check(failed && controller.Storage.Root == target && !controller.IsDataChanging &&
            new DataDirectoryLocation(sourceRoot).ResolveRoot() == target && File.Exists(newFile),
            "切换前失败回退到旧活动数据及定位入口");
        var commitTarget = Path.Combine(output, "commit-failed-" + Guid.NewGuid().ToString("N"));
        using (var blocker = new FileStream(new DataDirectoryLocation(sourceRoot).LocationFile,
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failed = false;
            try { Complete(controller.ChangeDataDirectoryAsync(commitTarget, associate: false)); }
            catch (IOException) { failed = true; }
            Check(failed && controller.Storage.Root == target && controller.DesktopWindows.Count == 4 &&
                new DataDirectoryLocation(sourceRoot).ResolveRoot() == target,
                "定位文件提交失败恢复原卡片、文件监听和目录入口");
        }

        // 使用完整副本模拟手动搬移目录；保留原绝对路径提示以验证重新关联。
        var manual = Path.Combine(output, "manual-" + Guid.NewGuid().ToString("N"));
        PreparedDataDirectory? prepared = null;
        var copy = DataMigrationService.CopyAsync(controller.Storage, manual);
        Complete(copy);
        prepared = copy.Result;
        prepared.Dispose();
        Complete(controller.ChangeDataDirectoryAsync(manual, associate: true));
        Check(controller.Storage.Root == manual && Directory.Exists(controller.State.DockItems[0].Path) &&
            controller.State.DockItems[0].Path.StartsWith(manual, StringComparison.OrdinalIgnoreCase),
            "手动搬移副本重新关联后修复内部文件引用");
        dynamic associatedShortcut = shell.CreateShortcut(Path.Combine(
            controller.Storage.GetFolder(controller.State.Partitions[1]), shortcutName));
        Check((string)associatedShortcut.TargetPath == Path.Combine(controller.State.DockItems[0].Path, "阅读说明.txt"),
            "关联手动搬移的数据时同步修复内部快捷方式");
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(associatedShortcut);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        var scroll = Descendants<ScrollViewer>(page).First();
        scroll.ScrollToBottom();
        PumpDispatcher();
        Render((FrameworkElement)page.Content, Path.Combine(output, "settings-data-directory.png"), 520, 680);
        page.Close();
    }

    private static void ValidateTypography(AppController controller, string output)
    {
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            Console.WriteLine("PASS: " + label);
        }
        controller.Start(installShell: false);
        controller.ShowSettings();
        PumpDispatcher();
        var page = Application.Current.Windows.OfType<SettingsWindow>().Single();
        var cardFont = (ComboBox)page.FindName("CardFontChoice");
        var dockFont = (ComboBox)page.FindName("DockFontChoice");
        var settingsFont = (ComboBox)page.FindName("SettingsFontChoice");
        var choices = FontCatalog.Choices().Where(choice => choice.Family.Length > 0).Take(3).ToArray();
        Check(choices.Length == 3 && cardFont.Items.Count > 3, "字体列表来自系统已安装字体");
        var global = (ComboBox)page.FindName("GlobalFontChoice");
        global.SelectedValue = choices[0].Family;
        Check(page.FlushChanges() && controller.State.Typography.CardFont == choices[0].Family &&
            controller.State.Typography.DockFont == choices[0].Family &&
            controller.State.Typography.SettingsFont == choices[0].Family, "总字体一次同步三个区域并保存");
        cardFont.IsDropDownOpen = true;
        PumpDispatcher();
        var previewItem = (ComboBoxItem)cardFont.ItemContainerGenerator.ContainerFromItem(choices[0]);
        Check(previewItem.FontFamily.Source == choices[0].Family, "下拉字体条目使用该字体预览");
        cardFont.IsDropDownOpen = false;
        cardFont.SelectedValue = choices[0].Family;
        dockFont.SelectedValue = choices[1].Family;
        settingsFont.SelectedValue = choices[2].Family;
        ((Slider)page.FindName("TitleSizeSlider")).Value = 32;
        Check(page.FlushChanges(), "通过设置控件保存各区域字体与标题字号");
        PumpDispatcher();
        var fence = controller.DesktopWindows.OfType<FenceWindow>().First();
        var dock = controller.DesktopWindows.OfType<DockWindow>().Single();
        var title = (TextBlock)fence.FindName("PartitionTitle");
        var editor = (TextBox)fence.FindName("TitleEditor");
        Check(fence.FontFamily.Source == choices[0].Family && dock.FontFamily.Source == choices[1].Family &&
            page.FontFamily.Source == choices[2].Family, "卡片、Dock、设置页分别即时应用自己的字体");
        var tooltip = Descendants<Button>(dock).Select(button => button.ToolTip).OfType<ToolTip>().First();
        Check(tooltip.FontFamily.Source == choices[1].Family, "独立 Dock 名称提示应用 Dock 字体");
        Check(title.FontSize == 32 && editor.FontSize == 32 &&
            Descendants<TextBlock>(cardFont).Any(text => text.Text == choices[0].Name),
            "标题字号同步编辑框，字体选择框显示友好名称");
        fence.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            HwndSource.FromHwnd(new WindowInteropHelper(fence).Handle), 0, Key.F2)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        PumpDispatcher();
        var textHost = Descendants<ScrollViewer>(editor).First();
        Check(textHost.ScrollableHeight == 0 && editor.ActualHeight <= ((Grid)fence.FindName("TitleArea")).ActualHeight + 1,
            "最大标题字号编辑时不裁切文字");
        controller.SetPreferences(controller.Preferences with
        { Typography = controller.State.Typography with { CardTitleColor = "#336699" } });
        controller.SetTheme("dark");
        Check(((SolidColorBrush)title.Foreground).Color == Color.FromRgb(0x33, 0x66, 0x99) &&
            ((SolidColorBrush)editor.Foreground).Color == ((SolidColorBrush)title.Foreground).Color,
            "标题及编辑文字使用自定义颜色，切换主题保持颜色");
        var saved = controller.Storage.Load().Typography;
        Check(saved.CardFont == choices[0].Family && saved.DockFont == choices[1].Family &&
            saved.SettingsFont == choices[2].Family && saved.CardTitleSize == 32 && saved.CardTitleColor == "#336699",
            "字体、标题字号与颜色完整持久化");
        var partition = controller.State.Partitions[0];
        controller.SetPartitionColors(partition, "#123456", "#ABCDEF");
        Check(((GlassSurface)fence.FindName("Glass")).TintColor == Color.FromRgb(0x12, 0x34, 0x56) &&
            ((SolidColorBrush)title.Foreground).Color == Color.FromRgb(0xAB, 0xCD, 0xEF) &&
            controller.State.Partitions[1].TitleColor == null, "独立卡片背景色和标题色生效且不影响其他卡片");
        var savedCard = controller.Storage.Load().Partitions[0];
        Check(savedCard.BackgroundColor == "#123456" && savedCard.TitleColor == "#ABCDEF", "独立卡片颜色持久化");
        controller.SetPartitionColors(partition, null, null);
        Check(((GlassSurface)fence.FindName("Glass")).TintColor == null &&
            ((SolidColorBrush)title.Foreground).Color == Color.FromRgb(0x33, 0x66, 0x99),
            "卡片颜色可恢复主题背景和全局标题色");
        var heading = (TextBlock)page.FindName("TypographyHeading");
        Check(((SolidColorBrush)heading.Foreground).Color ==
            ((SolidColorBrush)Application.Current.Resources["LabelBrush"]).Color,
            "自定义字体后深色主题的普通文字继续使用主题颜色");
        var scroll = Descendants<ScrollViewer>(page).First();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + heading.TranslatePoint(new Point(), scroll).Y);
        PumpDispatcher();
        Render((FrameworkElement)page.Content, Path.Combine(output, "typography-settings.png"), 504, 642);
        var reset = Descendants<Button>(page).Single(button => Equals(button.Content, "跟随主题"));
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        page.FlushChanges();
        Check(controller.State.Typography.CardTitleColor == null && ((SolidColorBrush)title.Foreground).Color ==
            ((SolidColorBrush)Application.Current.Resources["LabelBrush"]).Color, "可恢复标题颜色跟随主题");
        var missing = "DeskNest-Missing-Test-Font";
        controller.SetPreferences(controller.Preferences with
        { Typography = controller.State.Typography with { CardFont = missing } });
        Check(fence.FontFamily.Source == FontCatalog.Resolve("").Source &&
            (string)cardFont.SelectedValue == missing, "未安装字体安全回退并保留配置选择");
        var rejected = false;
        try
        {
            controller.SetPreferences(controller.Preferences with
            { Typography = controller.State.Typography with { CardTitleSize = double.NaN } });
        }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && controller.State.Typography.CardTitleSize == 32, "非法标题字号被拒绝且不改变现有配置");
        page.Close();
        controller.ShowSettings();
        var reopened = Application.Current.Windows.OfType<SettingsWindow>().Single();
        Check((string)((ComboBox)reopened.FindName("CardFontChoice")).SelectedValue == missing &&
            ((Slider)reopened.FindName("TitleSizeSlider")).Value == 32, "重新打开设置保留字体及字号");
        reopened.Close();
    }

    private static void ValidateSettings(AppController controller, MemoryStartupService startup, string output)
    {
        void Check(bool value, string label)
        {
            if (!value) throw new Exception("FAIL: " + label);
            Console.WriteLine("PASS: " + label);
        }
        controller.Start(installShell: false);
        Check(!startup.IsEnabled, "启动程序不会默认启用自启动");
        controller.ShowSettings();
        controller.ShowSettings();
        PumpDispatcher();
        var pages = Application.Current.Windows.OfType<SettingsWindow>().ToArray();
        Check(pages.Length == 1, "托盘设置入口复用同一个设置页");
        var page = pages[0];
        var primary = Descendants<Button>(page).Single(button => Equals(button.Content, "完成"));
        var primaryText = Descendants<TextBlock>(primary).Single();
        Check(((SolidColorBrush)primaryText.Foreground).Color == Colors.White,
            "浅色主题下蓝色主按钮的实际文字为白色");
        var height = (Slider)page.FindName("HeightSlider");
        var offset = (Slider)page.FindName("BottomOffsetSlider");
        var transparency = (Slider)page.FindName("TransparencySlider");
        var theme = (ComboBox)page.FindName("ThemeChoice");
        var glass = (CheckBox)page.FindName("GlassCheck");
        var auto = (CheckBox)page.FindName("StartupCheck");
        height.Value = 120;
        transparency.Value = 60;
        theme.SelectedIndex = 2;
        glass.IsChecked = false;
        Check(page.FlushChanges(), "设置页可保存外观配置");
        var saved = controller.Storage.Load();
        Check(saved.DockHeight == 120 && saved.CardOpacity == 0.4 && saved.Theme == "dark" && !saved.GlassEnabled,
            "Dock 高度、透明度、主题和玻璃开关均持久化");
        PumpDispatcher();
        var dock = controller.DesktopWindows.OfType<DockWindow>().Single();
        Check(dock.Height == 120, "Dock 高度立即更新");
        var bounds = DesktopLayer.Bounds(dock);
        var dpi = VisualTreeHelper.GetDpi(dock);
        Check(Math.Abs(bounds.Bottom - (System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea.Bottom -
            Math.Round(12 * dpi.DpiScaleY))) <= 1,
            "调整高度后 Dock 仍贴合工作区底部");
        offset.Value = 60;
        Check(page.FlushChanges() && controller.Storage.Load().DockBottomOffset == 60 && dock.Height == 120,
            "底部间距独立保存且不改变 Dock 高度");
        PumpDispatcher();
        Check(Math.Abs(DesktopLayer.Bounds(dock).Bottom -
            (System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea.Bottom - Math.Round(60 * dpi.DpiScaleY))) <= 1,
            "只调整底部间距也会立即移动 Dock 并按 DPI 换算");
        Check(((SolidColorBrush)primaryText.Foreground).Color == Colors.White,
            "深色主题下蓝色主按钮的文字仍为白色");
        var fence = controller.DesktopWindows.OfType<FenceWindow>().First();
        Check(((GlassSurface)fence.FindName("Glass")).MaterialOpacity == 0.4 && fence.Opacity == 1,
            "透明度只改变卡片材质，文字和图标保持不透明");
        auto.IsChecked = true;
        Check(startup.IsEnabled, "设置页能启用自启动服务");
        auto.IsChecked = false;
        Check(!startup.IsEnabled, "设置页能关闭自启动服务");
        startup.FailNextWrite = true;
        auto.IsChecked = true;
        Check(!startup.IsEnabled && auto.IsChecked == false, "自启动写入失败后勾选状态回滚");
        transparency.Value = 80;
        height.Value = 64;
        Check(page.FlushChanges() && controller.State.CardOpacity == 0.2 && dock.Height == 64,
            "透明度和 Dock 高度边界值可保存并生效");
        offset.Value = 0;
        page.FlushChanges();
        PumpDispatcher();
        Check(DesktopLayer.Bounds(dock).Bottom == System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea.Bottom,
            "底部间距为零时贴合工作区底边");
        offset.Value = 300;
        Check(page.FlushChanges() && controller.Storage.Load().DockBottomOffset == 300,
            "底部间距上限可保存");
        height.Value = 100;
        using (var locked = new FileStream(
            controller.Storage.StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!page.FlushChanges() && controller.State.DockHeight == 64, "保存失败回滚内存配置，保留待重试输入");
        Check(page.FlushChanges() && controller.Storage.Load().DockHeight == 100, "解除文件占用后可重试保存");
        Render((FrameworkElement)page.Content, Path.Combine(output, "settings-dark.png"), 504, 642);
        theme.SelectedIndex = 1;
        page.FlushChanges();
        Render((FrameworkElement)page.Content, Path.Combine(output, "settings-light.png"), 504, 642);
        page.Close();
        controller.ShowSettings();
        var reopened = Application.Current.Windows.OfType<SettingsWindow>().Single();
        Check(((Slider)reopened.FindName("HeightSlider")).Value == 100 &&
            ((Slider)reopened.FindName("TransparencySlider")).Value == 80 &&
            ((Slider)reopened.FindName("BottomOffsetSlider")).Value == 300, "重新打开设置页保留先前配置及底部间距");
        reopened.Close();
    }

    private static void ProbeVisuals(AppController controller, string output, bool verify)
    {
        var fence = new FenceWindow(controller, controller.State.Partitions[0]);
        controller.State.DockItems.Add(new DockItem
        {
            Name = "资源管理器",
            Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
        });
        var dock = new DockWindow(controller);
        fence.Show();
        dock.Show();
        PumpDispatcher();
        var iconsReady = PumpUntil(() => Descendants<Button>((DependencyObject)dock.Content)
            .Where(button => button.Content is Image).All(button => ((Image)button.Content).Source != null));
        if (!iconsReady) Console.WriteLine("INFO: Shell icon pending; geometry is checked using the reserved icon area.");
        try
        {
            var editor = (TextBox)fence.FindName("TitleEditor");
            fence.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                HwndSource.FromHwnd(new WindowInteropHelper(fence).Handle), 0, System.Windows.Input.Key.F2)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            editor.Text = "项目文档完整名称";
            var board = (FileBoard)fence.FindName("Board");
            var search = (TextBox)board.FindName("SearchBox");
            search.Text = "阅读说明";
            fence.UpdateLayout();
            foreach (var input in new[] { editor, search })
            {
                var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                Console.WriteLine($"{input.Name}: height={input.ActualHeight}, viewport={host.ViewportHeight}, " +
                    $"extent={host.ExtentHeight}, scroll={host.ScrollableHeight}, padding={input.Padding}");
                if (verify && host.ScrollableHeight > 0.1) throw new Exception(input.Name + " 仍裁切文字。");
            }
            var buttons = Descendants<Button>((DependencyObject)dock.Content).ToArray();
            foreach (var button in buttons)
            {
                var image = button.Content as Image;
                var rectangle = button.TransformToAncestor((Visual)dock.Content)
                    .TransformBounds(new Rect(button.RenderSize));
                Console.WriteLine($"Dock button: {button.ToolTip}, margin={button.Margin}, " +
                    $"height={button.ActualHeight}, icon={image?.Source != null}, bounds={rectangle}");
            }
            Console.WriteLine("File icon glyph layers: " + Descendants<TextBlock>(board)
                .Count(text => text.Text == "\uE8A5" && text.IsVisible));
            if (verify)
            {
                if (buttons.Length != controller.State.DockItems.Count ||
                    buttons.Any(button => button.Tag is not DockItem))
                    throw new Exception("Dock 仍有多余的添加按钮。");
                var icon = (Image)buttons[0].Content;
                var rectangle = icon.TransformToAncestor((Visual)dock.Content).TransformBounds(new Rect(icon.RenderSize));
                var bottom = ((FrameworkElement)dock.Content).ActualHeight - rectangle.Bottom;
                if (Math.Abs(rectangle.Top - bottom) > 1) throw new Exception("Dock 上下图标留白不对称。");
                if (Descendants<TextBlock>(board).Any(text => text.Text == "\uE8A5" && text.IsVisible))
                    throw new Exception("文件图标仍与占位字形叠加。");
                buttons[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                var nameTip = (ToolTip)buttons[0].ToolTip;
                if (!nameTip.IsOpen || (string)nameTip.Content != controller.State.DockItems[0].Name)
                    throw new Exception("悬停名称提示未打开。");
                buttons[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                if (nameTip.IsOpen) throw new Exception("离开后名称提示未关闭。");
                Console.WriteLine("PASS: Full input lines, symmetric Dock, no plus or icon overlay, hover names.");
                ValidateDockRemoval(controller, dock, output);
            }
            Render((FrameworkElement)fence.Content, Path.Combine(output, "filled-card.png"), 328, 320);
            Render((FrameworkElement)dock.Content, Path.Combine(output, "dock.png"), (int)dock.Width, (int)dock.Height);
        }
        finally { fence.Close(); dock.Close(); }
    }

    private static bool PumpUntil(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) PumpDispatcher();
        // 系统 Shell 扩展的图标加载有外部时延，不与本轮布局 / 拖拽的确定性判定耦合。
        return ready();
    }

    private static void ValidateDockRemoval(AppController controller, DockWindow dock, string output)
    {
        var folder = Path.Combine(output, "dock-pin-source");
        Directory.CreateDirectory(folder);
        var sentinel = Path.Combine(folder, "source-kept.txt");
        File.WriteAllText(sentinel, "Dock removal must not delete this file.");
        controller.AddDockItems(new[] { folder });
        dock.RefreshItems();
        PumpDispatcher();
        var item = controller.State.DockItems.Single(entry => entry.Path == folder);
        var bounds = DesktopLayer.Bounds(dock);
        var outside = new Point(bounds.Right + 40, bounds.Top);
        dock.CompleteItemDrag(item, outside, cancelled: true);
        if (!controller.State.DockItems.Contains(item)) throw new Exception("取消拖动后固定项丢失。");
        var first = Descendants<Button>((DependencyObject)dock.Content).First();
        dock.CompleteItemDrag(item, first.PointToScreen(new Point(first.ActualWidth / 2, first.ActualHeight / 2)));
        if (controller.State.DockItems[0] != item) throw new Exception("Dock 内拖动排序失败。");
        dock.RefreshItems();
        PumpDispatcher();
        dock.CompleteItemDrag(item, outside);
        if (controller.State.DockItems.Contains(item) ||
            controller.Storage.Load().DockItems.Any(entry => entry.Path == folder))
            throw new Exception("拖出后固定项未从内存与持久化布局移除。");
        if (!File.Exists(sentinel) || File.ReadAllText(sentinel) != "Dock removal must not delete this file.")
            throw new Exception("拖出 Dock 修改了源文件。");
        Console.WriteLine("PASS: Pinning, drag cancellation, reorder, unpin, persistence and source retention.");
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void ValidateCardInteractions(AppController controller)
    {
        var partition = controller.State.Partitions[0];
        var folder = controller.Storage.GetFolder(partition);
        var fence = new FenceWindow(controller, partition);
        fence.Show();
        PumpDispatcher();
        try
        {
            var title = (TextBlock)fence.FindName("PartitionTitle");
            var editor = (TextBox)fence.FindName("TitleEditor");
            var board = (FileBoard)fence.FindName("Board");
            var search = (TextBox)board.FindName("SearchBox");
            var files = (ListBox)board.FindName("Files");
            void Key(UIElement target, System.Windows.Input.Key key) => target.RaiseEvent(
                new KeyEventArgs(Keyboard.PrimaryDevice,
                    HwndSource.FromHwnd(new WindowInteropHelper(fence).Handle), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            void Check(bool value, string label)
            {
                if (!value) throw new Exception("FAIL: " + label);
                Console.WriteLine("PASS: " + label);
            }

            // 直接向自己的 WPF 控件树发送路由事件，不注入操作系统鼠标或键盘。
            title.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });
            fence.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Check(editor.Visibility == Visibility.Visible, "点击标题进入行内编辑");
            editor.Text = "  项目资料  ";
            Key(editor, System.Windows.Input.Key.Enter);
            Check(partition.Name == "项目资料" && controller.Storage.Load().Partitions[0].Name == "项目资料" &&
                controller.Storage.GetFolder(partition) == folder, "Enter 保存名称，不改变托管文件目录");
            Key(fence, System.Windows.Input.Key.F2);
            editor.Text = "不应保存";
            Key(editor, System.Windows.Input.Key.Escape);
            Check(partition.Name == "项目资料" && editor.Visibility == Visibility.Collapsed, "Esc 取消更名");
            Key(fence, System.Windows.Input.Key.F2);
            editor.Text = " ";
            Key(editor, System.Windows.Input.Key.Enter);
            Check(editor.Visibility == Visibility.Visible && partition.Name == "项目资料", "空名称不能覆盖原标题");
            editor.Text = "失焦保存";
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, editor, search)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Check(controller.Storage.Load().Partitions[0].Name == "失焦保存", "移开焦点自动保存标题");
            search.Text = "读明";
            Check(files.Items.Count == 1 && ((FileTile)files.Items[0]).Entry.Name == "阅读说明.txt",
                "卡片搜索框即时模糊筛选中文文件名");
            search.Text = "不存在的关键词";
            Check(files.Items.Count == 0 && ((TextBlock)board.FindName("EmptyTitle")).Text == "没有匹配的文件",
                "无匹配结果时显示搜索空状态");
            Key(search, System.Windows.Input.Key.Escape);
            Check(search.Text.Length == 0 && files.Items.Count == 2, "Esc 清空搜索并恢复所有文件");
            Check(board.FindName("Status") == null && controller.Storage.GetFiles(partition).Count == 2,
                "底部信息栏已删除，搜索不改变托管文件");
        }
        finally { fence.Close(); }
    }


    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void ValidatePipeline(string dataRoot, string output, bool relocated = false)
    {
        var executable = Path.GetFullPath("artifacts/DeskNest-Desktop/DeskNest.exe");
        var activeRoot = dataRoot;
        if (relocated)
        {
            activeRoot = Path.Combine(output, "active-" + Guid.NewGuid().ToString("N"));
            using var prepared = DataMigrationService.CopyAsync(new StorageService(dataRoot), activeRoot)
                .GetAwaiter().GetResult();
            prepared.Store.Save(prepared.Store.Load());
            new DataDirectoryLocation(dataRoot).SetRoot(activeRoot);
        }
        Process Launch(params string[] commands)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(dataRoot);
            foreach (var command in commands) start.ArgumentList.Add(command);
            return Process.Start(start)!;
        }
        void WaitFor(Func<bool> check)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try { if (check()) return; } catch (IOException) { }
                Thread.Sleep(80);
            }
            throw new Exception("桌面进程端到端检查超时。");
        }
        int Count()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(activeRoot, "layout.json")));
            return json.RootElement.GetProperty("Partitions").GetArrayLength();
        }

        // 启动真正的常驻程序，用另一个进程投递清单，验证单实例和异步移动链路。
        using var application = Launch("--new-card");
        try
        {
            WaitFor(() => Count() == 4);
            var source = Path.Combine(output, "pipeline-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);
            var first = Path.Combine(source, "多进程第一份.txt");
            var second = Path.Combine(source, "多进程第二份.txt");
            File.WriteAllText(first, "pipeline first");
            File.WriteAllText(second, "pipeline second");
            Directory.CreateDirectory(ShellIntegration.RequestsDirectory);
            var request = Path.Combine(ShellIntegration.RequestsDirectory, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(request, JsonSerializer.Serialize(
                new DesktopRequest { Action = "new", Paths = new[] { first, second } }));
            using (var client = Launch("--shell-request", request))
            {
                if (!client.WaitForExit(8000) || client.ExitCode != 0)
                    throw new Exception("第二个进程没有成功投递桌面选择请求。");
            }
            WaitFor(() => Count() == 5 && !File.Exists(first) && !File.Exists(second));
            application.Refresh();
            if (application.MainWindowHandle != IntPtr.Zero)
                throw new Exception("常驻进程意外出现了普通主窗口。");
            File.WriteAllText(Path.Combine(output, "pipeline-result.txt"),
                "PASS: Actual app has no top-level main window; second process delivered one two-file selection.");
            Console.WriteLine("PASS: Real desktop app receives multi-file selection from second process; no main window.");
            if (relocated)
            {
                using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "layout.json")));
                if (original.RootElement.GetProperty("Partitions").GetArrayLength() != 3)
                    throw new Exception("迁移后命令错误地写回旧数据目录。");
                Console.WriteLine("PASS: Stable profile routes startup, Shell selection and quit to relocated active data.");
            }
        }
        finally
        {
            if (!application.HasExited)
            {
                using var quit = Launch("--quit");
                quit.WaitForExit(8000);
                if (!application.WaitForExit(8000)) application.Kill();
            }
        }
        if (relocated)
        {
            using var restarted = Launch("--new-card");
            try
            {
                WaitFor(() => Count() == 6);
                Console.WriteLine("PASS: Restart resolves relocated data and preserves previous cards.");
            }
            finally
            {
                using var quit = Launch("--quit");
                quit.WaitForExit(8000);
                if (!restarted.WaitForExit(8000)) restarted.Kill();
            }
        }
    }

    private static void ValidateDesktop(AppController controller, string output)
    {
        controller.Start(installShell: false);
        var frame = new DispatcherFrame();
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                if (controller.DesktopWindows.Count != 4 ||
                    controller.DesktopWindows.Any(window => !DesktopLayer.IsAttached(window) || window.ShowInTaskbar))
                    throw new Exception("卡片或 Dock 未嵌入 Explorer 桌面。");
                var card = (FenceWindow)controller.DesktopWindows[0];
                var original = DesktopLayer.Bounds(card);
                card.ToggleCollapsed();
                if (DesktopLayer.Bounds(card).Height >= original.Height)
                    throw new Exception("桌面卡片折叠没有改变原生尺寸。");
                card.ToggleCollapsed();
                DesktopLayer.Place(card, original.Left + 24, original.Top + 24, original.Width, original.Height);
                card.SaveGeometry();
                var saved = controller.Storage.Load().Partitions[0];
                if (!saved.PhysicalCoordinates || saved.Left != original.Left + 24 || saved.Top != original.Top + 24)
                    throw new Exception("桌面卡片移动后未保存真实屏幕坐标。");
                var source = Path.Combine(output, "selection-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(source);
                var first = Path.Combine(source, "中文第一份.txt");
                var second = Path.Combine(source, "第二份.txt");
                File.WriteAllText(first, "first");
                File.WriteAllText(second, "second");
                await controller.HandleRequestAsync(new DesktopRequest
                { Action = "new", Paths = new[] { first, second } });
                var partition = controller.State.Partitions.Last();
                if (controller.State.Partitions.Count != 4 || controller.Storage.GetFiles(partition).Count != 2 ||
                    File.Exists(first) || File.Exists(second))
                    throw new Exception("多选命令未创建一张卡片并完整移动两份文件。");
                var diagnostics = controller.DesktopWindows.Select(window => new
                {
                    window.Title,
                    Embedded = DesktopLayer.IsAttached(window),
                    window.ShowInTaskbar,
                    Bounds = DesktopLayer.Bounds(window),
                });
                File.WriteAllText(Path.Combine(output, "desktop-integration.json"),
                    JsonSerializer.Serialize(diagnostics,
                        new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
                Console.WriteLine("PASS: Cards and Dock are Explorer children, absent from taskbar.");
                Console.WriteLine("PASS: One selection request created exactly one card and moved both fixture files.");
                Console.WriteLine("PASS: Embedded card collapse, movement and physical-coordinate persistence.");
            }
            catch (Exception error) { failure = error; }
            finally { frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (failure != null) throw failure;
    }

    private static void Render(FrameworkElement element, string output, int width = 1040, int height = 668)
    {
        // 直接渲染本项目的 WPF 可视树，不操作或截取其他应用窗口。
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var content = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        content.Render(element);
        var scene = new DrawingVisual();
        using (var drawing = scene.RenderOpen())
        {
            var rectangle = new Rect(0, 0, width, height);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(228, 233, 238)), null, rectangle);
            drawing.DrawImage(content, rectangle);
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(scene);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }
}
