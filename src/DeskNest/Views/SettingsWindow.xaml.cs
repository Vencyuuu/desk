using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskNest.Models;
using DeskNest.Services;
using Microsoft.Win32;

namespace DeskNest.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController controller;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private bool ready;
    private bool populating;
    private bool dirty;
    private CancellationTokenSource? migrationCancellation;
    private string? titleColor;

    public SettingsWindow(AppController controller)
    {
        InitializeComponent();
        this.controller = controller;
        var text = controller.State.Typography;
        var fonts = FontCatalog.Choices(text.CardFont, text.DockFont, text.SettingsFont);
        GlobalFontChoice.ItemsSource = CardFontChoice.ItemsSource = DockFontChoice.ItemsSource = SettingsFontChoice.ItemsSource = fonts;
        ready = true;
        RefreshValues();
        controller.Changed += RefreshValues;
        saveTimer.Tick += (_, _) => FlushChanges();
        Activated += (_, _) => RefreshValues();
        Closing += OnClosing;
        Closed += (_, _) => { saveTimer.Stop(); controller.Changed -= RefreshValues; };
    }

    private void RefreshValues()
    {
        if (!ready || dirty) return;
        populating = true;
        try
        {
            var state = controller.State;
            var text = state.Typography;
            if (new[] { text.CardFont, text.DockFont, text.SettingsFont }.Any(font =>
                !CardFontChoice.Items.Cast<FontChoice>().Any(choice => choice.Family == font)))
            {
                var fonts = FontCatalog.Choices(text.CardFont, text.DockFont, text.SettingsFont);
                GlobalFontChoice.ItemsSource = CardFontChoice.ItemsSource = DockFontChoice.ItemsSource = SettingsFontChoice.ItemsSource = fonts;
            }
            FontFamily = FontCatalog.Resolve(text.SettingsFont);
            CardFontChoice.SelectedValue = text.CardFont;
            DockFontChoice.SelectedValue = text.DockFont;
            SettingsFontChoice.SelectedValue = text.SettingsFont;
            GlobalFontChoice.SelectedValue = text.CardFont == text.DockFont && text.CardFont == text.SettingsFont
                ? text.CardFont : null;
            TitleSizeSlider.Value = text.CardTitleSize;
            titleColor = text.CardTitleColor;
            ThemeChoice.SelectedIndex = state.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
            HeightSlider.Value = state.DockHeight;
            BottomOffsetSlider.Value = state.DockBottomOffset;
            TransparencySlider.Value = Math.Round((1 - state.CardOpacity) * 100);
            GlassCheck.IsChecked = state.GlassEnabled;
            DockCheck.IsChecked = state.DockEnabled;
            StartupCheck.IsChecked = controller.Startup.IsEnabled;
            DataDirectoryText.Text = controller.Storage.Root;
            PreferencePanel.IsEnabled = !controller.IsDataChanging;
            DirectoryButtons.IsEnabled = !controller.IsDataChanging;
            UpdateLabels();
        }
        catch (Exception error) { SaveStatus.Text = "读取设置失败：" + error.Message; }
        finally { populating = false; }
    }

    private void UpdateLabels()
    {
        HeightValue.Text = $"{HeightSlider.Value:0}";
        BottomOffsetValue.Text = $"{BottomOffsetSlider.Value:0}";
        TransparencyValue.Text = $"{TransparencySlider.Value:0}%";
        TitleSizeValue.Text = $"{TitleSizeSlider.Value:0}";
        TitleColorValue.Text = titleColor ?? "标题颜色跟随主题";
    }

    private void OnPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || populating) return;
        UpdateLabels();
        SaveStatus.Text = "正在应用更改…";
        dirty = true;
        // 滑块拖动时合并连续输入，避免每个像素变化都写入布局文件。
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void OnGlobalFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || populating || GlobalFontChoice.SelectedValue is not string family) return;
        // 合并三个子控件的修改，一次保存；同步后仍允许分别覆盖某一区域字体。
        populating = true;
        try { CardFontChoice.SelectedValue = DockFontChoice.SelectedValue = SettingsFontChoice.SelectedValue = family; }
        finally { populating = false; }
        OnPreferenceChanged(sender, e);
    }

    public bool FlushChanges()
    {
        if (!dirty) return true;
        saveTimer.Stop();
        try
        {
            var theme = (string)((ComboBoxItem)ThemeChoice.SelectedItem).Tag;
            dirty = false;
            controller.SetPreferences(new AppearancePreferences(theme, HeightSlider.Value,
                Math.Round(1 - TransparencySlider.Value / 100, 2),
                GlassCheck.IsChecked == true, DockCheck.IsChecked == true, BottomOffsetSlider.Value,
                new TypographyPreferences((string?)CardFontChoice.SelectedValue ?? "",
                    (string?)DockFontChoice.SelectedValue ?? "", (string?)SettingsFontChoice.SelectedValue ?? "",
                    TitleSizeSlider.Value, titleColor)));
            SaveStatus.Text = "已保存，下次启动继续使用";
            return true;
        }
        catch (Exception error)
        {
            SaveStatus.Text = "保存失败：" + error.Message;
            // 保留待保存状态供完成按钮或下一次修改重试，不进行无限定时重写。
            dirty = true;
            return false;
        }
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || populating) return;
        try
        {
            controller.SetStartupEnabled(StartupCheck.IsChecked == true);
            SaveStatus.Text = StartupCheck.IsChecked == true ? "已开启登录后自启动" : "已关闭自启动";
        }
        catch (Exception error)
        {
            populating = true;
            try { StartupCheck.IsChecked = controller.Startup.IsEnabled; }
            finally { populating = false; }
            SaveStatus.Text = "自启动未更改：" + error.Message;
        }
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void OnPickTitleColor(object sender, RoutedEventArgs e)
    {
        using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (titleColor != null) picker.Color = System.Drawing.ColorTranslator.FromHtml(titleColor);
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        titleColor = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        OnPreferenceChanged(sender, e);
    }

    private void OnResetTitleColor(object sender, RoutedEventArgs e)
    {
        titleColor = null;
        OnPreferenceChanged(sender, e);
    }

    private void OnMigrateDirectory(object sender, RoutedEventArgs e) => PickDataDirectory(false);
    private void OnAssociateDirectory(object sender, RoutedEventArgs e) => PickDataDirectory(true);
    private void OnCancelMigration(object sender, RoutedEventArgs e) => migrationCancellation?.Cancel();

    private async void PickDataDirectory(bool associate)
    {
        if (controller.IsDataChanging || !FlushChanges()) return;
        var picker = new OpenFolderDialog
        {
            Title = associate ? "选择已有 DeskNest 数据目录" : "选择用于迁移的空目录",
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true) return;
        var explanation = associate
            ? "将使用此目录的卡片、文件和外观配置，当前数据保留。\n\n"
            : "将复制并校验全部托管文件后切换。原目录保留为备份。\n请先关闭正在编辑托管文件的程序。\n\n";
        if (MessageBox.Show(this, explanation + picker.FolderName, "更改数据目录",
            MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        using var cancellation = new CancellationTokenSource();
        migrationCancellation = cancellation;
        DirectoryProgress.Visibility = Visibility.Visible;
        DirectoryProgress.IsIndeterminate = true;
        CancelMigration.Visibility = associate ? Visibility.Collapsed : Visibility.Visible;
        CancelMigration.IsEnabled = true;
        DirectoryStatus.Text = associate ? "正在验证并关联…" : "正在准备迁移…";
        var progress = new Progress<DataMoveProgress>(value =>
        {
            DirectoryProgress.IsIndeterminate = value.Total == 0;
            DirectoryProgress.Maximum = Math.Max(1, value.Total);
            DirectoryProgress.Value = value.Completed;
            DirectoryStatus.Text = $"{value.Stage} · {value.Completed}/{value.Total}";
            if (value.Stage == "准备切换目录") CancelMigration.IsEnabled = false;
        });
        try
        {
            await controller.ChangeDataDirectoryAsync(picker.FolderName, associate, progress, cancellation.Token);
            DirectoryStatus.Text = associate ? "已关联，卡片与快捷入口已重新加载。"
                : "迁移完成，已切换到新目录。旧目录保留为备份。";
        }
        catch (OperationCanceledException) { DirectoryStatus.Text = "已取消，继续使用原数据目录。"; }
        catch (Exception error) { DirectoryStatus.Text = "未切换目录：" + error.Message; }
        finally
        {
            migrationCancellation = null;
            DirectoryProgress.Visibility = Visibility.Collapsed;
            CancelMigration.Visibility = Visibility.Collapsed;
            RefreshValues();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (controller.IsDataChanging) { e.Cancel = true; return; }
        if (!controller.IsQuitting && !FlushChanges()) e.Cancel = true;
    }
}
