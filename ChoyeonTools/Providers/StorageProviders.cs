using System.Globalization;
using ChoyeonTools.Models;
using ChoyeonTools.Services;

namespace ChoyeonTools.Providers;

/// <summary>硬盘分类页，对应原程序的 get_disk_info()。</summary>
internal static class DiskProvider
{
    public const string Key = "disk";

    private static readonly Dictionary<int, string> HealthStatusTable = new()
    {
        [0] = "正常",
        [1] = "维护中",
        [2] = "警告",
        [3] = "通信丢失",
        [4] = "需要重启",
        [5] = "临时状态",
    };

    public static InfoCategory Build(PnpDrivers drivers, SensorProbe sensors)
    {
        var storageRows = Wmi.Query("SELECT * FROM MSFT_PhysicalDisk", Wmi.Scopes.Storage);
        var reliabilityRows = Wmi.Query("SELECT * FROM MSFT_StorageReliabilityCounter", Wmi.Scopes.Storage);
        var driveRows = Wmi.Query("SELECT * FROM Win32_DiskDrive");
        int? bootDiskIndex = ReadBootDiskIndex();
        var groups = new List<InfoGroup>();

        foreach (var disk in driveRows)
        {
            string model = disk.Str("Model");
            int? diskIndex = disk.Int("Index");
            var storage = MatchStorageRow(storageRows, model, diskIndex);
            var reliability = MatchByDeviceId(reliabilityRows, diskIndex);
            var driver = drivers.Find(disk.Str("PNPDeviceID"));
            int partitions = disk.Int("Partitions") ?? 0;
            int? mediaType = storage?.Int("MediaType");

            var fields = new List<InfoField>
            {
                new("名称", disk.Str("Caption")),
                new("型号", model),
                new("接口类型", storage is not null
                    ? Formatting.Map(storage.Int("BusType"), Formatting.StorageBusType)
                    : disk.Str("InterfaceType")),
                new("媒体类型", mediaType is { } code
                    ? Formatting.Map(code, Formatting.StorageMediaType)
                    : disk.Str("MediaType")),
                new("总大小", Formatting.Bytes(disk.ULong("Size"))),
                new("分区数", partitions.ToString()),
                new("扇区大小", disk.Num("BytesPerSector") is { } sector && sector > 0 ? $"{sector} B" : Formatting.Unknown),
                new("序列号", disk.Str("SerialNumber").Trim('.', '_')),
                new("固件版本", storage?.StrOrNull("FirmwareVersion") ?? disk.Str("FirmwareRevision")),
                new("健康状态", storage?.Int("HealthStatus") is { } health
                    ? Formatting.Map(health, HealthStatusTable)
                    : disk.Str("Status")),
                new("温度", SensorProbe.Celsius(sensors.DriveTemperature(model) ?? SmartTemperature(reliability))),
                new("启动磁盘", bootDiskIndex is null ? Formatting.Unknown : Formatting.YesNo(diskIndex == bootDiskIndex)),
                new("转速", mediaType is 4 or 5
                    ? "不适用（固态硬盘）"
                    : storage?.Num("SpindleSpeed") is { } rpm && rpm > 0 ? $"{rpm} RPM" : Formatting.Unknown),
                new("通电时长", reliability?.Num("PowerOnHours") is { } hours && hours > 0 ? $"{hours:N0} 小时" : Formatting.Unknown),
                new("通电次数", Formatting.Number(reliability?.Num("PowerCycleCount"))),
                new("磨损度", reliability?.Int("Wear") is { } wear && wear >= 0 ? $"{wear}%" : Formatting.Unknown),
                new("驱动版本", driver?.DriverVersion ?? Formatting.Unknown),
                new("PnP 设备 ID", disk.Str("PNPDeviceID")),
            };
            groups.Add(new InfoGroup(model.Length > 0 ? model : disk.Str("Caption"), fields));
        }

        foreach (var volume in Wmi.Query("SELECT * FROM Win32_LogicalDisk"))
        {
            ulong size = volume.ULong("Size") ?? 0;
            ulong free = volume.ULong("FreeSpace") ?? 0;
            groups.Add(new InfoGroup($"驱动器 {volume.Str("DeviceID")}",
            [
                new("卷标", volume.Str("VolumeName")),
                new("文件系统", volume.Str("FileSystem")),
                new("类型", Formatting.Map(volume.Int("DriveType"), Formatting.LogicalDiskType)),
                new("总容量", Formatting.Bytes(size)),
                new("剩余空间", Formatting.Bytes(free)),
                new("已用空间", size > 0 ? Formatting.Bytes(size - free) : Formatting.Unknown),
                new("使用率", size > 0 ? Formatting.Percent((size - free) * 100d / size) : Formatting.Unknown),
            ]));
        }

        return new InfoCategory(Key, "硬盘", "物理磁盘规格、健康状态与分区使用情况", groups);
    }

