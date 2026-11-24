using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DeskNest.Services;

public sealed record DataMoveProgress(string Stage, int Completed, int Total);

public sealed class PreparedDataDirectory : IDisposable
{
    private readonly List<FileStream> sourceLocks;
    public StorageService Store { get; }
    private DataDirectoryLease? lease;
    private readonly Action? verifySource;

    public PreparedDataDirectory(StorageService store, DataDirectoryLease lease,
        List<FileStream>? sourceLocks = null, Action? verifySource = null)
    {
        Store = store;
        this.lease = lease;
        this.sourceLocks = sourceLocks ?? new();
        this.verifySource = verifySource;
    }

    public void VerifySource() => verifySource?.Invoke();

    public DataDirectoryLease TakeLease()
    {
        var result = lease ?? throw new InvalidOperationException("数据目录锁已转移。");
        lease = null;
        return result;
    }

    public void Dispose()
    {
        foreach (var file in sourceLocks) file.Dispose();
        lease?.Dispose();
    }
}

public static class DataMigrationService
{
    private sealed record Snapshot(string Relative, long Length, DateTime Modified);

    public static Task<PreparedDataDirectory> CopyAsync(StorageService source, string destination,
        IProgress<DataMoveProgress>? progress = null, CancellationToken cancellation = default) =>
        Task.Run(() => Copy(source, destination, progress, cancellation), cancellation);

    private static PreparedDataDirectory Copy(StorageService source, string destination,
        IProgress<DataMoveProgress>? progress, CancellationToken cancellation)
    {
        var sourceRoot = DataPaths.Canonical(source.Root);
        var target = DataPaths.Canonical(destination);
        if (DataPaths.Same(sourceRoot, target) || StorageService.IsInside(target, sourceRoot) ||
            StorageService.IsInside(sourceRoot, target))
            throw new IOException("新旧数据目录不能相同，也不能互为上级或下级目录。");
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new IOException("迁移目标必须是空目录；已有数据请使用“关联已有目录”。");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("不能迁移到磁盘根目录。");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".desknest-stage-" + Guid.NewGuid().ToString("N"));
        var locks = new List<FileStream>();
        var published = false;
        try
        {
            progress?.Report(new DataMoveProgress("检查文件", 0, 0));
            var (directories, files) = Scan(sourceRoot);
            var required = files.Sum(file => file.Length);
            var drive = new DriveInfo(Path.GetPathRoot(target)!);
            if (drive.IsReady && required > drive.AvailableFreeSpace) throw new IOException("目标磁盘空间不足。");
            Directory.CreateDirectory(stage);
            foreach (var directory in directories) Directory.CreateDirectory(Path.Combine(stage, directory));
            // 迁移期间持有只读且不共享写入 / 删除的句柄。正在编辑的文件会明确失败，不复制不一致内容。
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                locks.Add(new FileStream(Path.Combine(sourceRoot, file.Relative), FileMode.Open,
                    FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan));
            }
            var buffer = new byte[1024 * 1024];
            for (var index = 0; index < files.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var file = files[index];
                var targetFile = Path.Combine(stage, file.Relative);
                byte[] digest;
                using (var output = new FileStream(targetFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    int read;
                    while ((read = locks[index].Read(buffer)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                    }
                    output.Flush(true);
                    digest = hash.GetHashAndReset();
                }
                File.SetLastWriteTimeUtc(targetFile, file.Modified);
                using var verify = File.OpenRead(targetFile);
                if (!SHA256.HashData(verify).SequenceEqual(digest))
                    throw new IOException("文件校验失败：" + file.Relative);
                progress?.Report(new DataMoveProgress("复制并校验文件", index + 1, files.Count));
            }
            // 拦截迁移期间由其他程序新增目录 / 文件的情况；源目录始终保留。
            void VerifySource()
            {
                var after = Scan(sourceRoot);
                if (!files.SequenceEqual(after.Files) || !directories.SequenceEqual(after.Directories))
                    throw new IOException("源目录在迁移期间发生变化，请关闭相关程序后重试。");
            }
            VerifySource();
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(new DataMoveProgress("准备切换目录", files.Count, files.Count));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: false);
            Directory.Move(stage, target);
            published = true;
            var lease = DataDirectoryLease.Acquire(target);
            return new PreparedDataDirectory(new StorageService(target, source.DesktopDirectory), lease, locks, VerifySource);
        }
        catch
        {
            foreach (var file in locks) file.Dispose();
            // 只清理本次创建、位于已验证父目录下的唯一暂存目录，从不递归删除源目录或正式目标。
            if (!published && DataPaths.Same(Path.GetDirectoryName(stage)!, parent) &&
                Path.GetFileName(stage).StartsWith(".desknest-stage-", StringComparison.Ordinal) && Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static (List<string> Directories, List<Snapshot> Files) Scan(string root)
    {
        var directories = new List<string>();
        var files = new List<Snapshot>();
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path);
                if (directory == root && (Path.GetFileName(path) is "location.json" or "location.json.bak" or
                    "location.json.tmp" or "Requests" or DataDirectoryLease.FileName)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("暂不复制符号链接或目录联接，请先处理该项：" + relative);
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(relative); Visit(path); }
                else
                {
                    var info = new FileInfo(path);
                    files.Add(new Snapshot(relative, info.Length, info.LastWriteTimeUtc));
                }
            }
        }
        Visit(root);
        return (directories, files);
    }
}
