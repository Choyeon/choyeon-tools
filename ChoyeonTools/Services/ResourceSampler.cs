using System.Threading;
using Microsoft.Win32;

namespace ChoyeonTools.Services;

/// <summary>
/// 总览页的实时资源采样，取数口径对齐任务管理器"性能"页：
/// CPU 与内存走原生系统调用，磁盘和网卡读 WMI 的格式化性能计数器，N 卡用 nvidia-smi。
/// 采样固定在后台线程一秒一轮，界面线程只负责把结果写进卡片，避免 WMI 的偶发慢查询卡住渲染。
/// </summary>
internal sealed class ResourceSampler
{
    /// <summary>曲线窗口长度，一秒一个点，和任务管理器滚动的十分钟曲线取同样的观感。</summary>
    public const int HistoryCapacity = 60;

    /// <summary>nvidia-smi 一次要几十到几百毫秒，两秒取一次就够画曲线。</summary>
    private const int GpuEveryNTicks = 2;

    /// <summary>介质类型与网卡连接名基本不变，半分钟刷新一次即可。</summary>
    private const int StaticInfoEveryNTicks = 30;

    private readonly LiveMetrics.CpuUsageSampler cpuSampler = new();

    private Action<Reading>? onReading;
    private Timer? timer;
    private int tickCount;
    private int running;

    private IReadOnlyList<GpuRow> gpus = [];
    private Dictionary<string, string> diskMediums = new();
    private Dictionary<string, NetworkIdentity> networkIdentities = new();

    public sealed record CpuRow(double? Percent, int? Megahertz);

    public sealed record MemoryRow(ulong TotalBytes, ulong UsedBytes, double UsedPercent);

    public sealed record DiskRow(string Key, string Title, string Medium, double ActivePercent);

    public sealed record NetworkRow(string Key, string Title, string Adapter, double TotalBytesPerSecond, ulong ReceivedBytesPerSecond, ulong SentBytesPerSecond);

    public sealed record GpuRow(string Key, string Title, string Name, double Percent, float? TemperatureCelsius);

    public sealed record Reading(
        CpuRow Cpu,
        MemoryRow? Memory,
        IReadOnlyList<DiskRow> Disks,
        IReadOnlyList<NetworkRow> Networks,
        IReadOnlyList<GpuRow> Gpus,
        TimeSpan Uptime);

    private sealed record NetworkIdentity(string Title, bool Connected);

