using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoyeonTools.Services;

/// <summary>
/// 单表结构的 ID 名称库：显示器 PNP 代码（pnp.json）与内存 JEDEC 厂商（jep106.json），
/// 由 tools/build-pnp-catalog.ps1、tools/build-jep106-catalog.ps1 生成，许可声明随数据分发。
/// </summary>
internal sealed class NameTable
{
    private static readonly Lazy<NameTable> PnpShared = new(() => Load("pnp.json"));
    private static readonly Lazy<NameTable> JedecShared = new(() => Load("jep106.json"));

    private readonly Dictionary<string, string> _entries;

    private NameTable(string source, string attribution, string version, Dictionary<string, string> entries)
    {
        Source = source;
        Attribution = attribution;
        Version = version;
        _entries = entries;
    }

    /// <summary>显示器 EDID 的三字符 PNP 厂商代码表，源自 UEFI 注册表。</summary>
    public static NameTable Pnp => PnpShared.Value;

    /// <summary>内存厂商表，键为 "银行:条目"（JEP106），解码约定与 nwinfo 一致。</summary>
    public static NameTable Jedec => JedecShared.Value;

    public string Source { get; }

    public string Version { get; }

    public string Attribution { get; }

    public bool IsLoaded => _entries.Count > 0;

    public int Count => _entries.Count;

    public string? Find(string key) => _entries.GetValueOrDefault(key);

    private static NameTable Load(string fileName)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Hardware", fileName);
            if (!File.Exists(path))
            {
                return Empty();
            }

            Payload? payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
            return payload is null
                ? Empty()
                : new NameTable(
                    payload.Source ?? string.Empty,
                    payload.Attribution ?? string.Empty,
                    payload.Version ?? string.Empty,
                    new Dictionary<string, string>(payload.Entries ?? new(), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 名称库只是补充说明，读不到不影响采集。
            return Empty();
        }
    }

    private static NameTable Empty() => new(string.Empty, string.Empty, string.Empty, new());

    private sealed class Payload
    {
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("attribution")]
        public string? Attribution { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("entries")]
        public Dictionary<string, string>? Entries { get; set; }
    }
}