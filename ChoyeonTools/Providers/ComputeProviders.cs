using System.Runtime.InteropServices;
using ChoyeonTools.Models;
using ChoyeonTools.Services;

namespace ChoyeonTools.Providers;

/// <summary>CPU 分类页，对应原程序的 get_cpu_info()。</summary>
internal static class CpuProvider
{
    public const string Key = "cpu";

    public static InfoCategory Build(SensorProbe sensors)
    {
        var liveClocks = Wmi.Query("SELECT * FROM WmiProcInformation", Wmi.Scopes.Wmi).ToDictionaryByDeviceId();
        var cacheRows = Wmi.Query("SELECT InstalledSize, MaxCacheSize FROM Win32_CacheMemory");

        var groups = new List<InfoGroup>();
        foreach (var cpu in Wmi.Query("SELECT * FROM Win32_Processor").WithIndex())
        {
            string deviceId = cpu.Item.Str("DeviceID");
            var live = liveClocks.GetValueOrDefault(deviceId);
            // WMI 的 MaxClockSpeed 就是基础频率，加速频率只有固件 Type 4 的 Max Speed 才知道。
            Smbios.ProcessorSpeeds? nominal = cpu.Index < Smbios.Processors.Count ? Smbios.Processors[cpu.Index] : null;
            var fields = new List<InfoField>
            {
                new("名称", cpu.Item.Str("Name")),
                new("制造商", Vendors.Brand(cpu.Item.Str("Manufacturer"))),
                new("架构", Formatting.Map(cpu.Item.Int("Architecture"), Formatting.ProcessorArchitecture)),
                new("插槽", cpu.Item.Str("SocketDesignation")),
                new("核心数", Formatting.Number(cpu.Item.Num("NumberOfCores"))),
                new("线程数", Formatting.Number(cpu.Item.Num("ThreadCount"))),
                new("基础频率", Formatting.Megahertz(nominal?.BaseMHz ?? cpu.Item.Int("MaxClockSpeed"))),
                new("最大加速频率", Formatting.Megahertz(nominal?.MaxMHz)),
                new("当前频率", live is null
                    ? Formatting.Megahertz(cpu.Item.Int("CurrentClockSpeed"))
                    : Formatting.Megahertz(live.Int("CurrentClockMHz"))),
                new("参考时钟", Formatting.Megahertz(cpu.Item.Int("ExtClock"))),
                new("温度", SensorProbe.Celsius(sensors.CpuTemperature())),
                new("封装功耗", SensorProbe.Watts(sensors.CpuPower())),
                new("一级缓存", ReadL1Cache(cacheRows, cpu.Item.Num("L2CacheSize"))),
                new("二级缓存", Formatting.Bytes((ulong?)(cpu.Item.Num("L2CacheSize") * 1024))),
                new("三级缓存", Formatting.Bytes((ulong?)(cpu.Item.Num("L3CacheSize") * 1024))),
                new("系列描述", cpu.Item.Str("Description")),
                new("虚拟化", Formatting.YesNo(cpu.Item.Bool("VirtualizationFirmwareEnabled"))),
                new("二级地址翻译", Formatting.YesNo(cpu.Item.Bool("SecondLevelAddressTranslationExtensions"))),
            };
            groups.Add(new InfoGroup(deviceId.Length > 0 ? deviceId : $"CPU #{cpu.Index + 1}", fields));
        }

        if (groups.Count > 0)
        {
            var system = Wmi.Query("SELECT * FROM Win32_ComputerSystem").FirstOrDefault();
            groups.Add(new InfoGroup("汇总",
            [
                new("物理处理器", Formatting.Number(system?.Num("NumberOfProcessors"))),
                new("逻辑处理器", Environment.ProcessorCount.ToString()),
                new("进程架构", RuntimeInformation.ProcessArchitecture.ToString()),
                new("系统架构", RuntimeInformation.OSArchitecture.ToString()),
            ]));
        }

        return new InfoCategory(Key, "CPU", "处理器型号、核心线程、频率与缓存", groups);
    }

    /// <summary>
    /// Win32_CacheMemory 的 Level 编码整体偏移（本机把 L1 写成 3），所以不按层级号取，
    /// 改成把所有比二级缓存小的实例加起来——一级缓存总是最小的那一档。
    /// </summary>
    private static string ReadL1Cache(List<WmiObject> cacheRows, long? l2Kilobytes)
    {
        if (l2Kilobytes is not > 0)
        {
            return Formatting.Unknown;
        }

        long total = cacheRows
            .Select(row => row.Int("InstalledSize") ?? row.Int("MaxCacheSize") ?? 0)
            .Where(size => size > 0 && size < l2Kilobytes.Value)
            .Sum();

        return total > 0 ? Formatting.Bytes((ulong)total * 1024) : Formatting.Unknown;
    }
}

