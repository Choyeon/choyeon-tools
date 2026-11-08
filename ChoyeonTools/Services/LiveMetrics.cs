namespace ChoyeonTools.Services;

/// <summary>实时资源占用采集，供总览页的仪表卡片与自动刷新使用。</summary>
internal static class LiveMetrics
{
    public sealed record MemoryUsage(ulong TotalBytes, ulong AvailableBytes, double UsedPercent);

    public static MemoryUsage? ReadMemory()
    {
        var status = Native.MEMORYSTATUSEX.Current();
        if (status.ullTotalPhys == 0)
        {
            return null;
        }

        double used = (double)(status.ullTotalPhys - status.ullAvailPhys) / status.ullTotalPhys * 100;
        return new MemoryUsage(status.ullTotalPhys, status.ullAvailPhys, used);
    }

    /// <summary>从两次采样点之间计算 CPU 占用，供定时刷新复用而不额外等待。</summary>
    internal sealed class CpuUsageSampler
    {
        private long _idle;
        private long _kernel;
        private long _user;
        private bool _primed;

        public double? Sample()
        {
            if (!Native.GetSystemTimes(out long idle, out long kernel, out long user))
            {
                return null;
            }

            double? result = null;
            if (_primed)
            {
                long idleDelta = idle - _idle;
                long busyDelta = (kernel - _kernel) + (user - _user) - idleDelta;
                long total = idleDelta + busyDelta;
                if (total > 0)
                {
                    result = Math.Clamp(busyDelta * 100d / total, 0, 100);
                }
            }

            (_idle, _kernel, _user) = (idle, kernel, user);
            _primed = true;
            return result;
        }
    }

    public sealed record GpuEngineUsage(string AdapterKey, double PeakPercent);

    /// <summary>
    /// 取每个 GPU 上最繁忙引擎的占用率（任务管理器 "GPU" 曲线的近似值）。
    /// 该计数器在部分驱动上不可用，此时返回空集合。
    /// </summary>
    public static IReadOnlyList<GpuEngineUsage> ReadGpuEngineUsage()
    {
        var engines = Wmi.Query("SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
        var perEngine = new Dictionary<(string Adapter, string Engine), double>();

        foreach (var row in engines)
        {
            string adapter = row.Str("Luid");
            string engine = row.Str("EngineType");
            double value = row.Num("UtilizationPercentage") ?? 0;
            perEngine[(adapter, engine)] = perEngine.TryGetValue((adapter, engine), out var existing)
                ? existing + value
                : value;
        }

        return [.. perEngine
            .GroupBy(pair => pair.Key.Adapter)
            .Where(group => group.Key.Length > 0)
            .Select(group => new GpuEngineUsage(group.Key, group.Max(pair => Math.Clamp(pair.Value, 0, 100))))];
    }

    /// <summary>开机时长直接取系统计数器，避免每秒查询 WMI 拖慢界面。</summary>
    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);
}
