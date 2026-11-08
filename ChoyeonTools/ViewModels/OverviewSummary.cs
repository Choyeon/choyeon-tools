using ChoyeonTools.Models;
using ChoyeonTools.Providers;
using ChoyeonTools.Services;

namespace ChoyeonTools.ViewModels;

/// <summary>总览里的一台设备：一行简要文字，以及点进去要落的分类页与卡片。</summary>
public sealed record SummaryDevice(string Text, string CategoryKey, string GroupTitle);

/// <summary>详细信息里的一类设备，例如"显卡"下的两张卡。</summary>
public sealed record SummaryRow(string Label, IReadOnlyList<SummaryDevice> Devices);

/// <summary>
/// 图吧/鲁大师式的总览汇总：型号与系统两张头卡，加一份可点击跳转的设备清单。
/// 文字全部取自已采集的分类页字段，总览与详情页口径一致，也不为汇总重复查询 WMI。
/// </summary>
public sealed record OverviewSummary(string Model, string System, IReadOnlyList<SummaryRow> Rows)
{
    public static OverviewSummary Build(SystemSnapshot snapshot)
    {
        InfoCategory? system = snapshot.Find(SystemProvider.Key);
        string boardVendor = Brand(Field(system, "BIOS 与主板", "主板制造商"));
        string boardModel = Field(system, "BIOS 与主板", "主板型号");
        string model = Join(boardVendor, boardModel, Field(system, "计算机", "机型类别"));
        if (model.Length == 0)
        {
            model = Join(Brand(Field(system, "计算机", "制造商")), Field(system, "计算机", "型号"));
        }

        string os = Field(system, "操作系统", "系统家族")
            .Replace("Microsoft Windows 11", "Win11")
            .Replace("Microsoft Windows 10", "Win10");
        string systemText = Join(
            os,
            CompactVersion(Field(system, "操作系统", "版本")),
            Field(system, "操作系统", "体系结构").Replace(" 位", "位"));

        var rows = new List<SummaryRow>();

        var cpus = new List<SummaryDevice>();
        foreach (InfoGroup group in Groups(snapshot.Find(CpuProvider.Key)))
        {
            if (group.Title == "汇总")
            {
                continue;
            }

            string cores = Field(group, "核心数");
            cpus.Add(new SummaryDevice(
                Join(Field(group, "名称"), cores.Length > 0 ? $"{cores} 核" : string.Empty),
                CpuProvider.Key,
                group.Title));
        }

        AddRow(rows, "处理器", cpus);
        AddRow(rows, "主板", [new SummaryDevice(Join(boardVendor, boardModel), SystemProvider.Key, "BIOS 与主板")]);

        List<InfoGroup> sticks = Groups(snapshot.Find(MemoryProvider.Key)).Where(group => group.Title != "汇总").ToList();
        string capacities = string.Join(" + ", sticks.Select(stick => Field(stick, "容量")).Where(value => value.Length > 0));
        // 已安装内存取系统页的 SMBIOS 口径（64 GB），括号里补系统可用容量（61.6 GB），与 Windows"关于"页一致。
        string usable = Field(snapshot.Find(MemoryProvider.Key), "汇总", "系统可用容量");
        string usableText = usable.Length > 0 && usable != Formatting.Unknown ? $"可用 {usable}" : string.Empty;
        string memoryDetail = capacities.Length > 0 && usableText.Length > 0
            ? $"{capacities}，{usableText}"
            : Join(capacities, usableText);
        string brands = string.Join(" / ", sticks.Select(stick => Brand(Field(stick, "制造商")))
            .Where(value => value.Length > 0).Distinct());
        string memoryHead = Join(
            Field(system, "计算机", "已安装内存"),
            sticks.Count > 0 ? Field(sticks[0], "类型") : string.Empty,
            sticks.Count > 0 ? Field(sticks[0], "实际运行速度").Replace(" MHz", "MHz") : string.Empty,
            brands);
        AddRow(rows, "内存",
        [
            new SummaryDevice(memoryHead + Wrap(memoryDetail, "（", "）"), MemoryProvider.Key, "汇总"),
        ]);

        var gpus = new List<SummaryDevice>();
        foreach (InfoGroup group in Groups(snapshot.Find(GpuProvider.Key)))
        {
            string vendor = Brand(Field(group, "板卡厂商"));
            if (vendor.Length == 0)
            {
                vendor = Brand(Field(group, "芯片厂商"));
            }

            string vram = Field(group, "显存容量");
            string spec = vram.Length > 0 && vendor.Length > 0 ? $"{vram} / {vendor}" : Join(vram, vendor);
            gpus.Add(new SummaryDevice(
                Join(Field(group, "名称"), Wrap(spec, "（", "）")),
                GpuProvider.Key,
                group.Title));
        }

        AddRow(rows, "显卡", gpus);

        var monitors = new List<SummaryDevice>();
        foreach (InfoGroup group in Groups(snapshot.Find(MonitorProvider.Key)))
        {
            monitors.Add(new SummaryDevice(
                Join(
                    Field(group, "名称"),
                    Wrap(Join(Brand(Field(group, "显示器厂商")), Field(group, "型号")), "[", "]"),
                    Wrap(Field(group, "对角线"), "（", "）")),
                MonitorProvider.Key,
                group.Title));
        }

        AddRow(rows, "显示器", monitors);

        var disks = new List<SummaryDevice>();
        foreach (InfoGroup group in Groups(snapshot.Find(DiskProvider.Key)))
        {
            if (group.Title.StartsWith("驱动器 ", StringComparison.Ordinal))
            {
                continue;
            }

            disks.Add(new SummaryDevice(Join(group.Title, Wrap(Field(group, "总大小"), "（", "）")), DiskProvider.Key, group.Title));
        }

        AddRow(rows, "磁盘", disks);

        var sounds = new List<SummaryDevice>();
        foreach (InfoGroup group in Groups(snapshot.Find(SoundProvider.Key)))
        {
            string name = Field(group, "名称");
            sounds.Add(new SummaryDevice(name.Length > 0 ? name : group.Title, SoundProvider.Key, group.Title));
        }

        AddRow(rows, "声卡", sounds);

        return new OverviewSummary(model, systemText, rows);
    }

