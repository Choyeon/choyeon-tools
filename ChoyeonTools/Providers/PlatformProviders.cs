using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ChoyeonTools.Models;
using ChoyeonTools.Services;

namespace ChoyeonTools.Providers;

/// <summary>声卡分类页，对应原程序的 get_sound_info()。</summary>
internal static class SoundProvider
{
    public const string Key = "sound";

    public static InfoCategory Build(PnpDrivers drivers, HardwareCatalog catalog)
    {
        var groups = new List<InfoGroup>();
        foreach (var sound in Wmi.Query("SELECT * FROM Win32_SoundDevice"))
        {
            string pnpDeviceId = sound.Str("PNPDeviceID");
            var driver = drivers.Find(pnpDeviceId.Length > 0 ? pnpDeviceId : sound.Str("DeviceID"));
            var fields = new List<InfoField>
            {
                new("名称", sound.Str("Name")),
                new("制造商", Vendors.Brand(sound.Str("Manufacturer"))),
                new("状态", sound.Str("Status")),
                new("驱动版本", driver?.DriverVersion ?? Formatting.Unknown),
                new("驱动日期", Formatting.Date(driver?.DriverDate)),
                new("驱动提供程序", driver?.Provider ?? Formatting.Unknown),
                new("INF 文件", driver?.InfName ?? Formatting.Unknown),
                new("设备 ID", sound.Str("DeviceID")),
            };

            string? chip = catalog.Describe(pnpDeviceId.Length > 0 ? pnpDeviceId : sound.Str("DeviceID"));
            if (chip is not null)
            {
                fields.Insert(1, new("音频芯片", chip));
            }

            groups.Add(new InfoGroup(sound.Str("Name"), fields));
        }

        return new InfoCategory(Key, "声卡", "音频设备与驱动信息", groups);
    }
}

/// <summary>
/// USB 分类页。Windows 对 USB 设备只报 "USB Composite Device"、"USB 输入设备" 这类通用名称，
/// 产品名要用 usb.ids 名称库按 VID/PID 还原，所以这里按硬件 ID 把复合设备及其子功能合并成一张卡片。
/// </summary>
internal static class UsbProvider
{
    public const string Key = "usb";

    private sealed class Bucket(string hardwareId)
    {
        public string HardwareId { get; } = hardwareId;

        public List<string> Instances { get; } = [];

        public List<string> Functions { get; } = [];

        public IReadOnlyList<string> Classes => _classes;

        public IReadOnlyList<string> Manufacturers => _manufacturers;

        public string? ParentDeviceId { get; set; }

        public string? ReportName { get; set; }

        public void AddClass(string? value) => AddDistinct(_classes, value);

        public void AddManufacturer(string? value) => AddDistinct(_manufacturers, value);

        private readonly List<string> _classes = [];

        private readonly List<string> _manufacturers = [];

