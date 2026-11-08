using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using ChoyeonTools.Models;
using ChoyeonTools.Services;

namespace ChoyeonTools.ViewModels;

/// <summary>
/// 总览页的实时资源条目，照任务管理器"性能"列表的写法排：左侧迷你曲线，右侧标题加一到两行数值。
/// 卡片实例常驻界面，文字与历史就地更新，所以一秒一次的采样不会重建视觉树、也不会闪烁。
/// </summary>
public sealed partial class MetricCard : ObservableObject
{
    private readonly double[] history;
    private int cursor;

    public MetricCard(string key, string label, string accent = "", double graphMaximum = 100, bool showGraph = true)
    {
        Key = key;
        Label = label;
        Accent = accent;
        GraphMaximum = graphMaximum;
        GraphVisibility = showGraph ? Visibility.Visible : Visibility.Collapsed;
        history = new double[ResourceSampler.HistoryCapacity];
        Array.Fill(history, double.NaN);
        History = (double[])history.Clone();
    }

    /// <summary>同步卡片列表时用它认人，同一块盘或同一张网卡的曲线不会因为重新采集而清空。</summary>
    public string Key { get; }

    /// <summary>曲线颜色，与任务管理器给各类资源固定的强调色一致。</summary>
    public string Accent { get; }

    /// <summary>曲线纵轴上限，0 表示按窗口内最大值自动缩放。</summary>
    public double GraphMaximum { get; }

    public Visibility GraphVisibility { get; }

    [ObservableProperty]
    public partial string Label { get; private set; }

    [ObservableProperty]
    public partial string Value { get; private set; } = "采集中…";

    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility DetailVisibility { get; private set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial IReadOnlyList<double>? History { get; private set; }

    /// <summary>写文字并把这一秒的读数推进曲线窗口；sample 为 null 时留一个空位，不画点。</summary>
    public void Update(string label, string value, string detail, double? sample)
    {
        if (Label != label)
        {
            Label = label;
        }

        if (Value != value)
        {
            Value = value;
        }

        if (Detail != detail)
        {
            Detail = detail;
            DetailVisibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        history[cursor] = sample.HasValue ? Math.Max(0, sample.Value) : double.NaN;
        cursor = (cursor + 1) % history.Length;

        // cursor 现在指向最旧的样本，按它起算就是"左旧右新"。
        var snapshot = new double[history.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i] = history[(cursor + i) % history.Length];
        }

        History = snapshot;
    }
}

/// <summary>
/// 一个信息分组卡片。字段按标签匹配后原地改写，所以刷新只会更新文本，不会重排卡片。
/// </summary>
public sealed partial class GroupCard : ObservableObject
{
    public GroupCard(string title) => Title = title;

    public string Title { get; }

    public ObservableCollection<FieldRow> Fields { get; } = [];

    public void Sync(InfoGroup source)
    {
        var visible = source.Fields.Where(field => !field.IsMissing).ToList();
        var desired = new List<FieldRow>(visible.Count);
        var reused = new HashSet<FieldRow>();

        foreach (InfoField field in visible)
        {
            FieldRow row = Fields.FirstOrDefault(candidate => !reused.Contains(candidate) && candidate.Label == field.Label)
                ?? new FieldRow(field.Label);
            reused.Add(row);
            row.UpdateValue(field.Value);
            desired.Add(row);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            int current = Fields.IndexOf(desired[i]);
            if (current < 0)
            {
                Fields.Insert(i, desired[i]);
            }
            else if (current != i)
            {
                Fields.RemoveAt(current);
                Fields.Insert(i, desired[i]);
            }
        }

        for (int i = Fields.Count - 1; i >= desired.Count; i--)
        {
            Fields.RemoveAt(i);
        }
    }
}

/// <summary>卡片中的一行"标签：值"。</summary>
public sealed partial class FieldRow : ObservableObject
{
    public FieldRow(string label) => Label = label;

    public string Label { get; }

    [ObservableProperty]
    public partial string Value { get; private set; } = string.Empty;

    public void UpdateValue(string value)
    {
        if (Value != value)
        {
            Value = value;
        }
    }
}
