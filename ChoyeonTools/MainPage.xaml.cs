using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using ChoyeonTools.Services;
using ChoyeonTools.ViewModels;

// To learn more about WinUI, WinUI Universe insights, and helpful tips, visit:
// https://learn.microsoft.com/windows/apps/winui

namespace ChoyeonTools;

/// <summary>
/// 主页面：左侧分类导航沿用原程序的 Notebook 标签，右侧展示所选分类的信息卡片。
/// 刷新、复制、导出对的是当前页，所以放在内容区右键菜单里；导航底部的"设置"页只留界面偏好与本程序信息。
/// </summary>
public sealed partial class MainPage : Page
{
    private const double MetricGap = 12;
    private const double MetricCardHeight = 84;
    private const double MetricMinimumWidth = 240;
    private const double MetricMaximumWidth = 340;
    private const string SettingsKey = "settings";

    private bool initialized;

    private bool restoringSelectors;

    private double lastMetricCellWidth;

    /// <summary>分组卡片的数据对象到可视元素的映射，供总览跳转后滚动定位。</summary>
    private readonly Dictionary<GroupCard, FrameworkElement> groupCardElements = new();

    private string? pendingGroupTitle;

    private int scrollAttempts;

    public MainViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        ViewModel.LoadSettings();
        RestoreSelectors();
        await ViewModel.InitializeAsync();
    }

    /// <summary>回填上次的主题与刷新间隔，回填过程不触发变更处理。</summary>
    private void RestoreSelectors()
    {
        restoringSelectors = true;
        ThemeSelector.SelectedIndex = IndexByTag(ThemeSelector, ViewModel.Theme);
        AutoRefreshSelector.SelectedIndex = IndexByTag(AutoRefreshSelector, ViewModel.AutoRefreshSeconds.ToString());
        restoringSelectors = false;
        ApplyTheme(ViewModel.Theme);
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        // 换页先回顶部；总览点设备进来的滚动定位在 LayoutUpdated 里随后执行，不受影响。
        ContentScroll.ScrollToVerticalOffset(0);
        if ((item.Tag as string) == SettingsKey)
        {
            ViewModel.EnterSettings();
        }
        else
        {
            ViewModel.Select(item.Tag as string);
        }
    }

    /// <summary>
    /// 右键、长按和 Shift + F10 都走 ContextRequested。
    /// Grid 这类非控件元素不会自动弹出 AttachedFlyout，所以自己按指针位置弹出，
    /// 并先查 IsOpen，避免框架已经打开时重复弹一次。
    /// </summary>
    private void OnContentContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (FlyoutBase.GetAttachedFlyout(ContentRoot) is not { } flyout || flyout.IsOpen)
        {
            return;
        }

        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(ContentRoot, out Windows.Foundation.Point position))
        {
            options.Position = position;
        }

        flyout.ShowAt(ContentRoot, options);
        args.Handled = true;
    }

    private void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.RefreshCommand.Execute(null);
        args.Handled = true;
    }

    /// <summary>总览里点某台设备：切到对应分类页，等卡片生成后滚动到那张卡片。</summary>
    private void OnSummaryDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { Tag: SummaryDevice device }
            || Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => (item.Tag as string) == device.CategoryKey) is not { } target)
        {
            return;
        }

        pendingGroupTitle = device.GroupTitle;
        scrollAttempts = 0;
        GroupsList.LayoutUpdated += OnGroupsLayoutUpdated;
        Nav.SelectedItem = target;
    }

    /// <summary>容器是异步生成的，LayoutUpdated 里逐轮找目标卡片；找不到也要有次数上限，不能永远挂着。</summary>
    private void OnGroupsLayoutUpdated(object? sender, object e)
    {
        GroupCard? card = pendingGroupTitle is null
            ? null
            : ViewModel.Groups.FirstOrDefault(candidate => candidate.Title == pendingGroupTitle);

        if (card is not null && groupCardElements.TryGetValue(card, out FrameworkElement? element))
        {
            StopPendingScroll();
            // 目标卡片对齐到可视区顶部，点进来第一眼就是这台设备。
            element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true });
        }
        else if (++scrollAttempts >= 20)
        {
            StopPendingScroll();
        }
    }

    private void StopPendingScroll()
    {
        GroupsList.LayoutUpdated -= OnGroupsLayoutUpdated;
        pendingGroupTitle = null;
    }

    private void OnGroupCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupCard card } element)
        {
            groupCardElements[card] = element;
        }
    }

    private void OnGroupCardUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GroupCard card })
        {
            groupCardElements.Remove(card);
        }
    }

    /// <summary>资源条目按内容宽度等分成 1 到 4 列，宽度不够就整列换行，条目尺寸始终一致。</summary>
    private void OnMetricsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MetricsGrid?.ItemsPanelRoot is not ItemsWrapGrid wrap)
        {
            return;
        }

        double available = e.NewSize.Width;
        if (available <= 0)
        {
            return;
        }

        // 条目数量随磁盘/网卡/GPU 变化，列数取最小宽度能放下的列数，最多四列。
        int columns = Math.Clamp(
            (int)Math.Floor((available + MetricGap) / (MetricMinimumWidth + MetricGap)),
            1,
            4);

        double cell = Math.Min(MetricMaximumWidth + MetricGap, Math.Floor(available / columns));
        if (Math.Abs(cell - lastMetricCellWidth) < 1)
        {
            return;
        }

        lastMetricCellWidth = cell;
        wrap.ItemWidth = cell;
        wrap.ItemHeight = MetricCardHeight + MetricGap;
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoringSelectors || ThemeSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        ViewModel.Theme = item.Tag as string ?? AppSettings.SystemTheme;
        ApplyTheme(ViewModel.Theme);
        ViewModel.SaveSettings();
    }

    private void OnAutoRefreshSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (restoringSelectors || AutoRefreshSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        ViewModel.AutoRefreshSeconds = int.TryParse(item.Tag as string, out int seconds) ? seconds : 0;
        ViewModel.SaveSettings();
    }

    private void ApplyTheme(string theme) => RootGrid.RequestedTheme = theme switch
    {
        AppSettings.LightTheme => ElementTheme.Light,
        AppSettings.DarkTheme => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static int IndexByTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (string.Equals((box.Items[i] as ComboBoxItem)?.Tag as string, tag, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }
}
