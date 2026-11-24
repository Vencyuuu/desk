using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskNest.Models;

namespace DeskNest.Services;

public sealed class StorageService
{
    private readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private readonly string desktopDirectory;
    public string Root { get; }
    public string FilesRoot => Path.Combine(Root, "Files");
    public string StatePath => Path.Combine(Root, "layout.json");
    public string? RecoveryMessage { get; private set; }
    public string DesktopDirectory => desktopDirectory;

    public StorageService(string? root = null, string? desktop = null)
    {
        desktopDirectory = desktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest"));
        Directory.CreateDirectory(FilesRoot);
    }

    public AppState Load()
    {
        if (!File.Exists(StatePath))
            return new AppState();
        try
        {
            return ReadState(StatePath);
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
        {
            // 损坏的主配置保留供恢复；仅从已验证的备份读取，绝不覆盖为一份空布局。
            if (File.Exists(StatePath + ".bak"))
            {
                var backup = ReadState(StatePath + ".bak");
                File.Copy(StatePath, StatePath + ".damaged-" + Guid.NewGuid().ToString("N"));
                File.Copy(StatePath + ".bak", StatePath, true);
                RecoveryMessage = "布局文件异常，已从备份恢复。托管文件未受影响。";
                return backup;
            }
            throw new InvalidDataException("布局文件无法读取。请保留数据目录并检查 layout.json。", error);
        }
    }

    private AppState ReadState(string path)
    {
        var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), json)
            ?? throw new InvalidDataException("布局为空。");
        if (state.Version != 1 || state.Partitions == null || state.DockItems == null ||
            state.DockItems.Any(item => item == null || string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.Name) || !Guid.TryParseExact(item.Id, "N", out _)) ||
            !AppearancePreferences.IsValid(state.Theme, state.DockHeight, state.CardOpacity, state.DockBottomOffset) ||
            state.Typography == null || !state.Typography.IsValid() ||
            state.Partitions.Any(p => p == null || !Guid.TryParseExact(p.Id, "N", out _) ||
                !TypographyPreferences.ValidColor(p.BackgroundColor) || !TypographyPreferences.ValidColor(p.TitleColor) ||
                string.IsNullOrWhiteSpace(p.Name) || p.Accent == null ||
                !Regex.IsMatch(p.Accent, "^#[0-9a-fA-F]{6}$") || !double.IsFinite(p.Left) ||
                !double.IsFinite(p.Top) || !double.IsFinite(p.Width) || !double.IsFinite(p.Height)) ||
            state.Partitions.Select(p => p.Id).Distinct().Count() != state.Partitions.Count)
            throw new InvalidDataException("布局格式无效或版本不受支持。");
        DataPaths.RebaseDock(state, state.DataRootHint, Root);
        return state;
    }

    public void Save(AppState state)
    {
        // 同卷临时文件 + 原子替换，避免掉电或中途退出写坏布局，并保留上一版备份。
        var temporary = StatePath + ".tmp";
        state.DataRootHint = Root;
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, json));
        if (File.Exists(StatePath))
            File.Replace(temporary, StatePath, StatePath + ".bak");
        else
            File.Move(temporary, StatePath);
    }

    public string GetFolder(Partition partition)
    {
        if (!Guid.TryParseExact(partition.Id, "N", out _))
            throw new InvalidDataException("分区标识无效。");
        var folder = Path.Combine(FilesRoot, partition.Id);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public IReadOnlyList<FileEntry> GetFiles(Partition partition) =>
        Directory.EnumerateFileSystemEntries(GetFolder(partition))
            .Select(path => new FileEntry(path, Path.GetFileName(path), Directory.Exists(path)))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public ImportResult Import(Partition partition, IEnumerable<string> paths)
    {
        var errors = new List<string>();
        var succeeded = 0;
        var destination = GetFolder(partition);
        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var source = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar);
                if (!File.Exists(source) && !Directory.Exists(source))
                    throw new IOException("文件已不存在。");
                if (string.Equals(Path.GetDirectoryName(source), destination, StringComparison.OrdinalIgnoreCase))
                    continue;
                // 不允许把收纳根目录或其祖先搬进自身，避免递归移动和数据目录失效。
                if (IsInside(Root, source) || string.Equals(source, Root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("不能托管收纳数据目录或它的上级目录。");
                var target = UniquePath(destination, Path.GetFileName(source));
                Move(source, target);
                succeeded++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(raw)}：{error.Message}");
            }
        }
        return new ImportResult(succeeded, errors);
    }

    public string RestoreToDesktop(string path)
    {
        if (!IsInside(path, FilesRoot))
            throw new IOException("只能移出当前托管目录中的文件。");
        Directory.CreateDirectory(desktopDirectory);
        var target = UniquePath(desktopDirectory, Path.GetFileName(path));
        Move(path, target);
        return target;
    }

    public static bool IsInside(string path, string parent) =>
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static string UniquePath(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; File.Exists(candidate) || Directory.Exists(candidate); index++)
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
        return candidate;
    }

    private static void Move(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            // File.Move 支持跨卷；不覆盖同名文件。失败由调用方逐项报告。
            File.Move(source, target, false);
            return;
        }
        // 目录跨卷移动无法保证原子性，首版明确拒绝，避免部分复制后误删源目录。
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("文件夹跨磁盘托管暂不支持。请先移动到收纳目录所在磁盘后重试。");
        Directory.Move(source, target);
    }
}