/// <summary>内存分类页，对应原程序的 get_memory_info()。</summary>
internal static class MemoryProvider
{
    public const string Key = "memory";

    public static InfoCategory Build()
    {
        var groups = new List<InfoGroup>();
        foreach (var (index, memory) in Wmi.Query("SELECT * FROM Win32_PhysicalMemory").WithIndex())
        {
            string locator = memory.Str("DeviceLocator");
            // 厂商字符串写成 "Unknown" 的条子，ID 只存在于原始 SMBIOS Type 17 的数字字段里。
            string? manufacturer = HideIfBlank(memory.Str("Manufacturer"))
                ?? Smbios.ModuleManufacturerId(locator, memory.Str("BankLabel"));
            var fields = new List<InfoField>
            {
                new("容量", Formatting.Gigabytes(memory.ULong("Capacity"))),
                new("类型", Formatting.Map(memory.Int("SMBIOSMemoryType"), Formatting.SmbiosMemoryType)),
                new("标称速度", Formatting.Megahertz(memory.Int("Speed"))),
                new("实际运行速度", Formatting.Megahertz(memory.Int("ConfiguredClockSpeed"))),
                new("制造商", DecodeManufacturer(manufacturer)),
                new("型号", HideIfBlank(memory.Str("PartNumber"))),
                new("序列号", HideIfBlank(memory.Str("SerialNumber"))),
                new("插槽", locator),
                new("通道", DescribeChannel(locator, memory.Str("BankLabel"))),
                new("银行标签", memory.Str("BankLabel")),
                new("外形", Formatting.Map(memory.Int("FormFactor"), Formatting.MemoryFormFactor)),
                new("数据宽度", BitWidth(memory, "DataWidth")),
                new("总宽度", BitWidth(memory, "TotalWidth")),
                new("工作电压", memory.Num("ConfiguredVoltage") is { } millivolts && millivolts > 0 ? $"{millivolts} mV" : Formatting.Unknown),
            };
            groups.Add(new InfoGroup(locator.Length > 0 ? $"内存 · {locator}" : $"内存 #{index + 1}", fields));
        }

        var operatingSystem = Wmi.Query("SELECT * FROM Win32_OperatingSystem").FirstOrDefault();
        Native.GetPhysicallyInstalledSystemMemory(out ulong installedKb);
        var usage = LiveMetrics.ReadMemory();

        // 插槽数与整机上限来自 SMBIOS Type 16（Win32_PhysicalMemoryArray），Type 17 里没有这两个字段。
        var arrays = Wmi.Query("SELECT * FROM Win32_PhysicalMemoryArray").ToList();
        int slots = arrays.Sum(array => array.Int("MemoryDevices") ?? 0);
        ulong maxKilobytes = 0;
        foreach (var array in arrays)
        {
            // MaxCapacity 在超过 4GB 的主板上会截断，优先用 Type 16 的 MaxCapacityEx。
            ulong kilobytes = array.ULong("MaxCapacityEx") is { } extended && extended > 0
                ? extended
                : array.ULong("MaxCapacity") ?? 0;
            if (kilobytes > maxKilobytes)
            {
                maxKilobytes = kilobytes;
            }
        }

        var summary = new List<InfoField>
        {
            new("SMBIOS 安装容量", installedKb > 0 ? Formatting.GigabytesFromKilobytes(installedKb) : Formatting.Unknown),
            new("系统可用容量", Formatting.GigabytesOneDecimal(operatingSystem?.ULong("TotalVisibleMemorySize") * 1024)),
            new("当前已用", usage is null ? Formatting.Unknown : Formatting.Bytes(usage.TotalBytes - usage.AvailableBytes)),
            new("当前可用", usage is null ? Formatting.Unknown : Formatting.Bytes(usage.AvailableBytes)),
            new("使用率", usage is null ? Formatting.Unknown : Formatting.Percent(usage.UsedPercent)),
            new("内存插槽占用", groups.Count > 0 && slots > 0 ? $"{groups.Count} / {slots} 条" : groups.Count > 0 ? $"{groups.Count} 条" : Formatting.Unknown),
            new("主板内存上限", maxKilobytes > 0 ? Formatting.GigabytesFromKilobytes(maxKilobytes) : Formatting.Unknown),
            new("单槽最大容量", maxKilobytes > 0 && slots > 0 ? Formatting.GigabytesFromKilobytes(maxKilobytes / (ulong)slots) : Formatting.Unknown),
        };

        var pageFiles = Wmi.Query("SELECT * FROM Win32_PageFileUsage").ToList();
        foreach (var pageFile in pageFiles)
        {
            string allocated = $"{pageFile.Str("Name")} 已分配 {Formatting.Megabytes(pageFile.Num("AllocatedBaseSize"))}";
            string current = Formatting.Megabytes(pageFile.Num("CurrentUsage"));
            summary.Add(new("页面文件", current == Formatting.Unknown ? allocated : $"{allocated} / 当前使用 {current}"));
        }

        groups.Insert(0, new InfoGroup("汇总", summary));

        return new InfoCategory(Key, "内存", "物理内存条规格与当前占用", groups);
    }

