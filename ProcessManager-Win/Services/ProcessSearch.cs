using System.Text.RegularExpressions;

namespace ProcessManager.Win.Services;

public static class ProcessSearch
{
    // 引号内保留空格；多个关键词可以分别命中名称、PID、路径或命令行。
    public static string[] ParseTerms(string query) =>
        Regex.Matches(Environment.ExpandEnvironmentVariables(query), "\"([^\"]+)\"|(\\S+)")
            .Select(match => (match.Groups[1].Success ? match.Groups[1].Value : match.Value)
                .Replace('/', '\\'))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();

    public static bool Matches(ProcessSnapshot process, string[] terms) =>
        terms.All(term => process.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase));

    // 排序权重：名称最低、命令行最高；分数越小越靠前。
    // 关键属性（名称、PID、路径）命中的条目因此排在仅命令行命中的条目前面。
    public static int Rank(ProcessSnapshot process, string[] terms)
    {
        var worst = 0;
        var total = 0;
        foreach (var term in terms)
        {
            var field = FieldIndex(process, term);
            worst = Math.Max(worst, field);
            total += field;
        }
        return worst * FieldCount + total;
    }

    private const int FieldCount = 4;

    private static int FieldIndex(ProcessSnapshot process, string term)
    {
        // 名称匹配时允许省略或带上 .exe，与 SearchText 的匹配规则保持一致。
        if (Hit(process.Name, term) ||
            (!process.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Hit(process.Name + ".exe", term)))
            return 0;
        if (Hit(process.Id.ToString(), term)) return 1;
        if (Hit(process.FilePath, term)) return 2;
        return 3;
    }

    private static bool Hit(string? text, string term) =>
        text is not null && text.Replace('/', '\\').Contains(term, StringComparison.OrdinalIgnoreCase);

    public static List<(int Start, int Length)> FindMatches(string text, string[] terms)
    {
        var normalized = text.Replace('/', '\\');
        var matches = new List<(int Start, int Length)>();
        foreach (var term in terms)
        {
            if (term.Length == 0) continue;
            var offset = 0;
            while (offset < normalized.Length)
            {
                var start = normalized.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                matches.Add((start, term.Length));
                offset = start + 1;
            }
        }

        // 合并重叠或相邻的命中，避免多个关键词重复覆盖同一段文字。
        matches.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(int Start, int Length)>();
        foreach (var match in matches)
        {
            if (merged.Count > 0 && match.Start <= merged[^1].Start + merged[^1].Length)
            {
                var previous = merged[^1];
                merged[^1] = (previous.Start,
                    Math.Max(previous.Start + previous.Length, match.Start + match.Length) - previous.Start);
            }
            else merged.Add(match);
        }
        return merged;
    }
}
