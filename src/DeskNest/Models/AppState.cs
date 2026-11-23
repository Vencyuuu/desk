using System;
using System.Collections.Generic;

namespace DeskNest.Models;

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public string Theme { get; set; } = "system";
    public bool GlassEnabled { get; set; } = true;
    public bool DockEnabled { get; set; } = true;
    public bool DockInitialized { get; set; }
    public bool ShellMenuEnabled { get; set; } = true;
    public List<DockItem> DockItems { get; set; } = new();
    public List<Partition> Partitions { get; set; } = new();
}

public sealed class Partition
{
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
