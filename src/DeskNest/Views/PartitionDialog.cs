using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskNest.Views;

public sealed class PartitionDialog : Window
{
    private readonly TextBox name;
    private readonly ComboBox color;
    private static readonly string[] Colors = { "#007AFF", "#32A86D", "#FF9F0A", "#BF5AF2", "#FF375F", "#8E8E93" };
    public string PartitionName => name.Text.Trim();
    public string Accent => Colors[color.SelectedIndex];

    public PartitionDialog(string initialName = "", string initialColor = "#007AFF")
    {
        Style = (Style)FindResource(typeof(Window));
        Title = string.IsNullOrEmpty(initialName) ? "新建分区" : "编辑分区";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "为这一组文件留一个位置。",
            Margin = new Thickness(0, 8, 0, 24),
            Foreground = (Brush)Application.Current.FindResource("SecondaryBrush"),
        });
        panel.Children.Add(new TextBlock { Text = "分区名称", Margin = new Thickness(0, 0, 0, 8) });
        name = new TextBox { Text = initialName, MaxLength = 40, MinHeight = 32 };
        System.Windows.Automation.AutomationProperties.SetName(name, "分区名称");
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "标记颜色", Margin = new Thickness(0, 20, 0, 8) });
        color = new ComboBox { MinHeight = 30 };
        string[] labels = { "蓝色", "绿色", "橙色", "紫色", "粉色", "石墨" };
        for (var index = 0; index < Colors.Length; index++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Margin = new Thickness(0, 0, 8, 0),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors[index])),
            });
            row.Children.Add(new TextBlock { Text = labels[index] });
            color.Items.Add(new ComboBoxItem { Content = row });
        }
        color.SelectedIndex = System.Math.Max(0, System.Array.IndexOf(Colors, initialColor));
        System.Windows.Automation.AutomationProperties.SetName(color, "标记颜色");
        panel.Children.Add(color);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 28, 0, 0),
        };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "完成", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
