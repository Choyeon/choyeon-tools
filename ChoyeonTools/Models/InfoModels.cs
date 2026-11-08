using ChoyeonTools.Services;

namespace ChoyeonTools.Models;

/// <summary>一条 "标签: 值" 信息，等价于原 Python 版里的一行文本。</summary>
public sealed record InfoField(string Label, [property: System.Text.Json.Serialization.JsonIgnore] string? RawValue)
{
    private static readonly string[] Placeholders = ["unknown", "none", "n/a", "not available", "to be filled by o.e.m"];

    /// <summary>采集不到或固件留下占位值的字段统一显示为"未知"，界面上不出现空行。</summary>
    public string Value
    {
        get
        {
            string text = RawValue?.Trim() ?? string.Empty;
            if (text.Length == 0 || Array.IndexOf(Placeholders, text.TrimEnd('.', '_').ToLowerInvariant()) >= 0)
            {
                return Formatting.Unknown;
            }

            return text;
        }
    }

    /// <summary>
    /// 这一项到底有没有读到。界面隐藏这类空行，导出与复制仍然保留，
    /// 这样"没取到"是可以核对的，不会误以为硬件没有这个参数。
    /// </summary>
    public bool IsMissing => Value == Formatting.Unknown
        || Value.StartsWith(SensorProbe.UnavailablePrefix, StringComparison.Ordinal);
}

/// <summary>一组相关字段，对应界面上的一张卡片（例如某一条内存、某一块硬盘）。</summary>
public sealed record InfoGroup(string Title, IReadOnlyList<InfoField> Fields);

/// <summary>一个分类页，对应原程序的 Notebook 标签。</summary>
public sealed record InfoCategory(
    string Key,
    string Title,
    string Description,
    IReadOnlyList<InfoGroup> Groups);

/// <summary>一次完整采集的结果。</summary>
public sealed record SystemSnapshot(
    DateTimeOffset CollectedAt,
    IReadOnlyList<InfoCategory> Categories)
{
    public InfoCategory? Find(string key) => Categories.FirstOrDefault(c => c.Key == key);
}
