using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskNest.Interop;
using DeskNest.Models;
using DeskNest.Services;

namespace DeskNest.Views;

public partial class FileBoard : UserControl, IDisposable
{
    private AppController? controller;
    private Partition? partition;
    private FileSystemWatcher? watcher;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Point dragStart;
    private bool dragCandidate;
    private bool busy;
    private bool disposed;
    private string query = "";
    private FileTile[] allTiles = Array.Empty<FileTile>();
    private int generation;
    public event Action<int>? CountChanged;

    public FileBoard()
    {
        InitializeComponent();
        debounce.Tick += (_, _) => { debounce.Stop(); Refresh(); };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
    }

    public void Bind(AppController app, Partition value)
    {
        watcher?.Dispose();
        controller = app;
        partition = value;
        var folder = app.Storage.GetFolder(value);
        watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        watcher.Created += OnFileChanged;
        watcher.Deleted += OnFileChanged;
        watcher.Renamed += OnFileChanged;
        watcher.Changed += OnFileChanged;
        watcher.Error += (_, _) => QueueRefresh();
        Refresh();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => QueueRefresh();

    private void QueueRefresh()
    {
        if (disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (!disposed) { debounce.Stop(); debounce.Start(); } });
    }

    public void Search(string text) => SearchBox.Text = text;

    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        query = SearchBox.Text;
        if (Files != null) ApplyFilter();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        { SearchBox.Clear(); e.Handled = true; }
    }

    private void ApplyFilter()
    {
        // 输入只过滤缓存，避免每敲一个字都重新枚举目录和提取 Shell 图标。
        var selected = Files.SelectedItems.Cast<FileTile>().Select(tile => tile.Entry.Path).ToHashSet();
        var matches = allTiles.Where(tile => FileNameSearch.Matches(tile.Entry.Name, query)).ToArray();
        Files.ItemsSource = matches;
        foreach (var tile in matches.Where(tile => selected.Contains(tile.Entry.Path))) Files.SelectedItems.Add(tile);
        EmptyState.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searching = !string.IsNullOrWhiteSpace(query);
        EmptyTitle.Text = searching ? "没有匹配的文件" : "把文件拖到这里";
        EmptyDescription.Text = searching ? "换一个关键词试试" : "文件会移动到此分区，原位置不再保留";
    }

    public async void Refresh()
    {
        if (controller == null || partition == null || disposed) return;
        var token = ++generation;
        try
        {
            var entries = controller.Storage.GetFiles(partition);
            var cached = allTiles.ToDictionary(tile => tile.Entry.Path);
            allTiles = entries.Select(entry => cached.GetValueOrDefault(entry.Path) ?? new FileTile(entry)).ToArray();
            ApplyFilter();
            CountChanged?.Invoke(entries.Count);
            // 图标提取放到后台并限制串行处理，慢速 Shell 扩展不会阻塞拖拽与窗口交互。
            foreach (var tile in allTiles.Where(tile => tile.Icon == null))
            {
                var icon = await Task.Run(() => ShellService.GetIcon(tile.Entry.Path));
                if (disposed || generation != token) return;
                tile.Icon = icon;
            }
        }
        catch (Exception error) { controller.Notice("刷新文件失败", error.Message); }
    }

    public async Task ImportAsync(IEnumerable<string> paths)
    {
        if (busy || controller == null || partition == null) return;
        var app = controller;
        var target = partition;
        busy = true;
        app.ActiveOperations++;
        try
        {
            var items = paths.ToArray();
            var result = await Task.Run(() => app.Storage.Import(target, items));
            Refresh();
            app.Notice("收纳完成", $"已收纳 {result.Succeeded} 个项目。");
            if (result.Errors.Count > 0)
                MessageBox.Show(Window.GetWindow(this), string.Join("\n", result.Errors),
                    "部分文件未能收纳", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { busy = false; app.ActiveOperations--; }
    }

    public async void RestoreSelected()
    {
        if (busy || controller == null) return;
        var app = controller;
        var selected = Files.SelectedItems.Cast<FileTile>().ToArray();
        if (selected.Length == 0) { app.Notice("移回桌面", "请先选择要移回桌面的文件。"); return; }
        busy = true;
        app.ActiveOperations++;
        var errors = new List<string>();
        try
        {
            foreach (var tile in selected)
            {
                try { await Task.Run(() => app.Storage.RestoreToDesktop(tile.Entry.Path)); }
                catch (Exception error) { errors.Add(tile.Entry.Name + "：" + error.Message); }
            }
            Refresh();
            if (errors.Count > 0)
                MessageBox.Show(Window.GetWindow(this), string.Join("\n", errors), "移回桌面未完成");
        }
        finally { busy = false; app.ActiveOperations--; }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var accepted = !busy && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        DropOverlay.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await ImportAsync(paths);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Files, e.OriginalSource as DependencyObject) is ListBoxItem)
            OpenSelected();
    }

    private void OpenSelected()
    {
        if (Files.SelectedItem is not FileTile tile) return;
        try { ShellService.Open(tile.Entry.Path); }
        catch (Exception error) { MessageBox.Show(Window.GetWindow(this), error.Message, "无法打开文件"); }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Files, e.OriginalSource as DependencyObject) is not ListBoxItem item)
            return;
        // 本版系统菜单针对右击的单个文件，避免把多选操作错误地应用到不同目录。
        Files.SelectedItem = item.Content;
        e.Handled = true;
        ShowNativeMenu(PointToScreen(e.GetPosition(this)));
    }

    private void ShowNativeMenu(Point point)
    {
        if (Files.SelectedItem is not FileTile tile) return;
        try { ShellService.ShowContextMenu(Window.GetWindow(this), tile.Entry.Path, point); }
        catch (Exception error)
        {
            MessageBox.Show(Window.GetWindow(this), error.Message, "系统菜单暂不可用");
        }
        finally { QueueRefresh(); }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        else if (e.Key == Key.F5) { Refresh(); e.Handled = true; }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            ShowNativeMenu(PointToScreen(new Point(24, 24)));
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var paths = new System.Collections.Specialized.StringCollection();
            paths.AddRange(Files.SelectedItems.Cast<FileTile>().Select(tile => tile.Entry.Path).ToArray());
            if (paths.Count > 0) Clipboard.SetFileDropList(paths);
            e.Handled = true;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(Files);
        dragCandidate = ItemsControl.ContainerFromElement(Files, e.OriginalSource as DependencyObject) is ListBoxItem;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragCandidate || e.LeftButton != MouseButtonState.Pressed || busy) return;
        var position = e.GetPosition(Files);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragCandidate = false;
        var paths = Files.SelectedItems.Cast<FileTile>().Select(tile => tile.Entry.Path).ToArray();
        if (paths.Length == 0) return;
        // 使用真实文件路径进行 OLE 拖出；资源管理器和其他分区都能接收。
        DragDrop.DoDragDrop(
            Files,
            new DataObject(DataFormats.FileDrop, paths),
            DragDropEffects.Move | DragDropEffects.Copy);
        QueueRefresh();
    }

    public void Dispose()
    {
        disposed = true;
        generation++;
        debounce.Stop();
        watcher?.Dispose();
    }
}

public sealed class FileTile : INotifyPropertyChanged
{
    private ImageSource? icon;
    public FileEntry Entry { get; }
    public ImageSource? Icon
    {
        get => icon;
        set { icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }
    public FileTile(FileEntry entry) => Entry = entry;
    public event PropertyChangedEventHandler? PropertyChanged;
}
