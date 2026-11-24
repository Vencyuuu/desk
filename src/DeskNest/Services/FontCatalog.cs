using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Markup;
using System.Windows.Media;

namespace DeskNest.Services;

public sealed record FontChoice(string Family, string Name)
{
    public FontFamily PreviewFont => FontCatalog.Resolve(Family);
    // 自定义 ComboBox 的选中内容可能使用字符串回退，始终显示友好名称。
    public override string ToString() => Name;
}

public static class FontCatalog
{
    private static readonly FontFamily fallback = new("Segoe UI, Microsoft YaHei");
    private static readonly IReadOnlyDictionary<string, FontFamily> installed = Fonts.SystemFontFamilies
        .GroupBy(font => font.Source, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    // 只从系统已安装字体中解析，绝不把配置字符串当作外部字体文件或网络地址加载。
    public static FontFamily Resolve(string name) => installed.TryGetValue(name, out var font) ? font : fallback;

    public static IReadOnlyList<FontChoice> Choices(params string[] configured)
    {
        var language = XmlLanguage.GetLanguage("zh-CN");
        var choices = installed.Select(pair => new FontChoice(pair.Key,
                pair.Value.FamilyNames.TryGetValue(language, out var localized) ? localized : pair.Key))
            .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        choices.Insert(0, new FontChoice("", "默认字体"));
        // 数据迁移到未安装该字体的系统时保留选择，实际显示安全回退，不丢失原配置。
        foreach (var name in configured.Distinct(StringComparer.OrdinalIgnoreCase))
            if (name.Length > 0 && !installed.ContainsKey(name))
                choices.Add(new FontChoice(name, name + "（未安装，使用默认）"));
        return choices;
    }
}
