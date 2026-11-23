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
    // 键缺失表示展开。折叠状态按 PID 和启动时间记住，刷新后不丢失。
    private readonly Dictionary<(int Id, long? Start), bool> _expansion = [];
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
        AppWindow.Resize(new SizeInt32(1320, 780));
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

    #region 进程树
    private void PruneExpansion()
    {
        if (_expansion.Count == 0) return;
        HashSet<(int Id, long? Start)> live = [];
        foreach (ProcessSnapshot process in _snapshot) live.Add((process.Id, process.StartTimeTicks));
        List<(int Id, long? Start)> stale = [];
        foreach ((int Id, long? Start) key in _expansion.Keys)
        {
            if (!live.Contains(key)) stale.Add(key);
        }
        foreach ((int Id, long? Start) key in stale) _expansion.Remove(key);
    }

    // 没有记录过的进程默认展开。搜索命中子进程时，先把父进程展开，命中项才不会藏在折叠分支里。
    private bool IsNodeExpanded(ProcessSnapshot process) =>
        !_expansion.TryGetValue((process.Id, process.StartTimeTicks), out bool expanded) || expanded;

    private List<ProcessSnapshot> BuildVisibleTree(string[] terms, bool expandMatches)
    {
        Dictionary<int, ProcessSnapshot> byId = new(_snapshot.Count);
        foreach (ProcessSnapshot process in _snapshot) byId.TryAdd(process.Id, process);

        Dictionary<int, List<ProcessSnapshot>> children = [];
        List<ProcessSnapshot> roots = [];
        foreach (ProcessSnapshot process in _snapshot)
        {
            if (process.ParentProcessId is int parentId && byId.ContainsKey(parentId))
            {
                if (!children.TryGetValue(parentId, out List<ProcessSnapshot>? siblings))
                    children[parentId] = siblings = [];
                siblings.Add(process);
            }
            else roots.Add(process);
        }

        if (expandMatches) ExpandAncestorsOfMatches(byId, terms);

        HashSet<int>? visibleIds = terms.Length == 0 ? null : MatchingBranchIds(byId, terms);
        List<ProcessSnapshot> output = new(_snapshot.Count);
        HashSet<int> stack = [];
        void Walk(ProcessSnapshot process, int depth)
        {
            if ((visibleIds is not null && !visibleIds.Contains(process.Id)) || !stack.Add(process.Id)) return;
            List<ProcessSnapshot> visibleChildren = [];
            if (children.TryGetValue(process.Id, out List<ProcessSnapshot>? siblings))
            {
                foreach (ProcessSnapshot child in siblings)
                {
                    if (visibleIds is null || visibleIds.Contains(child.Id)) visibleChildren.Add(child);
                }
            }
            visibleChildren.Sort((left, right) => CompareSiblings(left, right, terms));
            bool expanded = IsNodeExpanded(process);
            process.SetTreeState(depth, visibleChildren.Count > 0, expanded);
            output.Add(process);
            if (!expanded)
            {
                stack.Remove(process.Id);
                return;
            }
            foreach (ProcessSnapshot child in visibleChildren) Walk(child, depth + 1);
            stack.Remove(process.Id);
        }

        roots.Sort((left, right) => CompareSiblings(left, right, terms));
        foreach (ProcessSnapshot root in roots) Walk(root, 0);
        return output;
    }

    private void ExpandAncestorsOfMatches(Dictionary<int, ProcessSnapshot> byId, string[] terms)
    {
        foreach (ProcessSnapshot process in _snapshot)
        {
            if (!ProcessSearch.Matches(process, terms)) continue;
            ProcessSnapshot current = process;
            HashSet<int> guard = [];
            while (current.ParentProcessId is int parentId &&
                   byId.TryGetValue(parentId, out ProcessSnapshot? parent) &&
                   guard.Add(parent.Id))
            {
                _expansion[(parent.Id, parent.StartTimeTicks)] = true;
                current = parent;
            }
        }
    }

    // 命中项本身，以及它上面每一层父进程，都要留在树上，否则分支会断。
    private HashSet<int> MatchingBranchIds(Dictionary<int, ProcessSnapshot> byId, string[] terms)
    {
        HashSet<int> visibleIds = [];
        foreach (ProcessSnapshot process in _snapshot)
        {
            if (!ProcessSearch.Matches(process, terms)) continue;
            ProcessSnapshot? current = process;
            HashSet<int> guard = [];
            while (current is not null && guard.Add(current.Id))
            {
                visibleIds.Add(current.Id);
                if (current.ParentProcessId is not int parentId || !byId.TryGetValue(parentId, out ProcessSnapshot? parent))
                    break;
                current = parent;
            }
        }
        return visibleIds;
    }

    // 只在同一父进程下排序，子进程不会因此跑到别的父进程下面。
    private int CompareSiblings(ProcessSnapshot left, ProcessSnapshot right, string[] terms)
    {
        if (terms.Length > 0)
        {
            int rank = ProcessSearch.Rank(left, terms).CompareTo(ProcessSearch.Rank(right, terms));
            if (rank != 0) return rank;
        }
        int column = _sortColumn switch
        {
            SortColumn.Id => left.Id.CompareTo(right.Id),
            SortColumn.Cpu => Nullable.Compare(left.CpuPercent, right.CpuPercent),
            SortColumn.Memory => Nullable.Compare(left.MemoryBytes, right.MemoryBytes),
            _ => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
        };
        if (_sortDescending) column = -column;
        return column != 0 ? column : left.Id.CompareTo(right.Id);
    }

    private void ToggleExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int id }) return;
        ProcessSnapshot? process = _visible.FirstOrDefault(item => item.Id == id);
        if (process is null) return;
        (int Id, long? Start) key = (process.Id, process.StartTimeTicks);
        bool expanded = IsNodeExpanded(process);
        _expansion[key] = !expanded;
        Trace.WriteLine($"[ProcessTree] {(expanded ? "折叠" : "展开")} {process.Name} PID {process.Id}");
        ApplyPinned(process);
    }

    // 上下键移动高亮行。左键四级：先收起直接子进程，再收起自己，再跳到父进程，已经是根进程时收起所有根进程。右键两级：先展开自己，再展开直接子进程。
    private void ProcessList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right && e.KeyStatus.RepeatCount > 1)
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Up:
                if (IsShiftDown()) return;
                e.Handled = true;
                MoveHighlight(-1);
                break;
            case Windows.System.VirtualKey.Down:
                if (IsShiftDown()) return;
                e.Handled = true;
                MoveHighlight(1);
                break;
            case Windows.System.VirtualKey.Left:
                e.Handled = true;
                if (HighlightedRow() is ProcessSnapshot collapse) CollapseHighlighted(collapse);
                break;
            case Windows.System.VirtualKey.Right:
                e.Handled = true;
                if (HighlightedRow() is ProcessSnapshot expand) ExpandHighlighted(expand);
                break;
        }
    }

    private void MoveHighlight(int delta)
    {
        if (_visible.Count == 0) return;
        ProcessSnapshot? current = HighlightedRow();
        int index = current is null ? -1 : _visible.IndexOf(current);
        int next = index < 0
            ? (delta > 0 ? 0 : _visible.Count - 1)
            : Math.Clamp(index + delta, 0, _visible.Count - 1);
        if (index >= 0 && next == index) return;
        SelectRow(_visible[next]);
    }

    private void CollapseHighlighted(ProcessSnapshot process)
    {
        // 自己已收起，或没有子进程：这一行已经完全折叠，左键改去父进程。
        if (!IsNodeExpanded(process) || !HasDirectChildren(process))
        {
            SelectParent(process);
            return;
        }
        List<ProcessSnapshot> children = DirectChildren(process);
        bool grandchildVisible = false;
        foreach (ProcessSnapshot child in children)
        {
            if (IsNodeExpanded(child) && HasDirectChildren(child))
            {
                grandchildVisible = true;
                break;
            }
        }
        if (grandchildVisible)
        {
            foreach (ProcessSnapshot child in children)
                _expansion[(child.Id, child.StartTimeTicks)] = false;
            Trace.WriteLine($"[ProcessTree] 左键折叠直接子进程 {process.Name} PID {process.Id}");
        }
        else
        {
            _expansion[(process.Id, process.StartTimeTicks)] = false;
            Trace.WriteLine($"[ProcessTree] 左键折叠自身 {process.Name} PID {process.Id}");
        }
        ApplyPinned(process);
    }

    private void SelectParent(ProcessSnapshot process)
    {
        if (process.ParentProcessId is not int parentId)
        {
            CollapseAllRoots(process);
            return;
        }
        ProcessSnapshot? parent = null;
        foreach (ProcessSnapshot item in _visible)
        {
            if (item.Id != parentId) continue;
            parent = item;
            break;
        }
        if (parent is null)
        {
            Trace.WriteLine($"[ProcessTree] 左键父进程不在列表中 PID {parentId}");
            return;
        }
        Trace.WriteLine($"[ProcessTree] 左键定位父进程 {parent.Name} PID {parent.Id}");
        SelectRow(parent);
    }

    // 当前行已经是收起的根进程。再按左键时，把每个根进程都收起，高亮仍留在这一行。
    private void CollapseAllRoots(ProcessSnapshot current)
    {
        bool changed = false;
        foreach (ProcessSnapshot item in _snapshot)
        {
            if (item.ParentProcessId is not null) continue;
            if (!IsNodeExpanded(item) || !HasDirectChildren(item)) continue;
            _expansion[(item.Id, item.StartTimeTicks)] = false;
            changed = true;
        }
        if (!changed)
        {
            Trace.WriteLine($"[ProcessTree] 根进程已经全部折叠 {current.Name} PID {current.Id}");
            return;
        }
        Trace.WriteLine($"[ProcessTree] 左键折叠所有根进程，高亮仍在 {current.Name} PID {current.Id}");
        ApplyPinned(current);
    }

    private void SelectRow(ProcessSnapshot target)
    {
        ProcessList.SelectedItems.Clear();
        ProcessList.SelectedItems.Add(target);
        ProcessList.ScrollIntoView(target);
        ProcessList.UpdateLayout();
        if (ProcessList.ContainerFromItem(target) is Control container)
            container.Focus(FocusState.Keyboard);
    }

    private void ExpandHighlighted(ProcessSnapshot process)
    {
        List<ProcessSnapshot> children = DirectChildren(process);
        if (children.Count == 0) return;
        if (!IsNodeExpanded(process))
        {
            _expansion[(process.Id, process.StartTimeTicks)] = true;
            Trace.WriteLine($"[ProcessTree] 右键展开自身 {process.Name} PID {process.Id}");
            ApplyPinned(process);
            return;
        }
        bool canExpandChild = false;
        foreach (ProcessSnapshot child in children)
        {
            if (!IsNodeExpanded(child) && HasDirectChildren(child))
            {
                canExpandChild = true;
                break;
            }
        }
        if (!canExpandChild) return;
        foreach (ProcessSnapshot child in children)
        {
            if (HasDirectChildren(child))
                _expansion[(child.Id, child.StartTimeTicks)] = true;
        }
        Trace.WriteLine($"[ProcessTree] 右键展开直接子进程 {process.Name} PID {process.Id}");
        ApplyPinned(process);
    }

    private List<ProcessSnapshot> DirectChildren(ProcessSnapshot process)
    {
        List<ProcessSnapshot> children = [];
        foreach (ProcessSnapshot item in _snapshot)
        {
            if (item.ParentProcessId == process.Id) children.Add(item);
        }
        return children;
    }

    private bool HasDirectChildren(ProcessSnapshot process)
    {
        foreach (ProcessSnapshot item in _snapshot)
        {
            if (item.ParentProcessId == process.Id) return true;
        }
        return false;
    }

    private ProcessSnapshot? HighlightedRow()
    {
        if (Root.XamlRoot is null) return ProcessList.SelectedItem as ProcessSnapshot;
        DependencyObject? current = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: ProcessSnapshot process }) return process;
            current = VisualTreeHelper.GetParent(current);
        }
        return ProcessList.SelectedItem as ProcessSnapshot;
    }

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // 先记下这一行在视口里的位置。子进程只增删在它下面，改完后把它放回原处。
    private void ApplyPinned(ProcessSnapshot process)
    {
        ScrollViewer? viewer = _listScrollViewer ??= FindScrollViewer(ProcessList);
        double? viewportY = TryGetViewportY(viewer, process);
        ApplyFilter(pinRows: true);
        if (viewer is not null && viewportY is double y) KeepRowInPlace(viewer, process, y);
    }

    private static bool SameProcess(ProcessSnapshot left, ProcessSnapshot right) =>
        left.Id == right.Id && left.StartTimeTicks == right.StartTimeTicks;

    // 折叠展开只在被点的那一行后面多出或少掉一整段。一次删掉、一次插入，避免逐行 Move。
    private bool SpliceVisible(List<ProcessSnapshot> visible)
    {
        int oldCount = _visible.Count;
        int newCount = visible.Count;
        int prefix = 0;
        while (prefix < oldCount && prefix < newCount && SameProcess(_visible[prefix], visible[prefix]))
        {
            _visible[prefix].Update(visible[prefix]);
            prefix++;
        }
        int suffix = 0;
        while (prefix + suffix < oldCount && prefix + suffix < newCount &&
               SameProcess(_visible[oldCount - 1 - suffix], visible[newCount - 1 - suffix]))
            suffix++;
        for (int i = 0; i < suffix; i++)
            _visible[oldCount - suffix + i].Update(visible[newCount - suffix + i]);

        int removeCount = oldCount - prefix - suffix;
        for (int n = 0; n < removeCount; n++)
            _visible.RemoveAt(prefix + removeCount - 1 - n);
        int insertCount = newCount - prefix - suffix;
        for (int n = 0; n < insertCount; n++)
        {
            ProcessSnapshot item = visible[prefix + n];
            int existing = _visible.IndexOf(item);
            int target = prefix + n;
            if (existing == target) continue;
            if (existing >= 0) _visible.Move(existing, target);
            else _visible.Insert(target, item);
        }
        Trace.WriteLine($"[ProcessTree] 列表拼接：前 {prefix} 行、后 {suffix} 行不动，移除 {removeCount}，插入 {insertCount}");
        return removeCount > 0 || insertCount > 0;
    }

    private double? TryGetViewportY(ScrollViewer? viewer, ProcessSnapshot process)
    {
        if (viewer is null) return null;
        if (ProcessList.ContainerFromItem(process) is not FrameworkElement container) return null;
        return container.TransformToVisual(viewer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
    }

    // 用这一行相对视口的纵坐标反推滚动偏移，不播滚动动画。
    private void KeepRowInPlace(ScrollViewer viewer, ProcessSnapshot process, double viewportY)
    {
        bool Align()
        {
            if (ProcessList.ContainerFromItem(process) is not FrameworkElement container) return false;
            double y = container.TransformToVisual(viewer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            double delta = y - viewportY;
            if (Math.Abs(delta) < 1) return true;
            double target = viewer.VerticalOffset + delta;
            if (target < 0) target = 0;
            return viewer.ChangeView(null, target, null, true);
        }

        ProcessList.UpdateLayout();
        if (Align()) return;
        ProcessList.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (_closed) return;
            ProcessList.UpdateLayout();
            Align();
        });
    }
    #endregion

    private void ApplyFilter(bool pinRows = false)
    {
        if (!_ready) return;
        var selectedKeys = SelectedTargets().Select(p => (p.Id, p.StartTimeTicks)).ToHashSet();
        var query = SearchBox.Text.Trim();
        ((SearchHighlightState)Root.Resources["SearchHighlightState"]).Query = query;
        string[] terms = ProcessSearch.ParseTerms(query);
        PruneExpansion();
        List<ProcessSnapshot> visible = BuildVisibleTree(terms, _selectFirstMatch && terms.Length > 0);
        ScrollViewer? scrollViewer = _listScrollViewer ??= FindScrollViewer(ProcessList);
        double scrollOffset = scrollViewer?.VerticalOffset ?? 0;
        bool structureChanged = pinRows ? SpliceVisible(visible) : MergeVisible(visible);
        // 折叠展开由调用方把被点的那一行钉在原处，这里不再按旧偏移量回滚。
        if (!pinRows && structureChanged && scrollViewer is not null && scrollOffset > 0)
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
            ProcessSnapshot? first = terms.Length == 0 ? null : _visible.FirstOrDefault(process => ProcessSearch.Matches(process, terms));
            if (first is not null && !ProcessList.SelectedItems.Contains(first))
            {
                ProcessList.SelectedItems.Clear();
                ProcessList.SelectedItems.Add(first);
                ProcessList.ScrollIntoView(first);
            }
        }
        UpdateEndButton();
        StatusText.Text = $"显示 {visible.Count} / {_snapshot.Count} 个进程    最近更新：{_lastUpdated:HH:mm:ss}";
    }

    // 刷新、搜索、排序仍按行对齐：同一进程保留原有行对象，避免悬停提示关掉、滚动跳回顶部。
    private bool MergeVisible(List<ProcessSnapshot> visible)
    {
        Dictionary<(int, long?), ProcessSnapshot> existingByKey = new(_visible.Count);
        foreach (ProcessSnapshot item in _visible) existingByKey[(item.Id, item.StartTimeTicks)] = item;
        bool structureChanged = false;
        for (int i = 0; i < visible.Count; i++)
        {
            ProcessSnapshot next = visible[i];
            if (i < _visible.Count && SameProcess(_visible[i], next))
            {
                _visible[i].Update(next);
                continue;
            }
            if (existingByKey.TryGetValue((next.Id, next.StartTimeTicks), out ProcessSnapshot? existing))
            {
                existing.Update(next);
                int index = _visible.IndexOf(existing);
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
        return structureChanged;
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
