using System.Diagnostics;
using System.Globalization;

namespace ChoyeonTools.Services;

/// <summary>设备对应的即插即用驱动信息（驱动版本、日期、INF）。</summary>
internal sealed class PnpDrivers
{
    public sealed record Entry(string? DriverVersion, DateTime? DriverDate, string? Provider, string? InfName);

    private readonly Dictionary<string, Entry> _byDeviceId;

    private PnpDrivers(Dictionary<string, Entry> byDeviceId) => _byDeviceId = byDeviceId;

    public static PnpDrivers Load()
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Wmi.Query("SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName FROM Win32_PnPSignedDriver"))
        {
            string deviceId = row.Str("DeviceID");
            if (deviceId.Length > 0)
            {
                map[deviceId] = new Entry(row.StrOrNull("DriverVersion"), row.Time("DriverDate"), row.StrOrNull("DriverProviderName"), row.StrOrNull("InfName"));
            }
        }

        return new PnpDrivers(map);
    }

    public Entry? Find(string? deviceId) => deviceId is not null && _byDeviceId.TryGetValue(deviceId, out var entry) ? entry : null;
}

/// <summary>厂商命令行工具信息。NVIDIA 的温度/显存/占用率只有 nvidia-smi 能给出。</summary>
internal static class VendorCli
{
    public sealed record NvidiaGpu(
        string Name,
        string DriverVersion,
        long MemoryTotalMb,
        long MemoryUsedMb,
        long MemoryFreeMb,
        long TemperatureC,
        long UtilizationPercent);

    private static readonly string[] CandidatePaths =
    [
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\Shared\NVSMI\nvidia-smi.exe",
    ];

    public static IReadOnlyList<NvidiaGpu> QueryNvidia()
    {
        string? executable = FindNvidiaSmi();
        if (executable is null)
        {
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--query-gpu=name,driver_version,memory.total,memory.used,memory.free,temperature.gpu,utilization.gpu --format=csv,noheader,nounits",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            // nvidia-smi 在无响应驱动下可能长时间挂起，超时后放弃这一路数据。
            if (!process.WaitForExit(4000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            string output = process.StandardOutput.ReadToEnd();
            return ParseNvidiaCsv(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return [];
        }
    }

    private static string? FindNvidiaSmi()
    {
        foreach (string path in CandidatePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim(), "nvidia-smi.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<NvidiaGpu> ParseNvidiaCsv(string output)
    {
        var result = new List<NvidiaGpu>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split(',').Select(c => c.Trim()).ToArray();
            if (columns.Length < 7)
            {
                continue;
            }

            result.Add(new NvidiaGpu(
                columns[0],
                columns[1],
                ParseLong(columns[2]),
                ParseLong(columns[3]),
                ParseLong(columns[4]),
                ParseLong(columns[5]),
                ParseLong(columns[6])));
        }

        return result;
    }

    // 温度、占用率等字段在不同驱动下会带小数（例如 "37.83"），按数值解析后取整。
    private static long ParseLong(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (long)Math.Round(value)
            : -1;
}
