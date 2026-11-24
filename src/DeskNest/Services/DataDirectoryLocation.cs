using System;
using System.IO;
using System.Text.Json;

namespace DeskNest.Services;

public sealed class DataDirectoryLocation
{
    public static string DefaultProfile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest");
    public string ProfileRoot { get; }
    public string LocationFile => Path.Combine(ProfileRoot, "location.json");

    public DataDirectoryLocation(string? profileRoot = null) => ProfileRoot = DataPaths.Normalize(profileRoot ?? DefaultProfile);

    public string ResolveRoot()
    {
        if (!File.Exists(LocationFile)) return ProfileRoot;
        var record = JsonSerializer.Deserialize<LocationRecord>(File.ReadAllText(LocationFile));
        if (record == null || !Path.IsPathFullyQualified(record.ActiveRoot))
            throw new InvalidDataException("数据目录定位文件无效。请重新关联已有目录。");
        var root = DataPaths.Normalize(record.ActiveRoot);
        // 已选目录不可用时不能悄悄创建空布局，让用户误以为文件丢失。
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "layout.json")))
            throw new DirectoryNotFoundException("已配置的数据目录不可用：" + root);
        return root;
    }

    public void SetRoot(string activeRoot)
    {
        Directory.CreateDirectory(ProfileRoot);
        var temporary = LocationFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new LocationRecord(DataPaths.Normalize(activeRoot))));
        if (File.Exists(LocationFile)) File.Replace(temporary, LocationFile, LocationFile + ".bak");
        else File.Move(temporary, LocationFile);
    }

    private sealed record LocationRecord(string ActiveRoot);
}
