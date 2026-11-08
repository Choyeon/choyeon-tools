using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using ChoyeonTools.Models;
using ChoyeonTools.Services;

namespace ChoyeonTools.ViewModels;

/// <summary>
/// 主界面视图模型：负责采集编排、分类导航、设置页、实时指标与刷新/复制/导出命令。
/// 界面绑定的是常驻的 <see cref="Groups"/> 与 <see cref="Metrics"/>，刷新只改内容不换集合实例，
/// 这样定时刷新就不会重建整页视觉树。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // 曲线配色对齐任务管理器"性能"列表：CPU 蓝、内存紫、磁盘绿、网络粉、GPU 紫罗兰。
    private const string CpuAccent = "#4CC2FF";
    private const string MemoryAccent = "#8A7DF6";
    private const string DiskAccent = "#6CCB5F";
    private const string NetworkAccent = "#F25894";
    private const string GpuAccent = "#B07AE6";

    private readonly SystemInfoService service = new();
    private readonly ResourceSampler sampler = new();
    private readonly MetricCard cpuCard = new("cpu", "CPU", CpuAccent);
    private readonly MetricCard memoryCard = new("memory", "内存", MemoryAccent);
    private readonly MetricCard uptimeCard = new("uptime", "运行时长", showGraph: false);
    private readonly Dictionary<string, MetricCard> diskCards = new();
    private readonly Dictionary<string, MetricCard> networkCards = new();
    private readonly Dictionary<string, MetricCard> gpuCards = new();
    private DispatcherQueueTimer? tickTimer;
    private int tickCount;
    private string selectedKey = SystemInfoService.OverviewKey;
    private bool isRefreshing;

    [ObservableProperty]
    public partial SystemSnapshot? Snapshot { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview))]
    [NotifyPropertyChangedFor(nameof(ShowsGroups))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedDescription))]
    public partial InfoCategory? Selected { get; private set; }

    /// <summary>总览页的简要汇总，随整轮采集重建。</summary>
    [ObservableProperty]
    public partial OverviewSummary? Summary { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "正在采集系统信息…";

    [ObservableProperty]
    public partial string Theme { get; set; } = AppSettings.SystemTheme;

    [ObservableProperty]
    public partial int AutoRefreshSeconds { get; set; } = 30;

    /// <summary>是否停在设置页。分类选择保持不变，离开设置后回到原来那一页。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDataView))]
    [NotifyPropertyChangedFor(nameof(IsOverview))]
    [NotifyPropertyChangedFor(nameof(ShowsGroups))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedDescription))]
    public partial bool IsSettings { get; private set; }

    public bool IsDataView => !IsSettings;

    public bool IsOverview => !IsSettings && Selected?.Key == SystemInfoService.OverviewKey;

    /// <summary>总览页用简要汇总代替完整卡片清单；复制与导出仍然走完整分组。</summary>
    public bool ShowsGroups => !IsSettings && !IsOverview;

    public bool HasNoSelection => !IsSettings && Selected is null;

    public string SelectedTitle => IsSettings ? "设置" : Selected?.Title ?? "Choyeon Tools";

    public string SelectedDescription => IsSettings
        ? "界面偏好、数据采集与本程序信息"
        : Selected?.Description ?? string.Empty;

    /// <summary>
    /// 总览页的资源条目。磁盘、网卡、GPU 按实际设备增减，
    /// 卡片实例按 Key 复用，所以设备列表变化不会把已经攒下的曲线清空。
    /// </summary>
    public ObservableCollection<MetricCard> Metrics { get; } = [];

    /// <summary>设置页"关于"分组，内容固定，只有传感器那一行随采集更新。</summary>
    public GroupCard AboutCard { get; }

    private readonly FieldRow sensorRow = new("传感器");

    public MainViewModel() => AboutCard = BuildAboutCard();

    /// <summary>当前页面的卡片集合，实例在刷新之间保持稳定。</summary>
    public ObservableCollection<GroupCard> Groups { get; } = [];

    /// <summary>只读取偏好，供界面在采集前回填下拉框与主题。</summary>
    public void LoadSettings()
    {
        var settings = AppSettings.Load();
        Theme = settings.Theme;
        AutoRefreshSeconds = settings.AutoRefreshSeconds;
    }

    public async Task InitializeAsync()
    {
        sampler.Start(OnSampled);
        await RefreshAsync();
        StartTicker();
    }

    public void EnterSettings() => IsSettings = true;

    public void Select(string? key)
    {
        if (key is null)
        {
            return;
        }

        IsSettings = false;
        selectedKey = key;
        Selected = Snapshot?.Find(key);
        SyncGroups(Selected?.Groups);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        IsBusy = true;
        StatusText = "正在采集系统信息…";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Snapshot = await service.CollectAsync();
            Summary = OverviewSummary.Build(Snapshot);
            Selected = Snapshot.Find(selectedKey);
            SyncGroups(Selected?.Groups);
            StatusText = $"上次更新 {Snapshot.CollectedAt.ToLocalTime():HH:mm:ss}（用时 {stopwatch.Elapsed.TotalSeconds:0.0} 秒）";
        }
        catch (Exception ex)
        {
            StatusText = $"采集失败：{ex.Message}";
        }
        finally
        {
            isRefreshing = false;
            IsBusy = false;
            sensorsPending = !SensorProbe.Current.IsReady;
            sensorRow.UpdateValue(SensorStatusText());
        }
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ExportService.ToPlainText(Selected));

        const uint ClipbrdCantOpen = 0x800401D0;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                // 无包标识的进程退出后剪贴板内容会被回收，显式刷新保持可用。
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                StatusText = $"已复制“{Selected.Title}”到剪贴板";
                return;
            }
            catch (Exception ex) when (attempt < 4 && unchecked((uint)ex.HResult) == ClipbrdCantOpen)
            {
                // 剪贴板常被输入法、远程桌面等短期占用，稍等重试。
                await Task.Delay(120);
            }
            catch (Exception ex)
            {
                StatusText = $"复制失败：{ex.Message}";
                return;
            }
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Snapshot is null)
        {
            return;
        }

        try
        {
            string? path = await ExportService.SaveJsonAsync(Snapshot, App.WindowHandle);
            StatusText = path is null ? "已取消导出" : $"已导出：{path}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    public void SaveSettings()
    {
        var settings = AppSettings.Load();
        settings.Theme = Theme;
        settings.AutoRefreshSeconds = AutoRefreshSeconds;
        settings.Save();
    }

    private void StartTicker()
    {
        tickTimer = App.DispatcherQueue.CreateTimer();
        tickTimer.Interval = TimeSpan.FromSeconds(1);
        tickTimer.IsRepeating = true;
        tickTimer.Tick += (_, _) => OnTick();
        tickTimer.Start();
    }

    /// <summary>驱动探针比首轮采集慢时，等它就绪后自动补采一次。</summary>
    private bool sensorsPending;

    private void OnTick()
    {
        tickCount++;
        if (sensorsPending && SensorProbe.Current.IsReady)
        {
            sensorsPending = false;
            _ = RefreshAsync();
        }
        else if (AutoRefreshSeconds > 0 && tickCount % AutoRefreshSeconds == 0)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// 把新采集的分组写回已有卡片：同名分组复用容器，字段按标签原地改值。
    /// </summary>
    private void SyncGroups(IReadOnlyList<InfoGroup>? source)
    {
        source ??= Array.Empty<InfoGroup>();
        var desired = new List<GroupCard>(source.Count);
        var reused = new HashSet<GroupCard>();

        foreach (InfoGroup group in source)
        {
            GroupCard card = Groups.FirstOrDefault(candidate => !reused.Contains(candidate) && candidate.Title == group.Title)
                ?? new GroupCard(group.Title);
            reused.Add(card);
            card.Sync(group);
            // 全部字段都没读到就没有可展示的内容，整张卡片一起隐藏。
            if (card.Fields.Count > 0)
            {
                desired.Add(card);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            int current = Groups.IndexOf(desired[i]);
            if (current < 0)
            {
                Groups.Insert(i, desired[i]);
            }
            else if (current != i)
            {
                Groups.RemoveAt(current);
                Groups.Insert(i, desired[i]);
            }
        }

        for (int i = Groups.Count - 1; i >= desired.Count; i--)
        {
            Groups.RemoveAt(i);
        }
    }

    /// <summary>采样跑在后台线程，写卡片要回界面线程；界面已经关掉时排队失败，顺手停掉采样。</summary>
    private void OnSampled(ResourceSampler.Reading reading)
    {
        if (!App.DispatcherQueue.TryEnqueue(() => ApplyReading(reading)))
        {
            sampler.Stop();
        }
    }

    /// <summary>
    /// 一轮读数落到固定顺序的条目列表上：CPU、内存、各块磁盘、各张网卡、各张 GPU，最后是不带曲线的运行时长。
    /// 设备条目按 Key 复用卡片实例，所以插拔设备或换盘符不会把已经攒下的曲线清空。
    /// </summary>
    private void ApplyReading(ResourceSampler.Reading reading)
    {
        ResourceSampler.CpuRow cpu = reading.Cpu;
        cpuCard.Update(
            "CPU",
            cpu.Percent is { } percent ? $"{percent:0}%  {Formatting.Gigahertz(cpu.Megahertz)}" : "采集中…",
            string.Empty,
            cpu.Percent);

        if (reading.Memory is { } memory)
        {
            memoryCard.Update(
                "内存",
                Formatting.MemoryUsage(memory.UsedBytes, memory.TotalBytes, memory.UsedPercent),
                string.Empty,
                memory.UsedPercent);
        }

        TimeSpan uptime = reading.Uptime;
        uptimeCard.Update(
            "运行时长",
            Formatting.DurationCompact(uptime),
            $"开机于 {Formatting.Date(DateTime.Now - uptime, "MM-dd HH:mm")}",
            null);

        foreach (ResourceSampler.DiskRow disk in reading.Disks)
        {
            string active = $"{disk.ActivePercent:0}%";
            ApplyDevice(
                diskCards,
                DiskAccent,
                disk.Key,
                disk.Title,
                disk.Medium.Length > 0 ? disk.Medium : active,
                disk.Medium.Length > 0 ? active : string.Empty,
                disk.ActivePercent);
        }

        foreach (ResourceSampler.NetworkRow network in reading.Networks)
        {
            string throughput = Formatting.TrafficPair(network.SentBytesPerSecond, network.ReceivedBytesPerSecond);
            ApplyDevice(
                networkCards,
                NetworkAccent,
                network.Key,
                network.Title,
                network.Adapter,
                throughput,
                network.TotalBytesPerSecond,
                graphMaximum: 0);
        }

        foreach (ResourceSampler.GpuRow gpu in reading.Gpus)
        {
            string load = $"{gpu.Percent:0}%";
            string detail = gpu.TemperatureCelsius is { } celsius ? $"{load}（{Formatting.Celsius(celsius)}）" : load;
            ApplyDevice(gpuCards, GpuAccent, gpu.Key, gpu.Title, gpu.Name, detail, gpu.Percent);
        }

        PruneStale(diskCards, reading.Disks.Select(disk => disk.Key));
        PruneStale(networkCards, reading.Networks.Select(network => network.Key));
        PruneStale(gpuCards, reading.Gpus.Select(gpu => gpu.Key));
        SyncOrder(reading);
    }

    /// <summary>磁盘在介质信息没读到时也要显示占用率，两行文字按缺哪一行来分配。</summary>
    private static void ApplyDevice(
        Dictionary<string, MetricCard> pool,
        string accent,
        string key,
        string title,
        string value,
        string detail,
        double? sample,
        double graphMaximum = 100)
    {
        if (!pool.TryGetValue(key, out MetricCard? card))
        {
            card = new MetricCard(key, title, accent, graphMaximum);
            pool[key] = card;
        }

        card.Update(title, value, detail, sample);
    }

    /// <summary>本轮没出现的设备说明已经拔出，卡片直接撤掉，曲线不再停在旧读数上。</summary>
    private static void PruneStale(Dictionary<string, MetricCard> pool, IEnumerable<string> liveKeys)
    {
        var alive = new HashSet<string>(liveKeys);
        foreach (string key in pool.Keys.Where(key => !alive.Contains(key)).ToList())
        {
            pool.Remove(key);
        }
    }

    private void SyncOrder(ResourceSampler.Reading reading)
    {
        var desired = new List<MetricCard> { cpuCard, memoryCard };
        desired.AddRange(reading.Disks.Select(disk => diskCards[disk.Key]));
        desired.AddRange(reading.Networks.Select(network => networkCards[network.Key]));
        desired.AddRange(reading.Gpus.Select(gpu => gpuCards[gpu.Key]));
        desired.Add(uptimeCard);

        for (int i = 0; i < desired.Count; i++)
        {
            int current = Metrics.IndexOf(desired[i]);
            if (current < 0)
            {
                Metrics.Insert(i, desired[i]);
            }
            else if (current != i)
            {
                Metrics.RemoveAt(current);
                Metrics.Insert(i, desired[i]);
            }
        }

        for (int i = Metrics.Count - 1; i >= desired.Count; i--)
        {
            Metrics.RemoveAt(i);
        }
    }

    private GroupCard BuildAboutCard()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var version = assembly.GetName().Version;
        string displayVersion = version is null ? "开发版" : $"{version.Major}.{version.Minor}.{version.Build}";

        var card = new GroupCard("程序信息");
        List<(string Label, string? Value)> rows =
        [
            ("名称", "Choyeon Tools"),
            ("版本", displayVersion),
            ("作者", "Choyeon"),
            ("联系方式", "Choyeon@foxmail.com"),
            ("许可证", "MIT"),
            ("功能", "查看 CPU、内存、GPU、硬盘、声卡、网络、USB、显示器与系统信息"),
        ];
        AddCatalogRows(rows, "PCI 名称库", HardwareCatalog.Current);
        AddCatalogRows(rows, "USB 名称库", HardwareCatalog.Usb);
        AddNameTableRows(rows, "显示器 PNP 库", NameTable.Pnp);
        AddNameTableRows(rows, "内存 JEDEC 库", NameTable.Jedec);

        foreach ((string label, string? value) in rows)
        {
            if (value is null)
            {
                continue;
            }

            var row = new FieldRow(label);
            row.UpdateValue(value);
            card.Fields.Add(row);
        }

        sensorRow.UpdateValue(SensorStatusText());
        card.Fields.Add(sensorRow);

        return card;
    }

    /// <summary>探针到底读没读到值只有跑起来才知道，关于页按本轮实测数量说明，不把"需要管理员"当成万能解释。</summary>
    private static string SensorStatusText()
    {
        string text = $"LibreHardwareMonitor {SensorProbe.LibraryVersion}（{(SensorProbe.IsElevated ? "已管理员运行" : "未以管理员运行")}，本轮读到 {SensorProbe.Current.LastReadingCount} 个实时值）";
        return SensorProbe.Current.LastError is { } error ? $"{text}；{error}" : text;
    }

    /// <summary>名称库的版权与许可声明随数据一起分发，界面原样展示；库没加载时这些行直接隐藏。</summary>
    private static void AddCatalogRows(List<(string Label, string? Value)> rows, string label, HardwareCatalog catalog)
    {
        if (!catalog.IsLoaded)
        {
            return;
        }

        string scale = catalog.SubsystemCount > 0
            ? $"{catalog.DeviceCount:N0} 个设备，{catalog.SubsystemCount:N0} 个子系统"
            : $"{catalog.DeviceCount:N0} 个设备";
        rows.Add((label, $"{catalog.Source} {catalog.Version}（{scale}）"));
        if (catalog.Attribution.Length > 0)
        {
            rows.Add(($"{label}许可", catalog.Attribution));
        }
    }

    private static void AddNameTableRows(List<(string Label, string? Value)> rows, string label, NameTable table)
    {
        if (!table.IsLoaded)
        {
            return;
        }

        rows.Add((label, $"{table.Source} {table.Version}（{table.Count:N0} 条）"));
        if (table.Attribution.Length > 0)
        {
            rows.Add(($"{label}许可", table.Attribution));
        }
    }
}
