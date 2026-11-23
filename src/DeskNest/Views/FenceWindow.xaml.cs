using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskNest.Interop;
using DeskNest.Models;
using DeskNest.Services;

namespace DeskNest.Views;

public partial class FenceWindow : Window
{
    private readonly AppController controller;
    private readonly Partition partition;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private DesktopLayer.NativePoint dragStart;
    private DesktopLayer.NativeRect initialBounds;
    private bool dragging;
    private bool titleClickPending;
    private bool headerMoved;
    private bool editingTitle;
    private bool finishingTitle;
    private bool placed;
    private bool requestedVisible = true;
    public bool HasClosed { get; private set; }

    public FenceWindow(AppController app, Partition value)
    {
        InitializeComponent();
        controller = app;
        partition = value;
        Width = Math.Clamp(value.Width, 260, SystemParameters.VirtualScreenWidth);
        Height = value.Collapsed ? 50 : Math.Max(180, value.Height);
        var visible = false;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var bounds = screen.WorkingArea;
            if (value.Left + Width > bounds.Left && value.Left < bounds.Right &&
                value.Top + 48 > bounds.Top && value.Top < bounds.Bottom) visible = true;
        }
        Left = visible ? value.Left : SystemParameters.WorkArea.Left + 36;
        Top = visible ? value.Top : SystemParameters.WorkArea.Top + 48;
        if (!visible) { value.Left = Left; value.Top = Top; }
        Board.Bind(controller, partition);
        Board.CountChanged += count => { Count.Text = count.ToString(); controller.NotifyFilesChanged(); };
        Count.Text = controller.Storage.GetFiles(partition).Count.ToString();
        UpdateHeader();
        controller.Changed += ApplyAppearance;
        ApplyAppearance();
        SourceInitialized += (_, _) => EnsureAttached();
        Loaded += (_, _) => { EnsureAttached(); if (!DesktopLayer.IsAttached(this)) Hide(); };
        SizeChanged += (_, _) => { Glass.InvalidateVisual(); ScheduleSave(); };
        PreviewMouseMove += OnPointerMove;
        PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => { dragging = false; titleClickPending = false; };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveGeometry(); };
        Closed += (_, _) =>
        {
            HasClosed = true;
            saveTimer.Stop();
            controller.Changed -= ApplyAppearance;
            Board.Dispose();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            { controller.AddPartition(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (partition.Collapsed) ToggleCollapsed();
                Board.FocusSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.F2) { BeginTitleEdit(); e.Handled = true; }
        };
    }

    public void EnsureAttached()
    {
        if (HasClosed || DesktopLayer.IsAttached(this)) return;
        if (!DesktopLayer.Attach(this)) return;
        if (!placed && partition.PhysicalCoordinates)
        {
            var bounds = DesktopLayer.Bounds(this);
            DesktopLayer.Place(this, (int)partition.Left, (int)partition.Top, bounds.Width, bounds.Height);
        }
        placed = true;
        Glass.InvalidateVisual();
        if (requestedVisible && IsLoaded) Show();
    }

    public void Reveal()
    {
        requestedVisible = true;
        EnsureAttached();
        if (DesktopLayer.IsAttached(this)) Show();
    }

    public void HideOnDesktop() { requestedVisible = false; Hide(); }
    public void RefreshFiles() => Board.Refresh();
    public void RestoreSelected() => Board.RestoreSelected();

    private void ApplyAppearance()
    {
        Glass.GlassEnabled = controller.State.GlassEnabled;
        Glass.InvalidateVisual();
    }

    public void UpdateHeader()
    {
        Title = "DeskNest · " + partition.Name;
        PartitionTitle.Text = partition.Name;
        AccentMark.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(partition.Accent));
        Board.Visibility = partition.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeHandle.Visibility = partition.Locked || partition.Collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        var onTitle = false;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = EventParent(node))
        {
            if (node is Button or TextBox) return;
            if (node == TitleArea) onTitle = true;
        }
        if (e.ClickCount == 2 && !onTitle) { ToggleCollapsed(); return; }
        titleClickPending = onTitle;
        headerMoved = false;
        dragStart = DesktopLayer.Cursor();
        initialBounds = DesktopLayer.Bounds(this);
        if (DesktopLayer.IsAttached(this)) dragging = CaptureMouse();
        e.Handled = true;
    }

    private static DependencyObject? EventParent(DependencyObject node) => node is ContentElement content
        ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
        : VisualTreeHelper.GetParent(node);

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        var cursor = DesktopLayer.Cursor();
        var dpi = VisualTreeHelper.GetDpi(this);
        // 超过系统拖动阈值才移动；轻点标题进入编辑，拖动标题则不会误触重命名。
        if (!headerMoved &&
            Math.Abs(cursor.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
            Math.Abs(cursor.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
        headerMoved = true;
        titleClickPending = false;
        if (partition.Locked) return;
        DesktopLayer.Place(this, initialBounds.Left + cursor.X - dragStart.X,
            initialBounds.Top + cursor.Y - dragStart.Y, initialBounds.Width, initialBounds.Height);
        Glass.InvalidateVisual();
    }

    private void EndDrag()
    {
        if (!dragging && !titleClickPending) return;
        var edit = titleClickPending && !headerMoved;
        dragging = false;
        titleClickPending = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (headerMoved) ScheduleSave();
        if (edit) BeginTitleEdit();
    }

    private void BeginTitleEdit()
    {
        if (editingTitle) return;
        editingTitle = true;
        TitleEditor.Text = partition.Name;
        TitleEditor.ToolTip = "Enter 保存，Esc 取消";
        PartitionTitle.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = Visibility.Visible;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void FinishTitleEdit(bool save, bool keepInvalidInput = false)
    {
        if (!editingTitle || finishingTitle) return;
        if (save && string.IsNullOrWhiteSpace(TitleEditor.Text) && keepInvalidInput)
        {
            TitleEditor.ToolTip = "名称不能为空";
            return;
        }
        finishingTitle = true;
        try
        {
            // Enter 或移开键盘焦点保存；空白输入失焦时保留原名称；Esc 完整取消。
            if (save) controller.RenamePartition(partition, TitleEditor.Text);
            editingTitle = false;
            PartitionTitle.Text = partition.Name;
            TitleEditor.Visibility = Visibility.Collapsed;
            PartitionTitle.Visibility = Visibility.Visible;
        }
        catch (Exception error) { controller.Notice("名称未保存", error.Message); }
        finally { finishingTitle = false; }
    }

    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FinishTitleEdit(true, keepInvalidInput: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { FinishTitleEdit(false); e.Handled = true; }
    }

    private void OnTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishTitleEdit(true);

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        if (partition.Locked || partition.Collapsed) return;
        DesktopLayer.Resize(this, Math.Max(260, ActualWidth + e.HorizontalChange),
            Math.Max(180, ActualHeight + e.VerticalChange));
        Glass.InvalidateVisual();
        ScheduleSave();
    }

    private void OnToggle(object sender, RoutedEventArgs e) => ToggleCollapsed();

    public void ToggleCollapsed()
    {
        if (!partition.Collapsed) partition.Height = ActualHeight > 0 ? ActualHeight : Height;
        partition.Collapsed = !partition.Collapsed;
        DesktopLayer.Resize(this, Width, partition.Collapsed ? 50 : Math.Max(180, partition.Height));
        UpdateHeader();
        SaveGeometry();
        controller.Persist();
    }

    private void ScheduleSave()
    {
        if (!placed) return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    public void SaveGeometry()
    {
        if (!placed || !DesktopLayer.IsAttached(this) || !controller.State.Partitions.Contains(partition)) return;
        var bounds = DesktopLayer.Bounds(this);
        var dpi = VisualTreeHelper.GetDpi(this);
        partition.Left = bounds.Left;
        partition.Top = bounds.Top;
        partition.Width = bounds.Width / dpi.DpiScaleX;
        if (!partition.Collapsed) partition.Height = bounds.Height / dpi.DpiScaleY;
        partition.PhysicalCoordinates = true;
        controller.Persist();
    }
}
