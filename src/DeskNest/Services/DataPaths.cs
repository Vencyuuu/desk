using System;
using System.IO;
using DeskNest.Models;

namespace DeskNest.Services;

public static class DataPaths
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static string Canonical(string path)
    {
        var absolute = Normalize(path);
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        foreach (var segment in absolute[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("无法解析目录链接：" + current);
        }
        return Normalize(current);
    }

    public static bool Same(string first, string second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    public static string Rebase(string path, string? oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(oldRoot) || !Path.IsPathFullyQualified(oldRoot) ||
            !Path.IsPathFullyQualified(path)) return path;
        if (Same(path, oldRoot)) return Normalize(newRoot);
        return StorageService.IsInside(path, oldRoot)
            ? Path.Combine(newRoot, Path.GetRelativePath(oldRoot, path)) : path;
    }

    public static void RebaseDock(AppState state, string? oldRoot, string newRoot)
    {
        foreach (var item in state.DockItems)
        {
            var candidate = Rebase(item.Path, oldRoot, newRoot);
            if (File.Exists(candidate) || Directory.Exists(candidate)) item.Path = candidate;
        }
    }

    public static AppState ValidateExisting(StorageService store)
    {
        if (!File.Exists(store.StatePath) || !Directory.Exists(store.FilesRoot))
            throw new InvalidDataException("所选目录需要包含 layout.json 和 Files，不能关联空目录。");
        var state = store.Load();
        foreach (var partition in state.Partitions)
            if (!Directory.Exists(Path.Combine(store.FilesRoot, partition.Id)))
                throw new InvalidDataException($"“{partition.Name}”的托管目录缺失，请选择完整的数据目录。");
        return state;
    }
}
