using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskNest.Interop;
using DeskNest.Models;
using DeskNest.Services;

namespace DeskNest.Views;

public sealed class DockWindow : Window
{
    private readonly AppController controller;
    private readonly GlassSurface glass = new();
    private readonly Dictionary<string, ImageSource> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel items = new()
    { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Popup dragPreview = new()
    {
        AllowsTransparency = true,
        Placement = PlacementMode.AbsolutePoint,
        StaysOpen = true,
        IsHitTestVisible = false,
        Focusable = false,
    };
    private readonly Image dragImage = new() { Width = 40, Height = 40, Opacity = 0.85 };
    private readonly TextBlock dragHint = new()
    {
        FontSize = 11,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(4),
    };
    private Button? pointerButton;
    private DockItem? pointerItem;
    private Point pointerStart;
    private bool dragging;
    private bool suppressClick;
    private int generation;
    public bool HasClosed { get; private set; }

    public DockWindow(AppController app)
    {
        controller = app;
        Style = (Style)FindResource(typeof(Window));
        Title = "DeskNest · 快捷应用栏";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Height = controller.State.DockHeight;
        // 拖动预览位于独立 Popup，显式使用主题文字色，不依赖主窗口的颜色继承。
        dragHint.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        var preview = new StackPanel();
        preview.Children.Add(dragImage);
        var hintSurface = new Border
        {
            CornerRadius = new CornerRadius(5),
            Child = dragHint,
        };
        hintSurface.SetResourceReference(Border.BackgroundProperty, "ContentBrush");
        preview.Children.Add(hintSurface);
        dragPreview.Child = preview;
        var scene = new Grid();
        scene.Children.Add(glass);
        var strip = new Grid();
        strip.Children.Add(items);
        var scroll = new ScrollViewer
        {
            Content = strip,
            Margin = new Thickness(12, 0, 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        scroll.SizeChanged += (_, e) => strip.MinWidth = e.NewSize.Width;
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (scroll.ScrollableWidth <= 0) return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        scene.Children.Add(scroll);
        Content = scene;
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) controller.AddDockItems(paths);
            e.Handled = true;
        };
        controller.Changed += ApplyAppearance;
        SourceInitialized += (_, _) => EnsureAttached();
        Loaded += (_, _) => { EnsureAttached(); if (!DesktopLayer.IsAttached(this)) Hide(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelPointer(); };
        Closed += (_, _) =>
        {
            HasClosed = true;
            generation++;
            CancelPointer();
            CloseNames();
            controller.Changed -= ApplyAppearance;
        };
        RefreshItems();
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        FontFamily = FontCatalog.Resolve(controller.State.Typography.DockFont);
        dragHint.FontFamily = FontFamily;
        foreach (var button in items.Children.OfType<Button>())
            if (button.ToolTip is ToolTip name) name.FontFamily = FontFamily;
        glass.GlassEnabled = controller.State.GlassEnabled;
        glass.InvalidateVisual();
        if (Math.Abs(Height - controller.State.DockHeight) > 0.01)
        {
            CancelPointer();
            Height = controller.State.DockHeight;
            foreach (var button in items.Children.OfType<Button>()) ResizeButton(button);
        }
        // 距底部间距独立于 Dock 高度，间距变化也必须立即更新实际桌面位置。
        Reposition();
    }

    public async void RefreshItems()
    {
        if (HasClosed) return;
        CancelPointer();
        CloseNames();
        var revision = ++generation;
        items.Children.Clear();
        // 先同步生成所有命中区域，图标再异步加载，避免加载时 Dock 宽度与内容不一致。
        var pending = controller.State.DockItems.Select(item => (Item: item, Image: AddButton(item))).ToArray();
        if (pending.Length == 0)
        {
            var empty = new TextBlock
            {
                Text = "拖入应用或文件夹",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                IsHitTestVisible = false,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
            items.Children.Add(empty);
        }
        Reposition();
        foreach (var entry in pending)
        {
            if (entry.Image.Source != null) continue;
            try
            {
                var source = await Task.Run(() => ShellService.GetIcon(entry.Item.Path));
                if (HasClosed || revision != generation) return;
                if (source != null) iconCache[entry.Item.Path] = source;
                entry.Image.Source = source;
            }
            catch (Exception error) { controller.Notice("应用图标未加载", entry.Item.Name + "：" + error.Message); }
        }
    }

    private Image AddButton(DockItem item)
    {
        var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform };
        if (iconCache.TryGetValue(item.Path, out var cachedIcon)) icon.Source = cachedIcon;
        var scale = new ScaleTransform(1, 1);
        var button = new Button
        {
            Width = 54,
            Height = 54,
            Padding = new Thickness(7),
            Margin = new Thickness(3, 17, 3, 17),
            Background = Brushes.Transparent,
            Content = icon,
            Tag = item,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var name = new ToolTip
        {
            FontFamily = FontCatalog.Resolve(controller.State.Typography.DockFont),
            Content = item.Name,
            Placement = PlacementMode.Top,
            PlacementTarget = button,
            VerticalOffset = -8,
            StaysOpen = true,
            Focusable = false,
            Padding = new Thickness(8, 5, 8, 5),
        };
        name.SetResourceReference(Control.BackgroundProperty, "ContentBrush");
        name.SetResourceReference(Control.ForegroundProperty, "LabelBrush");
        name.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        button.ToolTip = name;
        // 桌面嵌入的 HWND 不一定是活动窗口，显式控制名称提示，避免默认服务不弹出。
        ToolTipService.SetIsEnabled(button, false);
        System.Windows.Automation.AutomationProperties.SetName(button, "打开 " + item.Name);
        button.Click += (_, _) =>
        {
            if (suppressClick) return;
            name.IsOpen = false;
            try { ShellService.Open(item.Path); }
            catch (Exception error) { controller.Notice("无法打开应用", error.Message); }
        };
        button.MouseEnter += (_, _) =>
        {
            if (dragging) return;
            name.IsOpen = true;
            Magnify(scale, 1.22);
        };
        button.MouseLeave += (_, _) => { name.IsOpen = false; Magnify(scale, 1); };
        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            name.IsOpen = false;
            pointerButton = button;
            pointerItem = item;
            pointerStart = CursorPoint();
            suppressClick = false;
        };
        button.PreviewMouseMove += (_, e) => MovePointer(button, e);
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var moved = dragging;
            var selected = pointerItem;
            var point = CursorPoint();
            if (!moved)
            {
                // 普通点击的捕获由 Button 自己释放，否则会阻止正常的 Click / 应用启动。
                pointerButton = null;
                pointerItem = null;
                return;
            }
            CancelPointer();
            if (selected == null) return;
            e.Handled = true;
            try { CompleteItemDrag(selected, point); }
            catch (Exception error) { controller.Notice("Dock 未更新", error.Message); }
        };
        button.LostMouseCapture += (_, _) => { if (pointerButton == button) CancelPointer(); };
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !dragging) return;
            CancelPointer();
            e.Handled = true;
        };
        items.Children.Add(button);
        ResizeButton(button);
        return icon;
    }

    private void ResizeButton(Button button)
    {
        // 图标随 Dock 高度缩放，上下留白始终相等；固定项与图标缓存不重新创建。
        var iconSize = Math.Clamp(40 * Height / 88, 24, 64);
        var buttonSize = iconSize + 14;
        button.Width = buttonSize;
        button.Height = buttonSize;
        var inset = (Height - buttonSize) / 2;
        button.Margin = new Thickness(3, inset, 3, inset);
        if (button.Content is Image icon) { icon.Width = iconSize; icon.Height = iconSize; }
    }

    private void MovePointer(Button button, MouseEventArgs e)
    {
        if (pointerButton != button || pointerItem == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelPointer(); return; }
        var point = CursorPoint();
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!dragging)
        {
            if (Math.Abs(point.X - pointerStart.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
                Math.Abs(point.Y - pointerStart.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
            if (!button.CaptureMouse()) return;
            dragging = true;
            suppressClick = true;
            CloseNames();
            button.Opacity = 0.45;
            dragImage.Source = ((Image)button.Content).Source;
        }
        dragHint.Text = ContainsScreenPoint(point) ? "松开调整顺序" : "松开移除固定项";
        dragPreview.HorizontalOffset = point.X / dpi.DpiScaleX + 12;
        dragPreview.VerticalOffset = point.Y / dpi.DpiScaleY - 20;
        dragPreview.IsOpen = true;
        e.Handled = true;
    }

    public void CompleteItemDrag(DockItem item, Point screenPoint, bool cancelled = false)
    {
        if (cancelled) return;
        // 自己捕获鼠标完成移出，不向资源管理器发送 FileDrop，避免在桌面意外创建文件或快捷方式。
        if (!ContainsScreenPoint(screenPoint)) { controller.RemoveDockItem(item); return; }
        var buttons = items.Children.OfType<Button>().ToArray();
        var closest = buttons.Select((button, index) => new
        {
            Index = index,
            Distance = Math.Abs(button.PointToScreen(new Point(button.ActualWidth / 2, 0)).X - screenPoint.X),
        }).OrderBy(candidate => candidate.Distance).FirstOrDefault();
        if (closest != null) controller.ReorderDockItem(item, closest.Index);
    }

    private bool ContainsScreenPoint(Point point)
    {
        var bounds = DesktopLayer.Bounds(this);
        return point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }

    private static Point CursorPoint()
    {
        var point = DesktopLayer.Cursor();
        return new Point(point.X, point.Y);
    }

    private void CloseNames()
    {
        foreach (var button in items.Children.OfType<Button>())
            if (button.ToolTip is ToolTip name) name.IsOpen = false;
    }

    private void CancelPointer()
    {
        var button = pointerButton;
        pointerButton = null;
        pointerItem = null;
        dragging = false;
        suppressClick = false;
        dragPreview.IsOpen = false;
        if (button == null) return;
        button.Opacity = 1;
        if (button.IsMouseCaptured) button.ReleaseMouseCapture();
    }

    private static void Magnify(ScaleTransform scale, double value)
    {
        var duration = SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
        var animation = new DoubleAnimation(value, duration) { EasingFunction = new QuadraticEase() };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    public void EnsureAttached()
    {
        if (HasClosed || DesktopLayer.IsAttached(this)) return;
        if (DesktopLayer.Attach(this))
        {
            Reposition();
            if (IsLoaded && controller.State.DockEnabled) Show();
        }
    }

    public void Reposition()
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var slot = Math.Clamp(40 * Height / 88, 24, 64) + 20;
        Width = Math.Min(Math.Max(144, controller.State.DockItems.Count * slot + 24), area.Width / dpi.DpiScaleX - 32);
        if (!DesktopLayer.IsAttached(this)) return;
        var width = (int)(Width * dpi.DpiScaleX);
        var height = (int)(Height * dpi.DpiScaleY);
        // 设置使用逻辑像素；转换成屏幕物理坐标，并限制到工作区内，避免小屏幕上移出桌面。
        var bottomOffset = Math.Clamp((int)Math.Round(controller.State.DockBottomOffset * dpi.DpiScaleY),
            0, Math.Max(0, area.Height - height));
        DesktopLayer.Place(this, area.Left + (area.Width - width) / 2,
            area.Bottom - height - bottomOffset, width, height);
        glass.InvalidateVisual();
    }
}
