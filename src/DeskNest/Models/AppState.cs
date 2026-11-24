using System;
using System.Collections.Generic;

namespace DeskNest.Models;

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public string? DataRootHint { get; set; }
    public string Theme { get; set; } = "system";
    public double DockHeight { get; set; } = 88;
    public double DockBottomOffset { get; set; } = 12;
    public double CardOpacity { get; set; } = 0.65;
    public bool GlassEnabled { get; set; } = true;
    public bool DockEnabled { get; set; } = true;
    public bool DockInitialized { get; set; }
    public bool ShellMenuEnabled { get; set; } = true;
    public TypographyPreferences Typography { get; set; } = new();
    public List<DockItem> DockItems { get; set; } = new();
    public List<Partition> Partitions { get; set; } = new();
}

public sealed class Partition
{
    public string? BackgroundColor { get; set; }
    public string? TitleColor { get; set; }
    // ID 同时作为真实目录名，重命名卡片不会改变文件路径或破坏快捷方式。
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新分区";
    public string Accent { get; set; } = "#007AFF";
    public double Left { get; set; } = 48;
    public double Top { get; set; } = 88;
    public double Width { get; set; } = 328;
    public double Height { get; set; } = 320;
    public bool Collapsed { get; set; }
    public bool Locked { get; set; }
    public bool PhysicalCoordinates { get; set; }
}

public sealed class DockItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "应用";
    public string Path { get; set; } = "";
}

public sealed class DesktopRequest
{
    public string Action { get; set; } = "show";
    public string[] Paths { get; set; } = Array.Empty<string>();
}

public sealed record FileEntry(string Path, string Name, bool IsDirectory)
{
    public string Kind => IsDirectory ? "文件夹" : System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
}

public sealed record ImportResult(int Succeeded, List<string> Errors);

public sealed record AppearancePreferences(
    string Theme, double DockHeight, double CardOpacity, bool GlassEnabled, bool DockEnabled,
    double DockBottomOffset = 12, TypographyPreferences? Typography = null)
{
    public static bool IsValid(string theme, double height, double opacity, double bottomOffset = 12) =>
        theme is "system" or "light" or "dark" &&
        double.IsFinite(height) && height >= 64 && height <= 128 &&
        double.IsFinite(opacity) && opacity >= 0.2 && opacity <= 1 &&
        double.IsFinite(bottomOffset) && bottomOffset >= 0 && bottomOffset <= 300;
}

// 空字体名表示默认字体；标题颜色为空时跟随主题，兼容已有布局及迁移后的缺失字体。
public sealed record TypographyPreferences(
    string CardFont = "", string DockFont = "", string SettingsFont = "",
    double CardTitleSize = 13, string? CardTitleColor = null)
{
    public static bool ValidColor(string? color) => color == null ||
        System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$");
    public bool IsValid() => ValidFont(CardFont) && ValidFont(DockFont) && ValidFont(SettingsFont) &&
        double.IsFinite(CardTitleSize) && CardTitleSize >= 10 && CardTitleSize <= 32 &&
        (CardTitleColor == null || System.Text.RegularExpressions.Regex.IsMatch(CardTitleColor, "^#[0-9a-fA-F]{6}$"));

    private static bool ValidFont(string? value) => value != null && value.Length <= 256;
}