    /// <summary>MSFT_StorageReliabilityCounter 用物理盘序号做 DeviceId，与 Win32_DiskDrive.Index 对齐。</summary>
    private static WmiObject? MatchByDeviceId(List<WmiObject> rows, int? diskIndex) =>
        diskIndex is { } index ? rows.FirstOrDefault(row => row.Str("DeviceId") == index.ToString()) : null;

    /// <summary>SMART 温度是 LHM 之外的第二个来源，标准用户两个都拿不到，由界面隐藏。</summary>
    private static float? SmartTemperature(WmiObject? reliability) =>
        reliability?.Int("Temperature") is { } degrees && degrees > 0 ? degrees : null;

    /// <summary>存储命名空间与 CIMV2 没有共同的键，按型号文本匹配、设备序号兜底。</summary>
    private static WmiObject? MatchStorageRow(List<WmiObject> storageRows, string model, int? driveIndex)
    {
        WmiObject? exact = storageRows.FirstOrDefault(row =>
            string.Equals(row.Str("FriendlyName").Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        WmiObject? contains = storageRows.FirstOrDefault(row =>
        {
            string friendly = row.Str("FriendlyName").Trim();
            return friendly.Length > 0 && model.Contains(friendly, StringComparison.OrdinalIgnoreCase);
        });
        if (contains is not null)
        {
            return contains;
        }

        return driveIndex is { } index
            ? storageRows.FirstOrDefault(row => row.Str("DeviceId") == index.ToString())
            : null;
    }

    /// <summary>MSFT_PhysicalDisk.IsBoot 在部分主机上为空，改用活动分区所在的物理盘序号推导。</summary>
    private static int? ReadBootDiskIndex()
    {
        foreach (var partition in Wmi.Query("SELECT DeviceID FROM Win32_DiskPartition WHERE BootPartition=TRUE"))
        {
            // DeviceID 形如 "Disk #0, Partition #1"，井号后的数字就是 Win32_DiskDrive.Index。
            string deviceId = partition.Str("DeviceID");
            int hash = deviceId.IndexOf('#');
            if (hash < 0)
            {
                continue;
            }

            string digits = new(deviceId.Skip(hash + 1).TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                return index;
            }
        }

        return null;
    }
}

/// <summary>显示器分类页，对应原程序的 get_monitor_info()（screeninfo 部分）。</summary>
internal static class MonitorProvider
{
    public const string Key = "monitor";

    /// <summary>WmiMonitorConnectionParams.VideoOutputTechnology 沿用 DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY 编码。</summary>
    private static readonly Dictionary<int, string> ConnectionType = new()
    {
        [-1] = "其它",
        [0] = "VGA",
        [1] = "S-Video",
        [2] = "复合视频",
        [3] = "分量视频",
        [4] = "DVI",
        [5] = "HDMI",
        [6] = "LVDS",
        [8] = "D4（日本标准）",
        [9] = "SDI",
        [10] = "DisplayPort（外接）",
        [11] = "DisplayPort（内置 eDP）",
        [12] = "UDI（外接）",
        [13] = "UDI（内置）",
        [14] = "模拟电视转接器",
    };

    private static readonly Dictionary<uint, string> Orientation = new()
    {
        [0] = "横向",
        [1] = "旋转 90°",
        [2] = "倒置",
        [3] = "旋转 270°",
    };

    public static InfoCategory Build()
    {
        var identity = ByMonitorKey(Wmi.Query("SELECT * FROM WmiMonitorID", Wmi.Scopes.Wmi));
        var geometry = ByMonitorKey(Wmi.Query("SELECT * FROM WmiMonitorBasicDisplayParams", Wmi.Scopes.Wmi));
        var connections = ByMonitorKey(Wmi.Query("SELECT * FROM WmiMonitorConnectionParams", Wmi.Scopes.Wmi));
        var monitorBounds = Native.EnumerateMonitors()
            .ToDictionary(item => item.DeviceName, StringComparer.OrdinalIgnoreCase);

        var groups = new List<InfoGroup>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = new Native.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
            {
                break;
            }

            // 未接到桌面的适配器（如离屏虚拟显卡）没有可显示的显示器。
            if ((adapter.StateFlags & 1) == 0)
            {
                continue;
            }

            var monitor = new Native.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, Native.EDD_GET_DEVICE_INTERFACE_NAME))
            {
                continue;
            }

            string instanceKey = ToInstanceKey(monitor.DeviceID);
            var edid = identity.GetValueOrDefault(instanceKey);
            var size = geometry.GetValueOrDefault(instanceKey);
            var connection = connections.GetValueOrDefault(instanceKey);
            string monitorCode = DeviceIdSegment(monitor.DeviceID, 1);
            var devMode = new Native.DEVMODE();
            bool hasMode = Native.EnumDisplaySettings(adapter.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref devMode);
            monitorBounds.TryGetValue(adapter.DeviceName, out Native.MonitorGeometry? bounds);

            var fields = new List<InfoField>
            {
                new("名称", EdidText(edid, "UserFriendlyName", FirstNonEmpty(monitorCode, adapter.DeviceString))),
                new("显示器厂商", Vendors.MonitorPnp(EdidText(edid, "ManufacturerName", string.Empty))),
                new("型号", EdidText(edid, "ProductCode", monitorCode)),
                new("显卡输出", adapter.DeviceString),
                new("设备路径", adapter.DeviceName),
                new("分辨率", hasMode ? $"{devMode.dmPelsWidth} x {devMode.dmPelsHeight}" : Formatting.Unknown),
                new("刷新率", hasMode && devMode.dmDisplayFrequency > 0 ? $"{devMode.dmDisplayFrequency} Hz" : Formatting.Unknown),
                new("颜色深度", hasMode && devMode.dmBitsPerPel > 0 ? $"{devMode.dmBitsPerPel} 位" : Formatting.Unknown),
                new("朝向", hasMode ? Orientation.GetValueOrDefault(devMode.dmDisplayOrientation, Formatting.Unknown) : Formatting.Unknown),
                new("坐标", bounds is null
                    ? Formatting.Unknown
                    : $"x={bounds.Bounds.Left}, y={bounds.Bounds.Top}"),
                new("像素尺寸", bounds is null ? Formatting.Unknown : $"{bounds.Bounds.Width} x {bounds.Bounds.Height}"),
                new("工作区", bounds is null
                    ? Formatting.Unknown
                    : $"x={bounds.WorkArea.Left}, y={bounds.WorkArea.Top}, {bounds.WorkArea.Width} x {bounds.WorkArea.Height}"),
                new("主显示器", Formatting.YesNo(bounds?.IsPrimary)),
                new("缩放比例", bounds is null ? Formatting.Unknown : $"{bounds.DpiX * 100 / 96}%"),
                new("物理宽度", size?.Int("MaxHorizontalImageSize") is int widthCm && widthCm > 0 ? $"{widthCm} cm" : Formatting.Unknown),
                new("物理高度", size?.Int("MaxVerticalImageSize") is int heightCm && heightCm > 0 ? $"{heightCm} cm" : Formatting.Unknown),
                new("对角线", DiagonalInches(size)),
                new("像素密度", PixelDensity(size, devMode)),
                new("接口类型", connection?.Int("VideoOutputTechnology") is { } technology
                    ? Formatting.Map(technology, ConnectionType)
                    : Formatting.Unknown),
                new("出厂时间", ManufactureDate(edid)),
                new("序列号", EdidText(edid, "SerialNumberID", string.Empty)),
            };
            groups.Add(new InfoGroup($"显示器 {groups.Count + 1}", fields));
        }