    public void Start(Action<Reading> callback)
    {
        onReading = callback;
        timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    private void Tick()
    {
        // 上一轮还没跑完就跳过这一秒，后台线程不会被慢查询堆起来。
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            tickCount++;
            if (tickCount % GpuEveryNTicks == 0)
            {
                gpus = ReadGpus();
            }

            if (tickCount % StaticInfoEveryNTicks == 1)
            {
                diskMediums = ReadDiskMediums();
                networkIdentities = ReadNetworkIdentities();
            }

            Reading reading = new(
                new CpuRow(cpuSampler.Sample(), ReadCpuMegahertz()),
                ToMemoryRow(LiveMetrics.ReadMemory()),
                ReadDisks(),
                ReadNetworks(),
                gpus,
                LiveMetrics.Uptime);
            onReading?.Invoke(reading);
        }
        catch (Exception)
        {
            // 采样线程不能因为一次查询失败就停掉，界面继续显示上一轮的曲线。
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private static MemoryRow? ToMemoryRow(LiveMetrics.MemoryUsage? usage) => usage is { } value
        ? new MemoryRow(value.TotalBytes, value.TotalBytes - value.AvailableBytes, value.UsedPercent)
        : null;

    /// <summary>注册表里的 ~MHz 是当前核心频率，读一次只要几微秒，比每秒查 WMI 便宜得多。</summary>
    private static int? ReadCpuMegahertz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") as int?;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private List<DiskRow> ReadDisks()
    {
        var rows = new List<DiskRow>();
        foreach (var counter in Wmi.Query("SELECT Name, PercentDiskTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk"))
        {
            string instance = counter.Str("Name");
            int spaceAt = instance.IndexOf(' ');
            if (instance.Length == 0 || instance.StartsWith("_", StringComparison.Ordinal) || spaceAt <= 0)
            {
                continue;
            }

            string index = instance[..spaceAt];
            string volumes = instance[(spaceAt + 1)..].Replace(" ", " / ");
            rows.Add(new DiskRow(
                $"disk:{index}",
                volumes.Length > 0 ? $"磁盘 {index}（{volumes}）" : $"磁盘 {index}",
                diskMediums.GetValueOrDefault(index, string.Empty),
                Math.Clamp(counter.Num("PercentDiskTime") ?? 0, 0, 100)));
        }

        return rows;
    }

    /// <summary>物理盘的介质与总线基本不变，按 MSFT_PhysicalDisk.DeviceId 对齐计数器实例序号。</summary>
    private static Dictionary<string, string> ReadDiskMediums()
    {
        var mediums = new Dictionary<string, string>();
        foreach (var disk in Wmi.Query("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk", Wmi.Scopes.Storage))
        {
            string id = disk.Str("DeviceId");
            string? media = Formatting.Map(disk.Int("MediaType"), Formatting.StorageMediaType);
            string? bus = Formatting.Map(disk.Int("BusType"), Formatting.StorageBusType);
            if (id.Length > 0 && media.Length > 0)
            {
                mediums[id] = bus.Length > 0 ? $"{media}（{bus}）" : media;
            }
        }

        return mediums;
    }

    private List<NetworkRow> ReadNetworks()
    {
        var rows = new List<NetworkRow>();
        foreach (var counter in Wmi.Query("SELECT Name, BytesReceivedPersec, BytesSentPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface"))
        {
            string adapter = counter.Str("Name");
            if (!networkIdentities.TryGetValue(adapter, out NetworkIdentity? identity) || !identity.Connected)
            {
                continue;
            }

            ulong received = (ulong)Math.Max(0, counter.Num("BytesReceivedPersec") ?? 0);
            ulong sent = (ulong)Math.Max(0, counter.Num("BytesSentPersec") ?? 0);
            rows.Add(new NetworkRow(
                $"net:{adapter}",
                identity.Title.Length > 0 ? identity.Title : adapter,
                adapter,
                received + sent,
                received,
                sent));
        }

        return rows;
    }

    /// <summary>计数器实例名是网卡描述，界面上要的是"WLAN / 以太网"这类连接名，所以提前建好对照表。</summary>
    private static Dictionary<string, NetworkIdentity> ReadNetworkIdentities()
    {
        var identities = new Dictionary<string, NetworkIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in Wmi.Query("SELECT Name, Description, NetConnectionID, NetConnectionStatus, PhysicalAdapter FROM Win32_NetworkAdapter"))
        {
            if (adapter.Bool("PhysicalAdapter") != true)
            {
                continue;
            }

            var identity = new NetworkIdentity(adapter.Str("NetConnectionID"), adapter.Int("NetConnectionStatus") == 2);
            foreach (string key in new[] { adapter.Str("Name"), adapter.Str("Description") })
            {
                if (key.Length > 0)
                {
                    identities[key] = identity;
                }
            }
        }

        return identities;
    }

    /// <summary>nvidia-smi 按适配器顺序返回，任务管理器的 GPU 序号也是同一套顺序。</summary>
    private static IReadOnlyList<GpuRow> ReadGpus()
    {
        var rows = new List<GpuRow>();
        IReadOnlyList<VendorCli.NvidiaGpu> gpus = VendorCli.QueryNvidia();
        for (int i = 0; i < gpus.Count; i++)
        {
            VendorCli.NvidiaGpu gpu = gpus[i];
            rows.Add(new GpuRow(
                $"gpu:{gpu.Name}",
                $"GPU {i}",
                gpu.Name,
                gpu.UtilizationPercent < 0 ? 0 : Math.Clamp(gpu.UtilizationPercent, 0, 100),
                gpu.TemperatureC < 0 ? null : (float)gpu.TemperatureC));
        }

        return rows;
    }
}
