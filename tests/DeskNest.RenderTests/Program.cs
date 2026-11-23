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
        using var controller = new AppController(storage, showTray: false);
        if (args.Contains("--pipeline"))
        {
            ValidatePipeline(storage.Root, output);
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

    private static void ValidatePipeline(string dataRoot, string output)
    {
        var executable = Path.GetFullPath("artifacts/DeskNest-Desktop/DeskNest.exe");
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
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "layout.json")));
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