        return new InfoCategory(Key, "显示器", "分辨率、坐标、物理尺寸与缩放信息", groups);
    }

    /// <summary>
    /// root\wmi 的键形如 "DISPLAY\XMIF001\5&amp;37A4FA18&amp;0&amp;UID4352_0"，
    /// EnumDisplayDevices 的接口名形如 "\\?\DISPLAY#XMIF001#5&amp;37a4fa18&amp;0&amp;UID4352#{guid}"，
    /// 两边都归一到 "&lt;序列段&gt;" 才能对齐。
    /// </summary>
    private static Dictionary<string, WmiObject> ByMonitorKey(IEnumerable<WmiObject> rows)
    {
        var map = new Dictionary<string, WmiObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string key = TrimInstanceSuffix(row.Str("InstanceName"));
            if (key.Length > 0)
            {
                map[key] = row;
            }
        }

        return map;
    }

    private static string TrimInstanceSuffix(string instanceName)
    {
        string tail = instanceName[(instanceName.LastIndexOf('\\') + 1)..];
        int underscore = tail.LastIndexOf('_');
        if (underscore > 0 && tail[(underscore + 1)..].All(char.IsAsciiDigit))
        {
            tail = tail[..underscore];
        }

        return tail;
    }

    private static string ToInstanceKey(string deviceInterfaceId) =>
        DeviceIdSegment(deviceInterfaceId, 2).ToUpperInvariant();

    private static string DiagonalInches(WmiObject? size)
    {
        int width = size?.Int("MaxHorizontalImageSize") ?? 0;
        int height = size?.Int("MaxVerticalImageSize") ?? 0;
        if (width <= 0 || height <= 0)
        {
            return Formatting.Unknown;
        }

        double inches = Math.Sqrt((width * width) + (height * height)) / 2.54;
        return string.Create(CultureInfo.CurrentCulture, $"{inches:0.0} 英寸");
    }

    private static string PixelDensity(WmiObject? size, Native.DEVMODE devMode)
    {
        int widthCm = size?.Int("MaxHorizontalImageSize") ?? 0;
        int heightCm = size?.Int("MaxVerticalImageSize") ?? 0;
        if (widthCm <= 0 || heightCm <= 0 || devMode.dmPelsWidth == 0)
        {
            return Formatting.Unknown;
        }

        double horizontal = devMode.dmPelsWidth / (widthCm / 2.54);
        double vertical = devMode.dmPelsHeight / (heightCm / 2.54);
        return string.Create(CultureInfo.CurrentCulture, $"{Math.Round((horizontal + vertical) / 2):0} PPI");
    }

    private static string ManufactureDate(WmiObject? edid)
    {
        int? week = edid?.Int("WeekOfManufacture");
        int? year = edid?.Int("YearOfManufacture");
        if (year is null or 0)
        {
            return Formatting.Unknown;
        }

        return week is { } w && w > 0 ? $"{year} 年第 {w} 周" : year.Value.ToString();
    }

    /// <summary>
    /// EDID 里的厂商 ID 是 "SKG" 这样的三字符 ASCII 码；型号缺失时退回设备路径里的 SKG2716 之类的标识。
    /// </summary>
    private static string EdidText(WmiObject? edid, string property, string fallback) =>
        FirstNonEmpty(edid is null ? string.Empty : Formatting.EdidText(edid.ByteArray(property)), fallback);

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => value.Trim().Length > 0) ?? string.Empty;

    /// <summary>取 "\\?\DISPLAY#SKG2716#5&amp;..." 中以 # 分隔的第 index 段。</summary>
    private static string DeviceIdSegment(string deviceInterfaceId, int index)
    {
        string[] parts = deviceInterfaceId.Split('#', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > index ? parts[index] : string.Empty;
    }
}