    private static IEnumerable<InfoGroup> Groups(InfoCategory? category) => category?.Groups ?? [];

    private static string Field(InfoCategory? category, string groupTitle, string label) =>
        Field(Groups(category).FirstOrDefault(group => group.Title == groupTitle), label);

    private static string Field(InfoGroup? group, string label)
    {
        InfoField? field = group?.Fields.FirstOrDefault(candidate => candidate.Label == label);
        return field is null || field.IsMissing ? string.Empty : field.Value;
    }

    private static string Join(params string[] parts) => string.Join(" ", parts.Where(part => part.Length > 0));

    private static string Wrap(string value, string open, string close) => value.Length == 0 ? string.Empty : open + value + close;

    /// <summary>详情页的厂商写成"技嘉（Gigabyte Technology Co., Ltd.）"，汇总只要括号前的品牌。</summary>
    private static string Brand(string value)
    {
        int open = value.IndexOf('（');
        return open > 0 ? value[..open].Trim() : value;
    }

    /// <summary>详情页版本是"10.0.26300.9711（内部版本 26300, 26H2）"，汇总只留 26H2 这样的发行号。</summary>
    private static string CompactVersion(string version)
    {
        const string marker = "（内部版本 ";
        int open = version.IndexOf(marker, StringComparison.Ordinal);
        if (open < 0)
        {
            return version;
        }

        string inner = version[(open + marker.Length)..].TrimEnd('）');
        string[] parts = inner.Split(", ");
        return parts.Length > 1 ? parts[1] : parts[0];
    }

    private static void AddRow(List<SummaryRow> rows, string label, IEnumerable<SummaryDevice> devices)
    {
        List<SummaryDevice> visible = devices.Where(device => device.Text.Length > 0).ToList();
        if (visible.Count > 0)
        {
            rows.Add(new SummaryRow(label, visible));
        }
    }
}