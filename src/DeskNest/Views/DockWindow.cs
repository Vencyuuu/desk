using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskNest.Interop;
using DeskNest.Services;

namespace DeskNest.Views;

public sealed class DockWindow : Window
{
    private readonly AppController controller;
    private readonly GlassSurface glass = new();
    private readonly StackPanel items = new() { Orientation = Orientation.Horizontal };
    private int generation;
    public bool HasClosed { get; private set; }

    public DockWindow(AppController app)
    {
        controller = app;
        Title = "DeskNest · 快捷应用栏";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Height = 92;
        var scene = new Grid();
        scene.Children.Add(glass);
        var scroll = new ScrollViewer
        {
            Content = items,
            Margin = new Thickness(12, 0, 12, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
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
        Closed += (_, _) => { HasClosed = true; generation++; controller.Changed -= ApplyAppearance; };
        RefreshItems();
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        glass.GlassEnabled = controller.State.GlassEnabled;
        glass.InvalidateVisual();
    }

    public async void RefreshItems()
    {
        if (HasClosed) return;
        var revision = ++generation;
        items.Children.Clear();
        foreach (var item in controller.State.DockItems.ToArray())
        {
            var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform };
            var scale = new ScaleTransform(1, 1);
            var button = new Button
            {
                Width = 54,
                Height = 54,
                Padding = new Thickness(7),
                Margin = new Thickness(3, 24, 3, 10),
                Background = Brushes.Transparent,
                Content = icon,
                ToolTip = item.Name,
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 1),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "打开 " + item.Name);
            button.Click += (_, _) =>
            {
                try { ShellService.Open(item.Path); }
                catch (Exception error) { controller.Notice("无法打开应用", error.Message); }
            };
            button.MouseEnter += (_, _) => Magnify(scale, 1.22);
            button.MouseLeave += (_, _) => Magnify(scale, 1);
            items.Children.Add(button);
            try
            {
                var source = await Task.Run(() => ShellService.GetIcon(item.Path));
                if (HasClosed || revision != generation) return;
                icon.Source = source;
            }
            catch (Exception error) { button.ToolTip = item.Name + " · " + error.Message; }
        }
        var add = new Button
        {
            Content = "\uE710",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Background = Brushes.Transparent,
            Width = 42,
            Height = 48,
            Margin = new Thickness(8, 27, 0, 10),
            ToolTip = "添加应用；也可拖入快捷方式",
        };
        add.Click += (_, _) => controller.PickDockItems();
        items.Children.Add(add);
        Reposition();
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
        Width = Math.Min((controller.State.DockItems.Count * 60 + 88), area.Width / dpi.DpiScaleX - 32);
        if (!DesktopLayer.IsAttached(this)) return;
        var width = (int)(Width * dpi.DpiScaleX);
        var height = (int)(Height * dpi.DpiScaleY);
        DesktopLayer.Place(this, area.Left + (area.Width - width) / 2, area.Bottom - height - 12, width, height);
        glass.InvalidateVisual();
    }
}