        private static void AddDistinct(List<string> target, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value);
            }
        }
    }

    public static InfoCategory Build(PnpDrivers drivers, HardwareCatalog catalog)
    {
        var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Wmi.Query("SELECT DeviceID, Name, Manufacturer, PNPClass FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'"))
        {
            string deviceId = row.Str("DeviceID");
            string hardwareId = ToHardwareId(deviceId);
            if (hardwareId.Length == 0)
            {
                continue;
            }

            if (!buckets.TryGetValue(hardwareId, out var bucket))
            {
                bucket = new Bucket(hardwareId);
                buckets[hardwareId] = bucket;
            }

            // 复合设备的每个功能（MI_00 音频、MI_03 输入…）各自是一个实例，产品相同，归到同一张卡片。
            bucket.Instances.Add(deviceId);
            if (deviceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            {
                bucket.Functions.Add(row.Str("Name"));
            }
            else
            {
                bucket.ParentDeviceId ??= deviceId;
                bucket.ReportName ??= row.Str("Name");
            }

            bucket.AddClass(row.StrOrNull("PNPClass"));
            bucket.AddManufacturer(row.StrOrNull("Manufacturer"));
        }

        var groups = new List<InfoGroup>();
        foreach (Bucket bucket in buckets.Values)
        {
            // 名称库只需要厂商号与产品号，用硬件 ID 拼出可查询的形式即可，不必带实例路径。
            string lookupId = @"USB\" + bucket.HardwareId;
            string? product = catalog.Describe(lookupId);
            string? vendor = Vendors.Brand(catalog.FindVendorName(lookupId));
            string instanceId = bucket.ParentDeviceId ?? bucket.Instances[0];
            var driver = drivers.Find(instanceId);

            // 标题里的产品名不带厂商后缀，厂商全称由"厂商"一行给出，否则会出现两层括号。
            string title = catalog.FindDeviceName(lookupId) ?? bucket.ReportName ?? bucket.HardwareId;
            groups.Add(new InfoGroup($"{title}（{bucket.HardwareId}）",
            [
                new("设备", product),
                new("厂商", vendor),
                new("系统自报名", bucket.ReportName),
                new("接口功能", Formatting.Join(bucket.Functions, "；")),
                new("设备类别", Formatting.Join(bucket.Classes, "；")),
                new("制造商", Formatting.Join(bucket.Manufacturers, "；")),
                new("实例数", bucket.Instances.Count > 1 ? bucket.Instances.Count.ToString() : null),
                new("驱动版本", driver?.DriverVersion),
                new("驱动日期", Formatting.Date(driver?.DriverDate)),
                new("驱动提供程序", driver?.Provider),
                new("INF 文件", driver?.InfName),
                new("设备实例 ID", instanceId),
            ]));
        }

        return new InfoCategory(Key, "USB", "USB 设备与集线器，按 VID/PID 还原产品名", groups);
    }

    /// <summary>"USB\VID_2D99&amp;PID_0028&amp;MI_00\7&amp;..." 取到 "VID_2D99&amp;PID_0028"，去掉子功能号和实例路径。</summary>
    private static string ToHardwareId(string deviceId)
    {
        string[] parts = deviceId.Split('\\');
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        int functionAt = parts[1].IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        return functionAt >= 0 ? parts[1][..functionAt] : parts[1];
    }
}

/// <summary>网络分类页，对应原程序的 get_network_info()。</summary>
internal static class NetworkProvider
{
    public const string Key = "network";

    public static InfoCategory Build(PnpDrivers drivers, HardwareCatalog catalog)
    {
        var configByIndex = new Dictionary<int, WmiObject>();
        foreach (var config in Wmi.Query("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=TRUE"))
        {
            if (config.Int("Index") is { } index)
            {
                configByIndex[index] = config;
            }
        }

        var groups = new List<InfoGroup>();
        var mtuByMac = ReadMtus();
        foreach (var adapter in Wmi.Query("SELECT * FROM Win32_NetworkAdapter"))
        {
            // 以物理网卡为主：拔掉网线的板载网卡没有 IP 配置，只按 IPEnabled 查就会整个消失。
            // 虚拟适配器只保留已经配好 IP 的那些，WAN Miniport、isatap 之类空项不占页面。
            var config = adapter.Int("Index") is { } index ? configByIndex.GetValueOrDefault(index) : null;
            if (adapter.Bool("PhysicalAdapter") != true && config is null)
            {
                continue;
            }

            string pnpDeviceId = adapter.Str("PNPDeviceID");
            string description = adapter.StrOrNull("Description") ?? Formatting.Unknown;
            string macAddress = adapter.StrOrNull("MACAddress") ?? config?.Str("MACAddress") ?? string.Empty;
            int mtu = mtuByMac.GetValueOrDefault(macAddress.Replace(":", string.Empty));
            ulong? linkBits = ReadLinkSpeed(adapter);

            var fields = new List<InfoField>
            {
                new("名称", adapter.StrOrNull("NetConnectionID") ?? description),
                new("物理描述", description),
            };

            string? chip = catalog.Describe(pnpDeviceId);
            if (chip is not null)
            {
                fields.Add(new("网卡芯片", chip));
            }

            fields.Add(new("物理地址", macAddress));

            if (config is not null)
            {
                string[] primaryWins = [config.Str("WINSPrimaryServer"), config.Str("WINSSecondaryServer")];
                fields.AddRange([
                    new("IP 地址", FormatAddresses(config)),
                    new("默认网关", Formatting.Join(config.StrArray("DefaultIPGateway"))),
                    new("DNS 服务器", Formatting.Join(config.StrArray("DNSServerSearchOrder"))),
                    new("DNS 后缀", Formatting.Join(config.StrArray("DNSDomainSuffixSearchOrder"))),
                    new("DHCP", config.Bool("DHCPEnabled") == true
                        ? $"已启用（{config.Str("DHCPServer")}）"
                        : Formatting.YesNo(config.Bool("DHCPEnabled"))),
                    new("WINS 服务器", Formatting.Join(primaryWins)),
                ]);
            }

            fields.AddRange([
                new("MTU", mtu > 0 ? $"{mtu} 字节" : Formatting.Unknown),
                new("链路速度", linkBits is { } bits ? LinkSpeed(bits) : Formatting.Unknown),
                new("制造商", Vendors.Brand(adapter.Str("Manufacturer"))),
                new("物理适配器", Formatting.YesNo(adapter.Bool("PhysicalAdapter"))),
                new("连接状态", adapter.Int("NetConnectionStatus") is { } status
                    ? Formatting.Map(status, Formatting.NetworkConnectionStatus)
                    : Formatting.Unknown),
                new("驱动版本", drivers.Find(pnpDeviceId)?.DriverVersion ?? Formatting.Unknown),
                new("适配器服务", adapter.StrOrNull("ServiceName") ?? Formatting.Unknown),
            ]);

            groups.Add(new InfoGroup(description, fields));
        }

        var ipProperties = IPGlobalProperties.GetIPGlobalProperties();
        var computerSystem = Wmi.Query("SELECT * FROM Win32_ComputerSystem").FirstOrDefault();
        int ipConnectionCount = configByIndex.Count;
        groups.Add(new InfoGroup("主机",
        [
            new("计算机名", ipProperties.HostName),
            new("完整域名", string.IsNullOrEmpty(ipProperties.DomainName) ? ipProperties.HostName : $"{ipProperties.HostName}.{ipProperties.DomainName}"),
            new("域或工作组", FormatDomain(computerSystem)),
            new("物理网卡数量", Formatting.Number(Wmi.Query("SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE").Count)),
            new("已启用 IP 的连接", ipConnectionCount.ToString()),
        ]));

        return new InfoCategory(Key, "网络", "网络连接、IP 配置与驱动信息", groups);
    }

