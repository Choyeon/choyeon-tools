using System.Text;
using System.Text.Json;
using ChoyeonTools.Models;
using ChoyeonTools.Providers;

namespace ChoyeonTools.Services;

/// <summary>
/// 一次采集的编排入口。分类顺序沿用原程序 Notebook 的标签顺序，总览在最前、关于由界面单独呈现。
/// </summary>
internal sealed class SystemInfoService
{
    public const string OverviewKey = "overview";

    public Task<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Collect(), cancellationToken);

    private static SystemSnapshot Collect()
    {
        var drivers = PnpDrivers.Load();
        var catalog = HardwareCatalog.Current;
        var sensors = SensorProbe.Current;
        sensors.Refresh();
        var categories = new List<InfoCategory>
        {
            Safe(CpuProvider.Key, "CPU", () => CpuProvider.Build(sensors)),
            Safe(MemoryProvider.Key, "内存", () => MemoryProvider.Build()),
            Safe(GpuProvider.Key, "GPU", () => GpuProvider.Build(drivers, catalog, sensors)),
            Safe(DiskProvider.Key, "硬盘", () => DiskProvider.Build(drivers, sensors)),
            Safe(SoundProvider.Key, "声卡", () => SoundProvider.Build(drivers, catalog)),
            Safe(NetworkProvider.Key, "网络", () => NetworkProvider.Build(drivers, catalog)),
            Safe(UsbProvider.Key, "USB", () => UsbProvider.Build(drivers, HardwareCatalog.Usb)),
            Safe(SystemProvider.Key, "系统", () => SystemProvider.Build(catalog)),
            Safe(MonitorProvider.Key, "显示器", () => MonitorProvider.Build()),
        };

        var withOverview = new List<InfoCategory>(categories.Count + 1)
        {
            new(
                OverviewKey,
                "总览",
                "全部类别的信息汇总",
                [.. categories.SelectMany(category =>
                    category.Groups.Select(group => new InfoGroup($"{category.Title} · {group.Title}", group.Fields)))]),
        };
        withOverview.AddRange(categories);

        return new SystemSnapshot(DateTimeOffset.Now, withOverview);
    }

    /// <summary>单个分类的异常不影响其它页面，界面上保留分类并说明失败原因。</summary>
    private static InfoCategory Safe(string key, string title, Func<InfoCategory> build)
    {
        try
        {
            return build();
        }
        catch (Exception ex)
        {
            return new InfoCategory(key, title, "该分类采集失败", [new InfoGroup(title, [new("采集失败", ex.Message)])]);
        }
    }
}

/// <summary>把采集结果转成文本或 JSON，对应原程序只读文本框的内容与导出能力。</summary>
internal static class ExportService
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToPlainText(InfoCategory category)
    {
        var builder = new StringBuilder();
        foreach (InfoGroup group in category.Groups)
        {
            builder.Append("===").Append(group.Title).AppendLine("===");
            foreach (InfoField field in group.Fields)
            {
                builder.Append(field.Label).Append(": ").AppendLine(field.Value);
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string ToPlainText(SystemSnapshot snapshot) =>
        string.Join(Environment.NewLine + Environment.NewLine, snapshot.Categories.Select(ToPlainText));

    public static string ToJson(SystemSnapshot snapshot) =>
        JsonSerializer.Serialize(new
        {
            collectedAt = snapshot.CollectedAt,
            computer = Environment.MachineName,
            categories = snapshot.Categories,
        }, Indented);

    /// <summary>
    /// 弹出保存对话框导出 JSON。返回 null 表示用户取消；对话框本身不可用（无窗口句柄、被组策略禁用）时直接写入"下载"目录。
    /// </summary>
    public static async Task<string?> SaveJsonAsync(SystemSnapshot snapshot, nint windowHandle)
    {
        string fileName = $"系统信息_{DateTime.Now:yyyyMMdd-HHmmss}.json";
        string content = ToJson(snapshot);

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.FileTypeChoices.Add("JSON 文件", [".json"]);
            picker.SuggestedFileName = fileName;

            Windows.Storage.StorageFile? target = await picker.PickSaveFileAsync();
            if (target is null)
            {
                return null;
            }

            await Windows.Storage.FileIO.WriteTextAsync(target, content);
            return target.Path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, content);
            return path;
        }
    }
}

/// <summary>界面偏好（主题与自动刷新间隔），保存在本地设置文件。</summary>
internal sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public int AutoRefreshSeconds { get; set; }

    public const string LightTheme = "Light";
    public const string DarkTheme = "Dark";
    public const string SystemTheme = "System";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChoyeonTools",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置写不进去不影响使用，下次启动回到默认值。
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
