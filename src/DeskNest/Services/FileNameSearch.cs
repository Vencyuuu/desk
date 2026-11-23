using System;
using System.Linq;
using System.Text;

namespace DeskNest.Services;

public static class FileNameSearch
{
    public static bool Matches(string name, string query)
    {
        // 统一全角 / 半角和大小写；多个空白分隔的关键词必须全部匹配。
        var candidate = name.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var tokens = query.Normalize(NormalizationForm.FormKC).ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.All(token => IsSubsequence(candidate, token));
    }

    private static bool IsSubsequence(string name, string token)
    {
        // 允许字符之间有间隔，例如“工报”匹配“工作报告”，但保持字符原顺序。
        var offset = 0;
        foreach (var character in token)
        {
            var index = name.IndexOf(character, offset);
            if (index < 0) return false;
            offset = index + 1;
        }
        return true;
    }
}