    /// <summary>Win32_NetworkAdapterConfiguration 没有 MTU 属性，改用 .NET 网络接口信息按 MAC 对齐。</summary>
    private static Dictionary<string, int> ReadMtus()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                string mac = nic.GetPhysicalAddress().ToString();
                if (mac.Length > 0)
                {
                    map[mac] = nic.GetIPProperties().GetIPv4Properties().Mtu;
                }
            }
            catch (Exception)
            {
                // 隧道、PPP 等虚拟接口没有 IPv4 配置（.NET 在这里抛出的是内部 NetInfoException），取不到 MTU 就跳过。
            }
        }

        return map;
    }

    private static string FormatAddresses(WmiObject config)
    {
        string[] addresses = config.StrArray("IPAddress");
        string[] subnets = config.StrArray("IPSubnet");
        var lines = new List<string>(addresses.Length);
        for (int i = 0; i < addresses.Length; i++)
        {
            string subnet = i < subnets.Length ? subnets[i] : string.Empty;
            lines.Add(subnet.Length > 0 ? $"{addresses[i]} / {subnet}" : addresses[i]);
        }

        return Formatting.Join(lines, "；");
    }

    /// <summary>
    /// 未插网线的适配器里 Speed 常是 0xFFFFFFFFFFFFFFFF 这类占位值，
    /// 直接换算会显示成几万 Tbps，所以超过单口物理上限都当作没读到。
    /// </summary>
    private const ulong MaxPlausibleLinkBits = 800_000_000_000;

    private static ulong? ReadLinkSpeed(WmiObject adapter) =>
        adapter.ULong("Speed") is { } bits && bits > 0 && bits <= MaxPlausibleLinkBits ? bits : null;

    private static string LinkSpeed(ulong bitsPerSecond)
    {
        double megabits = bitsPerSecond / 1_000_000d;
        return megabits >= 1000
            ? string.Create(CultureInfo.CurrentCulture, $"{megabits / 1000:0.#} Gbps")
            : string.Create(CultureInfo.CurrentCulture, $"{megabits:0.#} Mbps");
    }

    private static string FormatDomain(WmiObject? computerSystem)
    {
        if (computerSystem is null)
        {
            return Formatting.Unknown;
        }

        return computerSystem.Bool("PartOfDomain") == true
            ? $"域：{computerSystem.Str("Domain")}"
            : $"工作组：{computerSystem.Str("Domain")}";
    }
}

