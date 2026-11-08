using System.Globalization;
using System.Text;

namespace ChoyeonTools.Services;

/// <summary>把 WMI/Win32 原始值翻译成中文可读文本。</summary>
internal static class Formatting
{
    public const string Unknown = "未知";

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Bytes(ulong? bytes)
    {
        if (bytes is null or 0)
        {
            return Unknown;
        }

        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return string.Create(Culture, $"{value:0.##} {units[index]}");
    }

    /// <summary>任务管理器网络行的写法：发送与接收共用一个单位，单位按较大的那一路取，两行数字才塞得下。</summary>
    public static string TrafficPair(ulong sentBytesPerSecond, ulong receivedBytesPerSecond)
    {
        double larger = Math.Max(sentBytesPerSecond, receivedBytesPerSecond);
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int index = 0;
        while (larger >= 1024 && index < units.Length - 1)
        {
            larger /= 1024;
            index++;
        }

        double divisor = Math.Pow(1024, index);
        return string.Create(
            Culture,
            $"发送: {sentBytesPerSecond / divisor:0.#}  接收: {receivedBytesPerSecond / divisor:0.#} {units[index]}/s");
    }

    public static string Gigabytes(ulong? bytes)
    {
        if (bytes is null or 0)
        {
            return Unknown;
        }

        return string.Create(Culture, $"{bytes.Value / 1024d / 1024d / 1024d:0.##} GB");
    }

    public static string GigabytesFromKilobytes(ulong? kilobytes) => Gigabytes(kilobytes * 1024);

    /// <summary>Windows"关于"页的可用内存写法：一位小数（61.6 GB）。</summary>
    public static string GigabytesOneDecimal(ulong? bytes) => bytes is null or 0
        ? Unknown
        : string.Create(Culture, $"{bytes.Value / 1024d / 1024d / 1024d:0.#} GB");

    /// <summary>显存按整 GB 标称：注册表常报 11.99 GiB 这种零头，标称容量就是 12 GB；不足 1 GB 保留原单位。</summary>
    public static string VideoMemory(ulong? bytes)
    {
        if (bytes is null or 0)
        {
            return Unknown;
        }

        double gib = bytes.Value / 1024d / 1024d / 1024d;
        return gib >= 1 ? string.Create(Culture, $"{gib:0} GB") : Bytes(bytes);
    }

    /// <summary>WMI 中部分容量字段直接以 MB 为单位给出。</summary>
    public static string Megabytes(long? megabytes) => megabytes is null or <= 0
        ? Unknown
        : Bytes((ulong)megabytes.Value * 1024 * 1024);

    public static string Megahertz(int? mhz) => mhz is null or 0 ? Unknown : $"{mhz.Value} MHz";

    /// <summary>任务管理器的 CPU 行用 GHz 写当前频率，比 MHz 更短也更好读。</summary>
    public static string Gigahertz(int? mhz) => mhz is null or 0
        ? Unknown
        : string.Create(Culture, $"{mhz.Value / 1000d:0.##} GHz");

    /// <summary>任务管理器内存行的写法：已用/总量 GB（百分比）。</summary>
    public static string MemoryUsage(ulong usedBytes, ulong totalBytes, double usedPercent) => string.Create(
        Culture,
        $"{usedBytes / 1024d / 1024d / 1024d:0.#}/{totalBytes / 1024d / 1024d / 1024d:0.#} GB ({usedPercent:0}%)");

    public static string Percent(double? value) => value is null ? Unknown : string.Create(Culture, $"{value.Value:0.#}%");

    public static string Celsius(float? value) => value is null or <= 0 ? Unknown : string.Create(Culture, $"{value.Value:0.#} °C");

    public static string Watts(float? value) => value is null or <= 0 ? Unknown : string.Create(Culture, $"{value.Value:0.#} W");

    public static string YesNo(bool? value) => value switch { true => "是", false => "否", null => Unknown };

    public static string Number(long? value) => value is null ? Unknown : value.Value.ToString("N0", Culture);

    public static string Date(DateTime? value, string format = "yyyy-MM-dd") =>
        value is null ? Unknown : value.Value.ToLocalTime().ToString(format, Culture);

    public static string FullDateTime(DateTime? value) => Date(value, "yyyy-MM-dd HH:mm:ss");

    public static string Since(DateTime? value)
    {
        if (value is null)
        {
            return Unknown;
        }

        var span = DateTime.Now - value.Value.ToLocalTime();
        return $"{FullDateTime(value)}（{Duration(span)}前）";
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add($"{span.Days} 天");
        }

        if (span.Hours > 0)
        {
            parts.Add($"{span.Hours} 小时");
        }

        if (span.Minutes > 0)
        {
            parts.Add($"{span.Minutes} 分钟");
        }

        if (parts.Count == 0)
        {
            parts.Add($"{span.Seconds} 秒");
        }

