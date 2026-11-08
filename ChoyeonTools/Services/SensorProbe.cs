using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace ChoyeonTools.Services;

/// <summary>
/// 温度、功耗这类厂商传感器由 LibreHardwareMonitor 提供，WMI 里没有对应字段。
/// 这些探针要走驱动层，通常需要管理员权限；初始化或读取失败时一律返回 null，
/// 界面用 Unavailable 说明原因，WMI 能拿到的字段不受影响。
/// </summary>
internal sealed class SensorProbe
{
    /// <summary>首次采集时等后台枚举硬件的宽限时间；来不及就本轮先用 WMI 数据，后续刷新再补传感器值。</summary>
    private const int FirstOpenGraceMs = 1500;

    public static SensorProbe Current { get; } = new();

    private readonly Lock _gate = new();

    /// <summary>Computer 的构造和 Open 放到后台线程，任何异常都不能波及界面与其余采集。</summary>
    private Computer? _computer;

    private Task? _openTask;

    /// <summary>本轮采集的传感器读数。硬盘温度这类探针每次遍历都要重读 SMART，一次采集只走一遍。</summary>
    private IReadOnlyList<Reading>? _readings;

    private bool _snapshotTaken;

    private volatile bool ready;

    private SensorProbe()
    {
    }

    /// <summary>第三方传感器库的版本，关于页要如实标注数据来源。</summary>
    public static string LibraryVersion { get; } =
        typeof(Computer).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "未知";

    /// <summary>本进程是否以管理员权限运行——决定驱动层探针能不能读到数据。</summary>
    public static bool IsElevated { get; } = ReadElevation();

    /// <summary>取不到传感器值的说明统一以此开头，界面据此把整行隐藏。</summary>
    public const string UnavailablePrefix = "未读到";

    /// <summary>取不到传感器值时的说明。虚拟磁盘这类设备本来就没有温度传感器，所以不能一律归因到权限。</summary>
    public static string Unavailable => IsElevated
        ? $"{UnavailablePrefix}（该硬件不上报该传感器）"
        : $"{UnavailablePrefix}（该硬件不上报，或需管理员权限）";

    /// <summary>最近一轮采集实际读到的传感器个数，用来判断驱动探针到底有没有落地。</summary>
    public int LastReadingCount { get; private set; }

    /// <summary>初始化或读取抛出的异常摘要；关于页展示它，避免"读到 0 个"无从排查。</summary>
    public string? LastError { get; private set; }

    public static string Celsius(float? value) => value is { } degrees ? Formatting.Celsius(degrees) : Unavailable;

    public static string Percent(float? value) => value is { } load ? Formatting.Percent(load) : Unavailable;

    public static string Watts(float? value) => value is { } power ? Formatting.Watts(power) : Unavailable;

    /// <summary>
    /// 后台驱动探针的 Open 是否已经落地（加载成功或失败都算）。
    /// 以管理员身份运行时要现场加载驱动，Open 往往超过首轮采集的等待窗口，
    /// 界面据此在落地后自动补采一次，避免温度一直停在"未读到"。
    /// 失败也算落地，否则补采会无限重试。
    /// 用标志位而不是加锁读取：定时器在界面线程上每秒查询一次，不能卡在采集线程的锁上。
    /// </summary>
    public bool IsReady => ready;

