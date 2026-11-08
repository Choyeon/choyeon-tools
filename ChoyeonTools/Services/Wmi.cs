using System.Management;

namespace ChoyeonTools.Services;

/// <summary>一条 WMI 实例的属性快照，取值时不再依赖 COM 对象生命周期。</summary>
internal sealed class WmiObject
{
    private readonly Dictionary<string, object?> _properties;

    internal WmiObject(Dictionary<string, object?> properties) => _properties = properties;

    public object? this[string name] => _properties.TryGetValue(name, out var value) ? value : null;

    public string Str(string name) => this[name]?.ToString()?.Trim() ?? string.Empty;

    public string? StrOrNull(string name)
    {
        var value = this[name]?.ToString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public long? Num(string name) => this[name] is null ? null : Convert.ToInt64(this[name]);

    public ulong? ULong(string name)
    {
        var raw = this[name];
        if (raw is null)
        {
            return null;
        }

        // 部分类（如 Win32_PhysicalMemory.Capacity）以字符串形式返回_uint64。
        return ulong.TryParse(raw.ToString(), out var parsed) ? parsed : Convert.ToUInt64(raw);
    }

    public int? Int(string name) => this[name] is null ? null : Convert.ToInt32(this[name]);

    /// <summary>WMI 布尔量在不同类里可能是 Boolean、Uint8 或 SInt16。</summary>
    public bool? Bool(string name) => this[name] switch
    {
        null => null,
        bool value => value,
        _ => Convert.ToInt64(this[name]) != 0,
    };

    public string[] StrArray(string name) => this[name] as string[] ?? Array.Empty<string>();

    /// <summary>EDID 之类的字节数组在 System.Management 里可能以 UInt16[] 返回，统一收成字节。</summary>
    public byte[] ByteArray(string name) => this[name] switch
    {
        byte[] bytes => bytes,
        ushort[] words => [.. words.Select(value => (byte)value)],
        _ => Array.Empty<byte>(),
    };

    /// <summary>数值数组在不同类里可能是 UInt16[]、Int32[] 或 UInt8[]（如 Win32_SystemEnclosure.ChassisTypes）。</summary>
    public int[] IntArray(string name) => this[name] switch
    {
        int[] values => values,
        ushort[] values => [.. values.Select(value => (int)value)],
        byte[] values => [.. values.Select(value => (int)value)],
        _ => Array.Empty<int>(),
    };

    public DateTime? Time(string name)
    {
        if (this[name]?.ToString() is not { Length: > 8 } raw)
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(raw);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>WMI 查询入口。个别命名空间在部分 Windows SKU 上不存在，查询失败时按空结果处理。</summary>
internal static class Wmi
{
    public static class Scopes
    {
        public const string Cimv2 = @"root\cimv2";
        public const string Wmi = @"root\wmi";
        public const string Storage = @"root\microsoft\windows\storage";
    }

    public static List<WmiObject> Query(string wql, string scope = Scopes.Cimv2)
    {
        var result = new List<WmiObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
            using var collection = searcher.Get();
            foreach (ManagementBaseObject instance in collection)
            {
                result.Add(ReadProperties(instance));
            }
        }
        catch (Exception ex) when (ex is ManagementException or PlatformNotSupportedException or InvalidOperationException)
        {
            // 采集失败不影响其它分类，页面上以空卡片/占位说明呈现。
        }

        return result;
    }

    private static WmiObject ReadProperties(ManagementBaseObject instance)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyData property in instance.Properties)
        {
            properties[property.Name] = property.Value;
        }

        return new WmiObject(properties);
    }
}

internal static class WmiQueryExtensions
{
    public static IEnumerable<(int Index, WmiObject Item)> WithIndex(this IEnumerable<WmiObject> source) =>
        source.Select((item, index) => (index, item));

    public static Dictionary<string, WmiObject> ToDictionaryByDeviceId(this IEnumerable<WmiObject> source)
    {
        var map = new Dictionary<string, WmiObject>(StringComparer.OrdinalIgnoreCase);
        foreach (WmiObject item in source)
        {
            string deviceId = item.Str("DeviceID");
            if (deviceId.Length > 0)
            {
                map[deviceId] = item;
            }
        }

        return map;
    }
}