        return string.Join(" ", parts);
    }

    /// <summary>只保留最大的两级单位，用于宽度受限的实时指标卡。</summary>
    public static string DurationCompact(TimeSpan span, int maxParts = 2)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add($"{span.Days}天");
        }

        if (span.Hours > 0 && parts.Count < maxParts)
        {
            parts.Add($"{span.Hours}小时");
        }

        if (span.Minutes > 0 && parts.Count < maxParts)
        {
            parts.Add($"{span.Minutes}分钟");
        }

        if (parts.Count == 0)
        {
            parts.Add($"{span.Seconds}秒");
        }

        return string.Concat(parts);
    }

    public static string Map(int? key, IReadOnlyDictionary<int, string> table, string prefix = "")
    {
        if (key is null)
        {
            return Unknown;
        }

        string label = table.TryGetValue(key.Value, out var found) ? found : $"{Unknown}（{key.Value}）";
        return prefix.Length == 0 ? label : $"{prefix} {label}";
    }

    /// <summary>EDID 中的厂商/型号字符串按 ASCII 存储且以 NUL 结尾。</summary>
    public static string EdidText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
        {
            if (value == 0)
            {
                break;
            }

            builder.Append((char)value);
        }

        return builder.ToString().Trim();
    }

    public static string Join(IEnumerable<string> values, string separator = ", ")
    {
        var text = string.Join(separator, values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return text.Length == 0 ? Unknown : text;
    }

    public static IReadOnlyDictionary<int, string> ProcessorArchitecture { get; } =
        new Dictionary<int, string>
        {
            [0] = "未知",
            [1] = "x86",
            [2] = "MIPS",
            [3] = "Alpha",
            [4] = "PowerPC",
            [5] = "ARM",
            [6] = "Itanium",
            [9] = "x64",
            [12] = "ARM64",
        };

    /// <summary>SMBIOS Type 17 的 Memory Type 编码（本机 DDR5 实测为 34）。</summary>
    public static IReadOnlyDictionary<int, string> SmbiosMemoryType { get; } =
        new Dictionary<int, string>
        {
            [0] = "未知",
            [1] = "其它",
            [2] = "未知",
            [3] = "DRAM",
            [20] = "DDR",
            [21] = "DDR2",
            [22] = "DDR2 FB-DIMM",
            [24] = "DDR3",
            [26] = "DDR4",
            [27] = "LPDDR",
            [28] = "LPDDR2",
            [29] = "LPDDR3",
            [30] = "LPDDR4",
            [34] = "DDR5",
            [35] = "LPDDR5",
        };

    /// <summary>Win32_PhysicalMemory.FormFactor 编码（本机 DDR5 台式机实测 8 = DIMM）。</summary>
    public static IReadOnlyDictionary<int, string> MemoryFormFactor { get; } =
        new Dictionary<int, string>
        {
            [0] = "未知",
            [1] = "其它",
            [2] = "SIP",
            [3] = "DIP",
            [4] = "ZIP",
            [5] = "SOJ",
            [6] = "专用接口",
            [7] = "SIMM",
            [8] = "DIMM",
            [9] = "RIMM",
            [10] = "DIMM SDRAM",
            [12] = "SODIMM",
            [13] = "MicroDIMM",
            [14] = "MiniDIMM",
            [16] = "Socket DIMM (rDIMM)",
        };

    /// <summary>MSFT_PhysicalDisk.MediaType 编码；注意与文档常见的 3=SSD 说法相反，本机 NVMe 实测为 4。</summary>
    public static IReadOnlyDictionary<int, string> StorageMediaType { get; } =
        new Dictionary<int, string>
        {
            [0] = "未初始化",
            [3] = "HDD",
            [4] = "SSD",
            [5] = "SCM",
        };

    public static IReadOnlyDictionary<int, string> StorageBusType { get; } =
        new Dictionary<int, string>
        {
            [0] = "未知",
            [1] = "SCSI",
            [2] = "ATAPI",
            [3] = "ATA",
            [4] = "1394",
            [5] = "SSA",
            [6] = "Fibre Channel",
            [7] = "USB",
            [8] = "RAID",
            [9] = "iSCSI",
            [10] = "SAS",
            [11] = "SATA",
            [12] = "SD",
            [13] = "MMC",
            [14] = "Virtual",
            [15] = "File-backed",
            [16] = "Storage Spaces",
            [17] = "NVMe",
            [18] = "NVMe over Fabric",
        };

    public static IReadOnlyDictionary<int, string> LogicalDiskType { get; } =
        new Dictionary<int, string>
        {
            [0] = "未知",
            [1] = "无根目录",
            [2] = "可移动磁盘",
            [3] = "本地硬盘",
            [4] = "网络驱动器",
            [5] = "光驱",
            [6] = "RAM 磁盘",
        };

    /// <summary>Win32_NetworkAdapter.NetConnectionStatus 的官方编码（7 是"媒体未连接"，不是"已启用"）。</summary>
    public static IReadOnlyDictionary<int, string> NetworkConnectionStatus { get; } =
        new Dictionary<int, string>
        {
            [0] = "已断开",
            [1] = "正在连接",
            [2] = "已连接",
            [3] = "正在断开",
            [4] = "硬件不存在",
            [5] = "硬件已禁用",
            [6] = "硬件故障",
            [7] = "媒体未连接",
            [8] = "正在验证",
            [9] = "验证成功",
            [10] = "验证失败",
            [11] = "地址无效",
            [12] = "需要凭据",
        };

    public static IReadOnlyDictionary<int, string> SystemFamilyType { get; } =
        new Dictionary<int, string>
        {
            [0] = "未知",
            [1] = "台式机",
            [2] = "便携机",
            [3] = "移动工作站",
            [4] = "盒子",
            [5] = "笔记本",
            [6] = "Handheld PC",
            [7] = "坞站",
            [8] = "平板子系统",
            [9] = "平板",
            [10] = "可翻转",
            [11] = "Detachable",
            [12] = "All-in-One",
            [14] = "虚拟主机",
        };
}