/// <summary>系统分类页，对应原程序的 get_system_info()。</summary>
internal static class SystemProvider
{
    public const string Key = "system";

    public static InfoCategory Build(HardwareCatalog catalog)
    {
        var system = Wmi.Query("SELECT * FROM Win32_ComputerSystem").FirstOrDefault();
        var os = Wmi.Query("SELECT * FROM Win32_OperatingSystem").FirstOrDefault();
        var bios = Wmi.Query("SELECT * FROM Win32_BIOS").FirstOrDefault();
        var board = Wmi.Query("SELECT * FROM Win32_BaseBoard").FirstOrDefault();
        var product = Wmi.Query("SELECT * FROM Win32_ComputerSystemProduct").FirstOrDefault();

        var groups = new List<InfoGroup>
        {
            new("计算机",
            [
                new("制造商", Vendors.Brand(system?.Str("Manufacturer"))),
                new("型号", system?.Str("Model") ?? Formatting.Unknown),
                new("系统类型", system?.Str("SystemType") ?? Formatting.Unknown),
                new("机型类别", system?.Int("PCSystemType") is { } type ? Formatting.Map(type, Formatting.SystemFamilyType) : Formatting.Unknown),
                new("主总线类型", system?.Str("PrimaryBusType") ?? Formatting.Unknown),
                new("处理器数量", Formatting.Number(system?.Num("NumberOfProcessors"))),
                new("已安装内存", InstalledMemory(system)),
            ]),
            new("操作系统",
            [
                new("系统家族", os?.Str("Caption") ?? Formatting.Unknown),
                new("版本", BuildVersion(os)),
                new("内部版本号", os?.Str("BuildNumber") ?? Formatting.Unknown),
                new("体系结构", os?.Str("OSArchitecture") ?? Formatting.Unknown),
                new("产品线", os?.Int("ProductType") is { } productType
                    ? Formatting.Map(productType, new Dictionary<int, string> { [1] = "工作站", [2] = "域控制器", [3] = "服务器" })
                    : Formatting.Unknown),
                new("区域设置", FormatLocale(os?.Str("Locale"))),
                new("安装时间", Formatting.Since(os?.Time("InstallDate"))),
                new("上次启动", Formatting.Since(os?.Time("LastBootUpTime"))),
                new("已运行", os?.Time("LastBootUpTime") is { } boot
                    ? Formatting.Duration(DateTime.Now - boot.ToLocalTime())
                    : Formatting.Unknown),
                new("注册用户", os?.Str("RegisteredUser") ?? Formatting.Unknown),
                new("系统目录", os?.Str("SystemDirectory") ?? Formatting.Unknown),
            ]),
            new("BIOS 与主板",
            [
                new("BIOS 厂商", Vendors.Brand(bios?.Str("Manufacturer"))),
                new("BIOS 版本", bios?.Str("SMBIOSBIOSVersion") ?? Formatting.Unknown),
                new("BIOS 发布日期", Formatting.Date(bios?.Time("ReleaseDate"))),
                new("BIOS 序列号", bios?.Str("SerialNumber") ?? Formatting.Unknown),
                new("主板制造商", Vendors.Brand(board?.Str("Manufacturer"))),
                new("主板型号", board?.Str("Product") ?? Formatting.Unknown),
                new("主板版本", board?.Str("Version") ?? Formatting.Unknown),
                new("主板序列号", board?.Str("SerialNumber") ?? Formatting.Unknown),
            ]),
            new("产品标识",
            [
                new("厂商", Vendors.Brand(product?.Str("Vendor"))),
                new("产品名称", product?.Str("Name") ?? Formatting.Unknown),
                new("SKU", product?.Str("SKU") ?? Formatting.Unknown),
                new("版本", product?.Str("Version") ?? Formatting.Unknown),
                new("UUID", product?.Str("UUID") ?? Formatting.Unknown),
            ]),
            new("运行环境",
            [
                new(".NET 运行时", RuntimeInformation.FrameworkDescription),
                new("运行时版本", Environment.Version.ToString()),
                new("Windows App SDK", WindowsAppSdkVersion()),
                new("当前用户", Environment.UserName),
                new("处理器架构", RuntimeInformation.OSArchitecture.ToString()),
            ]),
        };

        // 固件与安全紧接在"BIOS 与主板"之后。
        groups.Insert(3, BuildFirmwareGroup());

        var chipset = BuildChipsetGroup(catalog);
        if (chipset is not null)
        {
            groups.Insert(groups.Count - 1, chipset);
        }

        return new InfoCategory(Key, "系统", "计算机、操作系统、BIOS 与主板信息", groups);
    }

