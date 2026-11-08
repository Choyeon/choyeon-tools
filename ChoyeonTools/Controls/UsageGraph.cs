using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace ChoyeonTools.Controls;

/// <summary>
/// 任务管理器风格的迷你曲线：网格底、折线，以及折线下方半透明填充，最新的值画在最右边。
/// 历史序列里的 NaN 表示这一秒还没采到，画出来就是左侧留空，曲线随采样时长从左往右长出来。
/// </summary>
public sealed class UsageGraph : Canvas
{
    private const int GridColumns = 6;
    private const int GridRows = 4;

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(IReadOnlyList<double>), typeof(UsageGraph), new PropertyMetadata(null, OnChartChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(UsageGraph), new PropertyMetadata(100d, OnChartChanged));

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor), typeof(string), typeof(UsageGraph), new PropertyMetadata(null, OnChartChanged));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(UsageGraph), new PropertyMetadata(null, OnChartChanged));

    private string? parsedLineColor;
    private SolidColorBrush? parsedLineBrush;

    public UsageGraph() => SizeChanged += (_, _) => Redraw();

    /// <summary>按时间从旧到新的采样值，长度固定为曲线窗口容量。</summary>
    public IReadOnlyList<double>? History
    {
        get => (IReadOnlyList<double>?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    /// <summary>纵轴上限，0 表示按本轮最大值自动缩放，用于网卡吞吐这类量纲不定的序列。</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>曲线颜色，"#RRGGBB"；任务管理器给每类资源固定一种强调色。</summary>
    public string? LineColor
    {
        get => (string?)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    private static void OnChartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((UsageGraph)d).Redraw();

    private SolidColorBrush? ResolveLineBrush()
    {
        string? text = LineColor;
        if (text is not { Length: 7 } || text[0] != '#')
        {
            return null;
        }

        if (parsedLineColor == text && parsedLineBrush is not null)
        {
            return parsedLineBrush;
        }

        try
        {
            byte red = Convert.ToByte(text[1..3], 16);
            byte green = Convert.ToByte(text[3..5], 16);
            byte blue = Convert.ToByte(text[5..7], 16);
            parsedLineColor = text;
            parsedLineBrush = new SolidColorBrush(new Color { R = red, G = green, B = blue, A = 255 });
            return parsedLineBrush;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void Redraw()
    {
        Children.Clear();
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (GridBrush is { } gridBrush)
        {
            for (int column = 1; column < GridColumns; column++)
            {
                double x = Math.Round(width * column / GridColumns) + 0.5;
                Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = height, Stroke = gridBrush, StrokeThickness = 1 });
            }

            for (int row = 1; row < GridRows; row++)
            {
                double y = Math.Round(height * row / GridRows) + 0.5;
                Children.Add(new Line { X1 = 0, Y1 = y, X2 = width, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            }
        }

        IReadOnlyList<double> history = History ?? Array.Empty<double>();
        if (history.Count < 2)
        {
            return;
        }

        double scale = Maximum > 0
            ? Maximum
            : history.Max(value => double.IsFinite(value) ? value : 0) * 1.1;
        if (scale <= 0 || ResolveLineBrush() is not { } lineBrush)
        {
            return;
        }

        var points = new PointCollection();
        foreach ((int index, double value) in history.Index())
        {
            if (double.IsFinite(value))
            {
                points.Add(new Point(
                    index * width / (history.Count - 1),
                    height - (Math.Clamp(value, 0, scale) / scale * height)));
            }
        }

        if (points.Count < 2)
        {
            return;
        }

        var area = new PointCollection { new Point(points[0].X, height) };
        foreach (Point point in points)
        {
            area.Add(point);
        }

        area.Add(new Point(points[^1].X, height));
        Children.Add(new Polygon { Points = area, Fill = lineBrush, Opacity = 0.28 });
        Children.Add(new Polyline { Points = points, Stroke = lineBrush, StrokeThickness = 1.5 });
    }
}
