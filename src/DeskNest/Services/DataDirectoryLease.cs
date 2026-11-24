using System;
using System.IO;

namespace DeskNest.Services;

public sealed class DataDirectoryLease : IDisposable
{
    public const string FileName = ".desknest.lock";
    private readonly FileStream stream;
    private DataDirectoryLease(FileStream stream) => this.stream = stream;

    public static DataDirectoryLease Acquire(string root)
    {
        Directory.CreateDirectory(root);
        try
        {
            // 不同入口目录也不能同时写入同一份活动数据，锁只随进程存活，不依赖删除标记文件。
            return new DataDirectoryLease(new FileStream(Path.Combine(root, FileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException error) { throw new IOException("数据目录正被另一个 DeskNest 实例使用。", error); }
    }

    public void Dispose() => stream.Dispose();
}
