using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoyeonTools.Services;

/// <summary>
/// 硬件 ID 到名称的对照表。数据由 tools 下的脚本分别从 PCI ID Repository 与 USB ID Database 生成，
/// 随应用打包在 Assets/Hardware/pci.json 与 usb.json。查不到时返回 null，调用方回退到 WMI 原始文本。
/// PCI 与 USB 的实例 ID 是两套写法（VEN_/DEV_ 与 VID_/PID_），所以按总线各加载一份。
/// </summary>
internal sealed class HardwareCatalog
{
    private static readonly Lazy<HardwareCatalog> PciShared = new(() => Load("pci.json", @"PCI\", "VEN_", "DEV_"));
    private static readonly Lazy<HardwareCatalog> UsbShared = new(() => Load("usb.json", @"USB\", "VID_", "PID_"));

    private readonly Dictionary<string, string> _vendors;
    private readonly Dictionary<string, string> _devices;
    private readonly Dictionary<string, string> _subsystems;
    private readonly string _busPrefix;
    private readonly string _vendorToken;
    private readonly string _deviceToken;

    private HardwareCatalog(
        string source,
        string attribution,
        string version,
        Dictionary<string, string> vendors,
        Dictionary<string, string> devices,
        Dictionary<string, string> subsystems,
        string busPrefix,
        string vendorToken,
        string deviceToken)
    {
        Source = source;
        Attribution = attribution;
        Version = version;
        _vendors = vendors;
        _devices = devices;
        _subsystems = subsystems;
        _busPrefix = busPrefix;
        _vendorToken = vendorToken;
        _deviceToken = deviceToken;
    }

    /// <summary>PCI 名称库：显卡、网卡、芯片组等按 PCI\VEN_ 加 DEV_ 枚举的设备。</summary>
    public static HardwareCatalog Current => PciShared.Value;

    /// <summary>USB 名称库：键鼠、耳机、集线器、U 盘等按 USB\VID_ 加 PID_ 枚举的设备。</summary>
    public static HardwareCatalog Usb => UsbShared.Value;

    public string Source { get; }

    public string Version { get; }

    /// <summary>名称库的版权与许可声明，随数据一起分发，界面原样展示。</summary>
    public string Attribution { get; }

    public bool IsLoaded => _vendors.Count > 0;

    /// <summary>名称库收录的设备条目数，关于页用来标注数据规模。</summary>
    public int DeviceCount => _devices.Count;

    /// <summary>子系统条目数（PCI 板卡厂商与型号），关于页用来标注数据规模。</summary>
    public int SubsystemCount => _subsystems.Count;

    /// <summary>从设备实例 ID 取出小写的厂商号与设备号；前缀与字段名按总线区分，避免把别的总线硬凑成键名。</summary>
    public bool TryGetIds(string? deviceInstanceId, out string vendorId, out string deviceId)
    {
        vendorId = string.Empty;
        deviceId = string.Empty;
        if (deviceInstanceId is null || !deviceInstanceId.StartsWith(_busPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int vendorAt = deviceInstanceId.IndexOf(_vendorToken, StringComparison.OrdinalIgnoreCase);
        int deviceAt = deviceInstanceId.IndexOf(_deviceToken, StringComparison.OrdinalIgnoreCase);
        if (vendorAt < 0 || deviceAt < 0 || vendorAt + 8 > deviceInstanceId.Length || deviceAt + 8 > deviceInstanceId.Length)
        {
            return false;
        }

        vendorId = deviceInstanceId.Substring(vendorAt + 4, 4).ToLowerInvariant();
        deviceId = deviceInstanceId.Substring(deviceAt + 4, 4).ToLowerInvariant();
        return IsHex(vendorId) && IsHex(deviceId);
    }

    public string? FindDeviceName(string? deviceInstanceId)
    {
        if (!TryGetIds(deviceInstanceId, out string vendorId, out string deviceId))
        {
            return null;
        }

        return _devices.GetValueOrDefault(vendorId + ":" + deviceId);
    }

    public string? FindVendorName(string? deviceInstanceId)
    {
        if (!TryGetIds(deviceInstanceId, out string vendorId, out _))
        {
            return null;
        }

        return _vendors.GetValueOrDefault(vendorId);
    }

    /// <summary>
    /// 板卡厂商：Windows 实例 ID 的 SUBSYS_ 段是"子系统设备号 + 子系统厂商号"各四位，
    /// 子系统厂商号本身也是 PCI 厂商号（如 10b0 = CardExpert/耕升），直接回厂商表查。
    /// </summary>
    public string? FindSubsystemVendor(string? deviceInstanceId)
    {
        if (!TryGetIds(deviceInstanceId, out _, out _) || deviceInstanceId is null)
        {
            return null;
        }

        int subsystemAt = deviceInstanceId.IndexOf("SUBSYS_", StringComparison.OrdinalIgnoreCase);
        if (subsystemAt < 0 || subsystemAt + 15 > deviceInstanceId.Length)
        {
            return null;
        }

        string subVendor = deviceInstanceId.Substring(subsystemAt + 11, 4).ToLowerInvariant();
        return IsHex(subVendor) ? _vendors.GetValueOrDefault(subVendor) : null;
    }

    /// <summary>一行内的完整描述：设备名（厂商）。</summary>
    public string? Describe(string? deviceInstanceId)
    {
        string? device = FindDeviceName(deviceInstanceId);
        if (device is null)
        {
            return null;
        }

        string? vendor = FindVendorName(deviceInstanceId);
        return vendor is null ? device : $"{device}（{vendor}）";
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return value.Length == 4;
    }

    private static HardwareCatalog Load(string fileName, string busPrefix, string vendorToken, string deviceToken)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Hardware", fileName);
            if (!File.Exists(path))
            {
                return Empty(busPrefix, vendorToken, deviceToken);
            }

            Payload? payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
            return payload is null
                ? Empty(busPrefix, vendorToken, deviceToken)
                : new HardwareCatalog(
                    payload.Source ?? string.Empty,
                    payload.Attribution ?? string.Empty,
                    payload.Version ?? string.Empty,
                    payload.Vendors ?? new Dictionary<string, string>(),
                    payload.Devices ?? new Dictionary<string, string>(),
                    payload.Subsystems ?? new Dictionary<string, string>(),
                    busPrefix,
                    vendorToken,
                    deviceToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 名称库只是补充说明，读不到不影响采集。
            return Empty(busPrefix, vendorToken, deviceToken);
        }
    }

    private static HardwareCatalog Empty(string busPrefix, string vendorToken, string deviceToken) =>
        new(string.Empty, string.Empty, string.Empty, new(), new(), new(), busPrefix, vendorToken, deviceToken);

    /// <summary>生成脚本写的是小写键名，这里显式对齐，避免默认区分大小写的匹配把整个库读成空表。</summary>
    private sealed class Payload
    {
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("attribution")]
        public string? Attribution { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("vendors")]
        public Dictionary<string, string>? Vendors { get; set; }

        [JsonPropertyName("devices")]
        public Dictionary<string, string>? Devices { get; set; }

        [JsonPropertyName("subsystems")]
        public Dictionary<string, string>? Subsystems { get; set; }
    }
}