    /// <summary>
    /// Win32_ComputerSystem.TotalPhysicalMemory 报的是"可用"内存（64 GB 机器只报 61.6），
    /// 已安装容量以 SMBIOS 为准（与内存页的"SMBIOS 安装容量"同源），读不到才退回 WMI。
    /// </summary>
    private static string InstalledMemory(WmiObject? system) =>
        Native.GetPhysicallyInstalledSystemMemory(out ulong installedKb) && installedKb > 0
            ? Formatting.GigabytesFromKilobytes(installedKb)
            : Formatting.GigabytesFromKilobytes(system?.ULong("TotalPhysicalMemory") / 1024);

    /// <summary>
    /// 主板固件的版本与开关状态只写在注册表的 SMBIOS 表和固件安全项里，WMI 没有对应属性；
    /// 这一组按 AIDA64 的"主板/BIOS"页补齐，读不到的项由界面隐藏。
    /// </summary>
    private static InfoGroup BuildFirmwareGroup()
    {
        var biosValues = ReadKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var secureBootValues = ReadKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        var deviceGuardValues = ReadKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Features\MemoryIntegrity");

        string? smbios = FirmwareVersion(biosValues, "BiosMajorRelease", "BiosMinorRelease");
        // 没有嵌入式控制器的主板把 EC 版本写成 255，等于没填。
        string? ec = FirmwareVersion(biosValues, "ECFirmwareMajorRelease", "ECFirmwareMinorRelease");
        if (ec == "255.255")
        {
            ec = null;
        }

        string? chassis = null;
        foreach (var enclosure in Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
        {
            // SMBIOS 的机箱类型可能一次给出多个值，取第一个能认出的编码。
            chassis = enclosure.IntArray("ChassisTypes")
                .Select(code => ChassisTypes.TryGetValue(code, out var name) ? name : null)
                .FirstOrDefault(name => name is not null);
            if (chassis is not null)
            {
                break;
            }
        }

        return new InfoGroup("固件与安全",
        [
            new("启动方式", FirmwareMode()),
            new("安全启动", Formatting.YesNo(ReadFlag(secureBootValues, "UEFISecureBootEnabled"))),
            new("内核强制代码完整性", Formatting.YesNo(ReadFlag(deviceGuardValues, "Enabled"))),
            new("SMBIOS 版本", smbios),
            new("EC 固件版本", ec),
            new("机箱类型", chassis),
        ]);
    }

    private static string FirmwareMode()
    {
        if (!Native.GetFirmwareType(out uint type))
        {
            return Formatting.Unknown;
        }

        return type switch
        {
            1 => "Legacy BIOS",
            2 => "UEFI",
            _ => Formatting.Unknown,
        };
    }

    /// <summary>把注册表里的两个版本号字段拼成 "5.41"；缺任意一段就不显示。</summary>
    private static string? FirmwareVersion(Dictionary<string, object?> values, string majorName, string minorName) =>
        values.GetValueOrDefault(majorName) is int major && values.GetValueOrDefault(minorName) is int minor
            ? $"{major}.{minor}"
            : null;

    /// <summary>注册表 DWORD 开关：只有确实读到值时才回答是或否。</summary>
    private static bool? ReadFlag(Dictionary<string, object?> values, string name) =>
        values.GetValueOrDefault(name) as int? is { } raw ? raw != 0 : null;

    private static Dictionary<string, object?> ReadKey(string relativePath)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(relativePath);
            if (key is not null)
            {
                foreach (string name in key.GetValueNames())
                {
                    values[name] = key.GetValue(name);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException or System.Security.SecurityException)
        {
            // 项不存在或无权读取时按"没读到"处理。
        }

        return values;
    }

    /// <summary>SMBIOS Type 3 的常见机箱编码，只收用得上的档位，认不出就不显示。</summary>
    private static readonly Dictionary<int, string> ChassisTypes = new()
    {
        [3] = "台式机",
        [4] = "薄型台式机",
        [6] = "服务器（机架式）",
        [7] = "塔式",
        [8] = "便携机",
        [9] = "笔记本",
        [10] = "笔记本",
        [12] = "坞站",
        [13] = "一体机",
        [14] = "子笔记本",
        [15] = "空间节省型",
        [16] = "机架式",
        [17] = "机架式",
        [23] = "系统机箱",
        [28] = "模块化机架",
        [30] = "立式箱体",
        [32] = "便携式",
        [34] = "密封式机箱",
        [39] = "平板",
        [40] = "可翻转本",
        [41] = "可拆卸本",
    };

    /// <summary>
    /// 主板芯片组没有独立的 WMI 类，只能按 PnP 的 System 类设备逐个查名称库，
    /// 把 "PCI 设备 #7A0D" 还原成 "WM790 Chipset LPC/eSPI Controller" 这样的条目。
    /// 同一条 PCIe 链路会枚举出多个重复实例，按 名称+芯片 合并后只报一次并给出数量。
    /// </summary>
    private static InfoGroup? BuildChipsetGroup(HardwareCatalog catalog)
    {
        if (!catalog.IsLoaded)
        {
            return null;
        }

        var merged = new List<(string Label, string Chip, int Count)>();
        foreach (var device in Wmi.Query("SELECT DeviceID, Name FROM Win32_PnPEntity WHERE PNPClass='System'"))
        {
            string label = device.Str("Name");
            string? chip = catalog.FindDeviceName(device.Str("DeviceID"));
            if (chip is null)
            {
                continue;
            }

            int at = merged.FindIndex(entry => entry.Label == label && entry.Chip == chip);
            if (at >= 0)
            {
                var existing = merged[at];
                merged[at] = (existing.Label, existing.Chip, existing.Count + 1);
            }
            else
            {
                merged.Add((label, chip, 1));
            }
        }

        if (merged.Count == 0)
        {
            return null;
        }

        merged.Sort((left, right) => string.Compare(right.Chip, left.Chip, StringComparison.CurrentCulture));
        const int limit = 40;
        var fields = merged.Take(limit)
            .Select(entry => new InfoField(entry.Count > 1 ? $"{entry.Label} ×{entry.Count}" : entry.Label, entry.Chip))
            .ToList();
        if (merged.Count > limit)
        {
            fields.Add(new InfoField("其它板载设备", $"另有 {merged.Count - limit} 项未显示"));
        }

        return new InfoGroup("芯片组与板载设备", fields);
    }

    /// <summary>"10.0.26100" 加上注册表 UBR 才是完整的 Windows 版本串。</summary>
    private static string BuildVersion(WmiObject? os)
    {
        if (os is null)
        {
            return Formatting.Unknown;
        }

        string version = os.Str("Version");
        string build = os.Str("BuildNumber");
        (int? ubr, string display) = ReadCurrentVersion();
        var parts = new List<string>();
        if (version.Length > 0)
        {
            parts.Add(ubr is { } revision ? $"{version}.{revision}" : version);
        }

        if (build.Length > 0)
        {
            parts.Add($"（内部版本 {build}{(display.Length > 0 ? $", {display}" : string.Empty)}）");
        }

        return parts.Count == 0 ? Formatting.Unknown : string.Join(" ", parts);
    }

    /// <summary>UBR 与 DisplayVersion（如 26H2）只在注册表里，Win32_OperatingSystem 没有这两个属性。</summary>
    private static (int? Ubr, string DisplayVersion) ReadCurrentVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return (key?.GetValue("UBR") as int?, key?.GetValue("DisplayVersion") as string ?? string.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException or System.Security.SecurityException)
        {
            return (null, string.Empty);
        }
    }

    private static string FormatLocale(string? hexLcid)
    {
        if (hexLcid is null || !int.TryParse(hexLcid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int lcid))
        {
            return Formatting.Unknown;
        }

        try
        {
            return $"{new CultureInfo(lcid).NativeName}（{hexLcid}）";
        }
        catch (ArgumentException)
        {
            return hexLcid;
        }
    }

    /// <summary>Microsoft.WinUI.dll 只带 3.0.0.0 的程序集版本，运行时版本要看引导 DLL 的文件版本。</summary>
    private static string WindowsAppSdkVersion()
    {
        string bootstrap = Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.Bootstrap.dll");

        return File.Exists(bootstrap)
            ? FileVersionInfo.GetVersionInfo(bootstrap).FileVersion ?? Formatting.Unknown
            : Formatting.Unknown;
    }
}
