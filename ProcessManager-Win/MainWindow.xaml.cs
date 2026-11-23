using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ProcessManager.Win.Controls;
using ProcessManager.Win.Services;
using Windows.Graphics;

namespace ProcessManager.Win;

public sealed partial class MainWindow : Window
{
    private enum SortColumn { Name, Id, Cpu, Memory }

    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortDescending;
    private readonly ProcessService _service = new();
    private readonly QuickSearchStore _quickSearchStore = new();
    private readonly ObservableCollection<ProcessSnapshot> _visible = [];
    private readonly ObservableCollection<string> _quickSearches = [];
    private readonly DispatcherQueueTimer _timer;
    private List<ProcessSnapshot> _snapshot = [];
    private DateTime? _lastUpdated;
    private bool _refreshing;
    private bool _closed;
    private bool _dialogOpen;
    private bool _ready;
    private bool _selectFirstMatch;
    private ScrollViewer? _listScrollViewer;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ProcessList.ItemsSource = _visible;
        QuickSearchList.ItemsSource = _quickSearches;
        foreach (var query in _quickSearchStore.Load()) _quickSearches.Add(query);
        UpdateQuickSearchBar();
        AppWindow.Resize(new SizeInt32(1200, 780));
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (_, _) =>
        {
            if (AutoRefresh.IsOn) await RefreshAsync();
        };
        Root.Loaded += async (_, _) =>
        {
            _ready = true;
            _timer.Start();
            await RefreshAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
        };
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await Task.Run(_service.Capture);
            if (_closed) return;
            _snapshot = snapshot;
            _lastUpdated = DateTime.Now;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (!_closed) ShowError($"刷新失败：{ex.Message}");
        }
        finally
        {
            _refreshing = false;
            if (!_closed) RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        if (!_ready) return;
        var selectedKeys = SelectedTargets().Select(p => (p.Id, p.StartTimeTicks)).ToHashSet();
        var query = SearchBox.Text.Trim();
        ((SearchHighlightState)Root.Resources["SearchHighlightState"]).Query = query;
        var terms = ProcessSearch.ParseTerms(query);
        IEnumerable<ProcessSnapshot> matches = _snapshot.Where(p => ProcessSearch.Matches(p, terms));
        // 搜索时关键属性（名称、PID、路径）命中的条目优先，仅命令行命中的排在后面。
        var ordered = terms.Length > 0
            ? matches.OrderBy(p => ProcessSearch.Rank(p, terms))
            : matches.OrderBy(_ => 0);
        var sorted = _sortColumn switch
        {
            SortColumn.Id => _sortDescending
                ? ordered.ThenByDescending(p => p.Id) : ordered.ThenBy(p => p.Id),
            SortColumn.Cpu => _sortDescending
                ? ordered.ThenByDescending(p => p.CpuPercent) : ordered.ThenBy(p => p.CpuPercent),
            SortColumn.Memory => _sortDescending
                ? ordered.ThenByDescending(p => p.MemoryBytes) : ordered.ThenBy(p => p.MemoryBytes),
            _ => _sortDescending
                ? ordered.ThenByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        };
        IEnumerable<ProcessSnapshot> items = sorted.ThenBy(p => p.Id);
        var visible = items.ToList();
        // 增量更新：同一进程保留原有行对象、只更新数据，避免行容器重建造成悬停提示重新弹出、滚动跳回顶部。
        var existingByKey = new Dictionary<(int, long?), ProcessSnapshot>(_visible.Count);
        foreach (var item in _visible) existingByKey[(item.Id, item.StartTimeTicks)] = item;
        var scrollViewer = _listScrollViewer ??= FindScrollViewer(ProcessList);
        var scrollOffset = scrollViewer?.VerticalOffset ?? 0;
        var structureChanged = false;
        for (var i = 0; i < visible.Count; i++)
        {
            var next = visible[i];
            if (i < _visible.Count &&
                _visible[i].Id == next.Id && _visible[i].StartTimeTicks == next.StartTimeTicks)
            {
                _visible[i].Update(next);
                continue;
            }
            if (existingByKey.TryGetValue((next.Id, next.StartTimeTicks), out var existing))
            {
                existing.Update(next);
                var index = _visible.IndexOf(existing);
                if (index > i) _visible.Move(index, i);
                structureChanged = true;
                continue;
            }
            _visible.Insert(i, next);
            structureChanged = true;
        }
        if (_visible.Count > visible.Count)
        {
            while (_visible.Count > visible.Count) _visible.RemoveAt(_visible.Count - 1);
            structureChanged = true;
        }
        // 结构变化（新增、移除、换位）后恢复滚动位置，避免列表跳回顶部。
        if (structureChanged && scrollViewer is not null && scrollOffset > 0)
            ProcessList.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => scrollViewer.ChangeView(null, scrollOffset, null, true));
        // 行对象保持不变，只有结构变化时才需重新同步选中顺序；避免频繁清空选中造成列表滚动跳动。
        if (structureChanged)
        {
            var expected = _visible.Where(p => selectedKeys.Contains((p.Id, p.StartTimeTicks))).ToList();
            if (!expected.SequenceEqual(SelectedTargets()))
            {
                ProcessList.SelectedItems.Clear();
                foreach (var item in expected) ProcessList.SelectedItems.Add(item);
            }
        }
        // 输入搜索后默认选中第一个匹配项，便于直接结束进程。
        if (_selectFirstMatch)
        {
            _selectFirstMatch = false;
            if (terms.Length > 0 && _visible.Count > 0 && !ProcessList.SelectedItems.Contains(_visible[0]))
            {
                ProcessList.SelectedItems.Clear();
                ProcessList.SelectedItems.Add(_visible[0]);
            }
        }
        UpdateEndButton();
        StatusText.Text = $"显示 {visible.Count} / {_snapshot.Count} 个进程    最近更新：{_lastUpdated:HH:mm:ss}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _selectFirstMatch = true;
        UpdateQuickSearchBar();
        ApplyFilter();
    }

    private void UpdateQuickSearchBar()
    {
        QuickSearchBar.Visibility = _quickSearches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var query = SearchBox.Text.Trim();
        AddQuickSearchButton.IsEnabled = query.Length > 0 && !_quickSearches.Contains(query);
    }

    // 在搜索框按回车即把当前搜索词存为常用搜索标签，等同于点击“添加常用”。
    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        AddCurrentQuickSearch();
    }

    private void AddQuickSearch_Click(object sender, RoutedEventArgs e) => AddCurrentQuickSearch();

    private void AddCurrentQuickSearch()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0 || _quickSearches.Contains(query)) return;
        _quickSearches.Insert(0, query);
        _quickSearchStore.Save(_quickSearches);
        UpdateQuickSearchBar();
    }

    // 点击标签直接填入搜索框，沿用现有的搜索与默认选中第一个匹配项的逻辑。
    private void QuickSearch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string query }) return;
        SearchBox.Text = query;
        SearchBox.SelectionStart = query.Length;
    }

    private void RemoveQuickSearch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string query }) return;
        _quickSearches.Remove(query);
        _quickSearchStore.Save(_quickSearches);
        UpdateQuickSearchBar();
    }
    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<SortColumn>(button.Tag?.ToString(), out var column)) return;
        _sortDescending = column == _sortColumn ? !_sortDescending : column is SortColumn.Cpu or SortColumn.Memory;
        _sortColumn = column;
        foreach (var (header, key, label) in new[]
        {
            (NameSortButton, SortColumn.Name, "进程名称"),
            (IdSortButton, SortColumn.Id, "PID"),
            (CpuSortButton, SortColumn.Cpu, "CPU"),
            (MemorySortButton, SortColumn.Memory, "内存")
        })
        {
            header.Content = key == _sortColumn ? $"{label} {(_sortDescending ? "↓" : "↑")}" : label;
        }
        ApplyFilter();
    }
    private List<ProcessSnapshot> SelectedTargets() =>
        ProcessList.SelectedItems.OfType<ProcessSnapshot>().ToList();

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private void UpdateEndButton()
    {
        var count = SelectedTargets().Count(p => p.CanTerminate);
        EndButton.IsEnabled = !_closed && count > 0;
        EndButton.Content = count > 1 ? $"结束 {count} 个进程" : "结束进程";
    }

    private void Process_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEndButton();

    // 不弹确认框，直接结束所有可结束的选中进程，失败项汇总提示。
    private async void End_Click(object sender, RoutedEventArgs e)
    {
        var targets = SelectedTargets().Where(p => p.CanTerminate).ToList();
        if (_closed || targets.Count == 0) return;
        EndButton.IsEnabled = false;
        try
        {
            var failures = await Task.Run(() => targets
                .Select(target => (target, Error: TryTerminate(target)))
                .Where(result => result.Error is not null)
                .Select(result => result.Error!)
                .ToList());
            await RefreshAsync();
            if (failures.Count > 0) ShowError($"有 {failures.Count} 个进程未能结束：\n{string.Join("\n", failures)}");
        }
        catch (Exception ex)
        {
            ShowError($"结束失败：{ex.Message}");
        }
        finally
        {
            if (!_closed) UpdateEndButton();
        }
    }

    private static string? TryTerminate(ProcessSnapshot target)
    {
        try
        {
            ProcessService.Terminate(target);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return $"{target.Name}（PID {target.Id}）：访问被拒绝，请尝试以管理员身份运行";
        }
        catch (Exception ex)
        {
            return $"{target.Name}（PID {target.Id}）：{ex.Message}";
        }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var projectDirectory = typeof(MainWindow).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "ProjectDirectory")?.Value;
            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
                throw new DirectoryNotFoundException("项目目录不存在，可能已被移动或删除。");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                Arguments = $"\"{projectDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError($"打开项目目录失败：{ex.Message}");
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var executable = new TextBox
            {
                Header = "程序名称或完整路径",
                PlaceholderText = "例如 notepad.exe 或 C:\\Windows\\System32\\notepad.exe",
                MinWidth = 420
            };
            var arguments = new TextBox { Header = "命令行参数（可选）" };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(executable);
            panel.Children.Add(arguments);
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "启动程序",
                Content = panel,
                PrimaryButtonText = "启动",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };
            executable.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(executable.Text);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed) return;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Text.Trim().Trim('"'),
                Arguments = arguments.Text,
                UseShellExecute = true
            });
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError($"启动失败：{ex.Message}");
        }
        finally { _dialogOpen = false; }
    }

    private void ShowError(string message)
    {
        if (_closed) return;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }
}