    private static string BitWidth(WmiObject memory, string property) =>
        memory.Int(property) is { } width && width > 0 ? $"{width} bits" : Formatting.Unknown;

    /// <summary>
    /// 部分主板只把容量和频率写进 SPD，厂商、型号、序列号留空或直接写 "Unknown"。
    /// 这类行按统一规则隐藏，界面不留"未写入 SPD"这种占位说明。
    /// </summary>
    private static string? HideIfBlank(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        return text.Length == 0 || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : text;
    }

    /// <summary>通道号按插槽命名推断（DIMMA2/Bank 0 → A 通道），主板不遵守惯例时只能留空。</summary>
    private static string DescribeChannel(string locator, string bankLabel)
    {
        string text = $"{locator} {bankLabel}".ToUpperInvariant();

        // 只有紧跟在 DIMM/BANK/SLOT/CHANNEL 后面的字母才是通道号；字母可选项会让 "DIMM 0" 里的 D 被当成通道。
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?:DIMM|BANK|SLOT|CHANNEL)[\s_-]*([A-H])[\s_-]*(\d*)");
        if (match.Success)
        {
            string channel = match.Groups[1].Value;
            string slot = match.Groups[2].Value;
            return slot.Length > 0 ? $"{channel} 通道 · 第 {slot} 槽" : $"{channel} 通道";
        }

        // 部分主板只写序号不写通道，退化成槽位号。
        return System.Text.RegularExpressions.Regex.Match(text, @"\d+").Value is { Length: > 0 } number
            ? $"第 {number} 槽"
            : Formatting.Unknown;
    }

    /// <summary>
    /// SMBIOS 里的厂商常写成 "80CE" 这样的 JEDEC ID：高字节 & 0x7F + 1 是银行，低字节 & 0x7F 是条目，
    /// 与 nwinfo 的解码约定一致（80CE = 1 银行 78 号 = 三星）。
    /// </summary>
    private static string? DecodeManufacturer(string? raw)
    {
        string? value = HideIfBlank(raw);
        if (value is null)
        {
            return null;
        }

        string hex = value.Replace(" ", string.Empty);
        var match = System.Text.RegularExpressions.Regex.Match(hex, @"^(?:00)*([0-9A-Fa-f]{2}(?:[0-9A-Fa-f]{2})?)$");
        if (!match.Success)
        {
            return Vendors.Brand(value);
        }

        int id = Convert.ToInt32(match.Groups[1].Value, 16);
        int bank = ((id >> 8) & 0x7F) + 1;
        int item = id & 0x7F;
        return NameTable.Jedec.Find($"{bank}:{item}") is { } name
            ? Vendors.BrandOnly(name)
            : $"JEDEC 0x{id:X4}";
    }
}

/// <summary>GPU 分类页，对应原程序的 get_gpu_info()。</summary>
internal static class GpuProvider
{
    public const string Key = "gpu";

