using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DeskNest.Interop;
using Microsoft.Win32;

namespace DeskNest.Views;

public sealed class GlassSurface : FrameworkElement
{
    private static BitmapSource? wallpaper;
    private static string? wallpaperPath;
    public bool GlassEnabled { get; set; } = true;

    public static void RefreshWallpaper()
    {
        var path = new StringBuilder(32768);
        SystemParametersInfo(0x73, (uint)path.Capacity, path, 0);
        var file = path.ToString();
        if (file == wallpaperPath && wallpaper != null) return;
        wallpaperPath = file;
        wallpaper = null;
        if (!File.Exists(file)) return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file);
            bitmap.DecodePixelWidth = 1920;
            bitmap.EndInit();
            bitmap.Freeze();
            // 壁纸只在变化时模糊一次，拖动卡片时仅改变采样位置，不反复处理整张大图。
            var image = new Image
            {
                Source = bitmap,
                Width = bitmap.PixelWidth,
                Height = bitmap.PixelHeight,
                Effect = new BlurEffect { Radius = 28, RenderingBias = RenderingBias.Performance },
            };
            image.Measure(new Size(image.Width, image.Height));
            image.Arrange(new Rect(0, 0, image.Width, image.Height));
            var blurred = new RenderTargetBitmap(
                bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            blurred.Render(image);
            blurred.Freeze();
            wallpaper = blurred;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or
            FormatException or UnauthorizedAccessException)
        {
            wallpaper = null;
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        var dark = ((SolidColorBrush)FindResource("LabelBrush")).Color.R > 128;
        drawing.PushClip(new RectangleGeometry(bounds, 12, 12));
        drawing.DrawRectangle((Brush)FindResource("ContentBrush"), null, bounds);
        if (GlassEnabled && wallpaper != null)
        {
            var window = Window.GetWindow(this);
            var native = window != null ? DesktopLayer.Bounds(window) : default;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(native.Left, native.Top));
            var monitor = screen.Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            using var settings = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var style = settings?.GetValue("WallpaperStyle") as string;
            var scaleX = (double)monitor.Width / wallpaper.PixelWidth;
            var scaleY = (double)monitor.Height / wallpaper.PixelHeight;
            var scale = style == "6" ? Math.Min(scaleX, scaleY) : Math.Max(scaleX, scaleY);
            var width = style == "2" ? monitor.Width : wallpaper.PixelWidth * scale;
            var height = style == "2" ? monitor.Height : wallpaper.PixelHeight * scale;
            if (style == "0") { width = wallpaper.PixelWidth; height = wallpaper.PixelHeight; }
            var x = (monitor.Left + (monitor.Width - width) / 2 - native.Left) / dpi.DpiScaleX;
            var y = (monitor.Top + (monitor.Height - height) / 2 - native.Top) / dpi.DpiScaleY;
            drawing.DrawImage(wallpaper, new Rect(x, y, width / dpi.DpiScaleX, height / dpi.DpiScaleY));
            drawing.DrawRectangle(new SolidColorBrush(dark
                ? Color.FromArgb(174, 30, 32, 38) : Color.FromArgb(165, 248, 250, 255)), null, bounds);
            // 透明着色、边缘亮线和柔和的方向高光共同近似液态玻璃，而非声称苹果原生材质。
            var sheen = new LinearGradientBrush(
                Color.FromArgb(dark ? (byte)28 : (byte)115, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255), 70);
            drawing.DrawRectangle(sheen, null, bounds);
        }
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 1);
        drawing.DrawRoundedRectangle(null, outline,
            new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 12, 12);
        drawing.Pop();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint size, StringBuilder result, uint flags);
}
