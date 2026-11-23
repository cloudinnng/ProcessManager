using System.Text.Json;

namespace ProcessManager.Win.Services;

// 常用搜索标签保存在用户配置目录，重启应用后自动恢复。
public sealed class QuickSearchStore
{
    private const int MaxItems = 30;
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProcessManager", "quick-search.json");

    public List<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path));
            return (items ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(MaxItems)
                .ToList();
        }
        catch (Exception)
        {
            // 配置缺失或损坏时按无记录处理，不影响启动。
            return [];
        }
    }

    public void Save(IReadOnlyList<string> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(items.Take(MaxItems)));
        }
        catch (Exception)
        {
            // 写入失败（如目录只读）时只丢失本次记忆，不打断使用。
        }
    }
}