    public static InfoCategory Build(PnpDrivers drivers, HardwareCatalog catalog, SensorProbe sensors)
    {
        var controllers = Wmi.Query("SELECT * FROM Win32_VideoController");
        var nvidiaGpus = VendorCli.QueryNvidia();
        double? enginePeak = LiveMetrics.ReadGpuEngineUsage().Select(u => u.PeakPercent).Append(0d).Max();
        var groups = new List<InfoGroup>();
        int nvidiaIndex = 0;

        foreach (var gpu in controllers)
        {
            string name = gpu.Str("Name");
            string pnpDeviceId = gpu.Str("PNPDeviceID");
            var driver = drivers.Find(pnpDeviceId);
            bool isNvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                || name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                || name.Contains("GTX", StringComparison.OrdinalIgnoreCase);
            VendorCli.NvidiaGpu? stats = isNvidia && nvidiaIndex < nvidiaGpus.Count ? nvidiaGpus[nvidiaIndex++] : null;

            // Win32_VideoController.VideoProcessor 在多数驱动上是空的，芯片代号改用 PCI ID 名称库补齐。
            string chip = catalog.FindDeviceName(pnpDeviceId) ?? gpu.Str("VideoProcessor");
            string vendor = Vendors.Brand(catalog.FindVendorName(pnpDeviceId) ?? gpu.Str("AdapterCompatibility"));

            var fields = new List<InfoField>
            {
                new("名称", name),
            };
            AddIdentity(fields, "显卡芯片", chip, name);
            AddIdentity(fields, "芯片厂商", vendor, name);
            // 子系统厂商是板卡品牌（耕升、华硕等），与芯片厂商（NVIDIA/AMD）是两回事。
            if (catalog.FindSubsystemVendor(pnpDeviceId) is { } boardVendor)
            {
                fields.Add(new("板卡厂商", Vendors.Brand(boardVendor)));
            }

            fields.AddRange([
                new("显存容量", Formatting.VideoMemory(ReadRealAdapterMemory(pnpDeviceId) ?? gpu.ULong("AdapterRAM"))),
                new("驱动版本", driver?.DriverVersion ?? gpu.Str("DriverVersion")),
                new("驱动日期", Formatting.Date(driver?.DriverDate ?? gpu.Time("DriverDate"))),
                new("驱动提供程序", driver?.Provider ?? Formatting.Unknown),
                new("INF 文件", driver?.InfName ?? Formatting.Unknown),
                new("当前分辨率", ReadCurrentMode(gpu)),
                new("刷新率", gpu.Int("CurrentRefreshRate") is { } hertz && hertz > 0 ? $"{hertz} Hz" : Formatting.Unknown),
                new("颜色深度", gpu.Int("CurrentBitsPerPixel") is { } depth && depth > 0 ? $"{depth} 位" : Formatting.Unknown),
                new("设备状态", gpu.Str("Status")),
                new("PnP 设备 ID", pnpDeviceId),
            ]);

            if (stats is not null)
            {
                fields.Add(new("GPU 占用率", stats.UtilizationPercent < 0 ? Formatting.Unknown : $"{stats.UtilizationPercent}%"));
                fields.Add(new("显存已用", Mebibytes(stats.MemoryUsedMb)));
                fields.Add(new("显存空闲", Mebibytes(stats.MemoryFreeMb)));
                fields.Add(new("显存总量", Mebibytes(stats.MemoryTotalMb)));
                fields.Add(new("温度", stats.TemperatureC < 0 ? Formatting.Unknown : $"{stats.TemperatureC} °C"));
            }
            else
            {
                float? sensorLoad = sensors.GpuLoad(name);
                fields.Add(new("GPU 占用率", sensorLoad is null
                    ? $"全部显卡引擎峰值 {Formatting.Percent(enginePeak)}"
                    : SensorProbe.Percent(sensorLoad)));
                fields.Add(new("温度", SensorProbe.Celsius(sensors.GpuTemperature(name))));
            }

            groups.Add(new InfoGroup(name, fields));
        }

        return new InfoCategory(Key, "GPU", "显卡型号、显存、驱动与实时占用", groups);
    }

    private static string Mebibytes(long value) => value < 0 ? Formatting.Unknown : $"{value} MiB";

    /// <summary>驱动回填的芯片串经常和产品名一模一样，重复时不再占一行。</summary>
    private static void AddIdentity(List<InfoField> fields, string label, string value, string productName)
    {
        if (value.Trim().Length > 0 && !value.Equals(productName, StringComparison.OrdinalIgnoreCase))
        {
            fields.Add(new InfoField(label, value));
        }
    }

    private static string ReadCurrentMode(WmiObject gpu)
    {
        int width = gpu.Int("CurrentHorizontalResolution") ?? 0;
        int height = gpu.Int("CurrentVerticalResolution") ?? 0;
        return width > 0 && height > 0 ? $"{width} x {height}" : gpu.Str("VideoModeDescription");
    }

    /// <summary>Win32_VideoController.AdapterRAM 上限 4GB，真实显存要读显卡类注册表项。</summary>
    private static ulong? ReadRealAdapterMemory(string pnpDeviceId)
    {
        if (pnpDeviceId.Length == 0)
        {
            return null;
        }

        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null)
            {
                return null;
            }

            foreach (string subName in classKey.GetSubKeyNames())
            {
                using var deviceKey = classKey.OpenSubKey(subName);
                if (deviceKey is null)
                {
                    continue;
                }

                string matching = deviceKey.GetValue("MatchingDeviceID") as string ?? string.Empty;
                if (matching.Length > 0 && pnpDeviceId.StartsWith(matching, StringComparison.OrdinalIgnoreCase))
                {
                    return deviceKey.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long bytes when bytes > 0 => (ulong)bytes,
                        byte[] buffer when buffer.Length == 8 => (ulong)BitConverter.ToInt64(buffer),
                        _ => null,
                    };
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or IOException or System.Security.SecurityException)
        {
            // 标准用户通常无权读取显卡类注册表项，回退到 WMI 的 AdapterRAM。
        }

        return null;
    }
}