    /// <summary>开始一轮新采集，丢弃上一轮的传感器读数。</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _readings = null;
            _snapshotTaken = false;
        }
    }

    public float? CpuTemperature() => PickPreferred(Snapshot(), "Cpu", "Temperature", ["Tctl", "Tdie", "Package"]);

    public float? CpuPower() => PickPreferred(Snapshot(), "Cpu", "Power", ["Package"]);

    public float? GpuTemperature(string name) => FindByHardware(Snapshot(), "Gpu", name, "Temperature");

    public float? GpuLoad(string name) => FindByHardware(Snapshot(), "Gpu", name, "Load");

    public float? DriveTemperature(string model) => FindByHardware(Snapshot(), "Storage", model, "Temperature");

    private static bool ReadElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private IReadOnlyList<Reading>? Snapshot()
    {
        lock (_gate)
        {
            if (_snapshotTaken)
            {
                return _readings;
            }

            _openTask ??= Task.Run(Open);
        }

        IReadOnlyList<Reading>? readings = null;

        // 等待不能持锁进行：后台 Open 完成时也要拿同一把锁。
        // 超时也不代表出错，硬件可能还在枚举，下一轮采集就能拿到。
        if (WaitForOpen())
        {
            lock (_gate)
            {
                readings = Collect();
            }
        }

        lock (_gate)
        {
            _readings = readings;
            LastReadingCount = readings?.Count ?? 0;
            _snapshotTaken = true;
            return _readings;
        }
    }

    private bool WaitForOpen()
    {
        Task opener;
        lock (_gate)
        {
            opener = _openTask!;
        }

        try
        {
            return opener.Wait(FirstOpenGraceMs) && opener.IsCompletedSuccessfully;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private IReadOnlyList<Reading>? Collect()
    {
        if (_computer is null)
        {
            return null;
        }

        try
        {
            // 0.9.7 的 Computer.Accept(IVisitor) 不再遍历硬件（实测访问 0 个），直接枚举 Hardware 才读得到传感器。
            var readings = new List<Reading>();
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectHardware(hardware, readings);
            }

            return readings;
        }
        catch (Exception ex)
        {
            // 个别硬件探针会在更新时抛异常，宁可不显示也不能拖垮整次采集。
            LastError = $"读取失败 {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static void CollectHardware(IHardware hardware, List<Reading> readings)
    {
        hardware.Update();
        string hardwareType = hardware.HardwareType.ToString();
        string hardwareName = hardware.Name;
        foreach (ISensor sensor in hardware.Sensors)
        {
            readings.Add(new(hardwareType, hardwareName, sensor.SensorType.ToString(), sensor.Name, sensor.Value));
        }

        foreach (IHardware sub in hardware.SubHardware)
        {
            CollectHardware(sub, readings);
        }
    }

    private void Open()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsStorageEnabled = true,
            };
            computer.Open();
            lock (_gate)
            {
                _computer = computer;
            }

            // 退出时释放驱动探针，否则服务会一直挂到下次重启。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();
        }
        catch (Exception ex)
        {
            LastError = $"初始化失败 {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            // 无论有没有加载成功都算落地，界面只在落地后补采一次。
            ready = true;
        }
    }

    private void Close()
    {
        lock (_gate)
        {
            try
            {
                _computer?.Close();
            }
            catch (Exception)
            {
                // 关闭探针失败不影响已有读数，句柄随进程退出回收。
            }
        }
    }

    private static float? FindByHardware(IReadOnlyList<Reading>? readings, string hardwareType, string name, string sensorType)
    {
        if (readings is null || name.Length == 0)
        {
            return null;
        }

        foreach (Reading reading in readings)
        {
            if (MatchesType(reading.HardwareType, hardwareType)
                && reading.SensorType == sensorType
                && MatchesModel(reading.Hardware, name)
                && reading.Value is { } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>同一类传感器常有多个（每核一个），按名称优先级取；都不命中时取最大值，整机温度更接近热点。</summary>
    private static float? PickPreferred(IReadOnlyList<Reading>? readings, string hardwareType, string sensorType, string[] preferred)
    {
        if (readings is null)
        {
            return null;
        }

        float? highest = null;
        foreach (Reading reading in readings)
        {
            if (!MatchesType(reading.HardwareType, hardwareType) || reading.SensorType != sensorType || reading.Value is not { } value)
            {
                continue;
            }

            foreach (string keyword in preferred)
            {
                if (reading.Sensor.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            if (highest is null || value > highest.Value)
            {
                highest = value;
            }
        }

        return highest;
    }

    /// <summary>LibreHardwareMonitor 把显卡细分成 GpuNvidia/GpuAmd/GpuIntel，核心温度又挂在 CpuCore 上，所以按前缀归类。</summary>
    private static bool MatchesType(string hardwareType, string wanted) =>
        hardwareType.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>WMI 的 Model 与传感器里的硬件名经常只差空格或厂商标题，按包含关系对齐。</summary>
    private static bool MatchesModel(string left, string right)
    {
        string a = Normalize(left);
        string b = Normalize(right);
        return a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));

        static string Normalize(string value) => value.Replace("  ", " ").Trim().ToUpperInvariant();
    }

    private sealed record Reading(string HardwareType, string Hardware, string SensorType, string Sensor, float? Value);
}
