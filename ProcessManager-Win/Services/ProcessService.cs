using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace ProcessManager.Win.Services;

// 采样数据可变：刷新时原地更新同一行的对象，避免列表项重建导致悬停提示关闭、滚动位置重置。
public sealed class ProcessSnapshot : INotifyPropertyChanged
{
    private string _name;
    private double? _cpuPercent;
    private long? _memoryBytes;
    private string? _filePath;
    private string? _commandLine;

    public ProcessSnapshot(int id, string name, long? startTimeTicks, double? cpuPercent, long? memoryBytes,
        string? filePath, string? commandLine = null)
    {
        Id = id;
        _name = name;
        StartTimeTicks = startTimeTicks;
        _cpuPercent = cpuPercent;
        _memoryBytes = memoryBytes;
        _filePath = filePath;
        _commandLine = commandLine;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; }
    public string Name => _name;
    public long? StartTimeTicks { get; private set; }
    public double? CpuPercent => _cpuPercent;
    public long? MemoryBytes => _memoryBytes;
    public string? FilePath => _filePath;
    public string? CommandLine => _commandLine;

    #region 进程树
    // 已核对过的父进程。null 表示父进程已退出、PID 被复用，或启动时间对不上，界面上它就是根节点。
    public int? ParentProcessId { get; private set; }
    // 与树上的父子关系一致。没有确认到父进程时显示“—”，不写一个已经对不上的 PID。
    public string ParentIdText => ParentProcessId is int parentId ? parentId.ToString() : "—";

    private const double IndentPerLevel = 16;
    private const string ExpandedGlyph = "\uE70D";
    private const string CollapsedGlyph = "\uE76C";
    private int _depth;
    private bool _hasChildren;
    private bool _isExpanded = true;

    public int Depth => _depth;
    public bool HasChildren => _hasChildren;
    public bool IsExpanded => _isExpanded;
    public Thickness IndentMargin => new(_depth * IndentPerLevel, 0, 0, 0);
    public string ExpandGlyph => _isExpanded ? ExpandedGlyph : CollapsedGlyph;
    public string ExpandToolTip => _isExpanded ? "折叠子进程" : "展开子进程";
    public Visibility ExpanderVisibility => _hasChildren ? Visibility.Visible : Visibility.Collapsed;

    internal void AssignParent(int parentProcessId) => ParentProcessId = parentProcessId;

    internal void SetTreeState(int depth, bool hasChildren, bool isExpanded)
    {
        if (_depth != depth)
        {
            _depth = depth;
            Notify(nameof(Depth));
            Notify(nameof(IndentMargin));
        }
        if (_hasChildren != hasChildren)
        {
            _hasChildren = hasChildren;
            Notify(nameof(HasChildren));
            Notify(nameof(ExpanderVisibility));
        }
        if (_isExpanded != isExpanded)
        {
            _isExpanded = isExpanded;
            Notify(nameof(IsExpanded));
            Notify(nameof(ExpandGlyph));
            Notify(nameof(ExpandToolTip));
        }
    }
    #endregion

    public string CpuText => CpuPercent is { } cpu ? $"{cpu:F1}%" : "—";
    public string MemoryText => MemoryBytes is { } bytes ? $"{bytes / 1024d / 1024:F1} MB" : "—";
    public string PathText => FilePath ?? "不可访问";
    public string DetailsText => $"路径：{PathText}\n命令行：{CommandLine ?? "不可访问"}";
    public string SearchText => $"{Id}\n{Name}\n{(Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Name : Name + ".exe")}\n{FilePath}\n{CommandLine}"
        .Replace('/', '\\');
    public bool CanTerminate => Id > 4 && Id != Environment.ProcessId && StartTimeTicks.HasValue;

    public void Update(ProcessSnapshot source)
    {
        var searchChanged = false;
        if (_name != source._name)
        {
            _name = source._name;
            Notify(nameof(Name));
            searchChanged = true;
        }
        if (StartTimeTicks != source.StartTimeTicks)
        {
            StartTimeTicks = source.StartTimeTicks;
            Notify(nameof(StartTimeTicks));
            Notify(nameof(CanTerminate));
        }
        if (_cpuPercent != source._cpuPercent)
        {
            _cpuPercent = source._cpuPercent;
            Notify(nameof(CpuPercent));
            Notify(nameof(CpuText));
        }
        if (_memoryBytes != source._memoryBytes)
        {
            _memoryBytes = source._memoryBytes;
            Notify(nameof(MemoryBytes));
            Notify(nameof(MemoryText));
        }
        if (_filePath != source._filePath)
        {
            _filePath = source._filePath;
            Notify(nameof(FilePath));
            Notify(nameof(PathText));
            Notify(nameof(DetailsText));
            searchChanged = true;
        }
        if (_commandLine != source._commandLine)
        {
            _commandLine = source._commandLine;
            Notify(nameof(CommandLine));
            Notify(nameof(DetailsText));
            searchChanged = true;
        }
        if (ParentProcessId != source.ParentProcessId)
        {
            ParentProcessId = source.ParentProcessId;
            Notify(nameof(ParentProcessId));
            Notify(nameof(ParentIdText));
        }
        SetTreeState(source.Depth, source.HasChildren, source.IsExpanded);
        if (searchChanged) Notify(nameof(SearchText));
    }

    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

// 仅在单个后台采样任务中访问；CPU 百分比按所有逻辑处理器归一化。
public sealed class ProcessService
{
    private Dictionary<(int Id, long Start), (TimeSpan Cpu, long Timestamp)> _previous = [];
    private sealed record SearchDetails(long? StartTimeTicks, string? FilePath, string? CommandLine);

    public List<ProcessSnapshot> Capture()
    {
        var details = ReadSearchDetails();
        var result = new List<ProcessSnapshot>();
        var current = new Dictionary<(int, long), (TimeSpan, long)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
                    var name = process.ProcessName;
                    var start = TryRead(() => process.StartTime.ToUniversalTime().Ticks);
                    var cpu = TryRead(() => process.TotalProcessorTime);
                    var memory = TryRead(() => process.WorkingSet64);
                    var timestamp = Stopwatch.GetTimestamp();
                    double? usage = null;
                    if (start is { } startTicks && cpu is { } cpuTime)
                    {
                        var key = (id, startTicks);
                        if (_previous.TryGetValue(key, out var previous))
                        {
                            var elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, timestamp).TotalSeconds;
                            if (elapsed > 0)
                            {
                                usage = Math.Clamp((cpuTime - previous.Cpu).TotalSeconds /
                                    elapsed / Environment.ProcessorCount * 100, 0, 100);
                            }
                        }
                        current[key] = (cpuTime, timestamp);
                    }

                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception ex) when (IsUnavailable(ex)) { }
                    details.TryGetValue(id, out var extra);
                    // WMI 的时间精度为微秒；核对启动时间，避免 PID 复用时混入旧进程信息。
                    if (extra is not null && (start is null || extra.StartTimeTicks is null ||
                        Math.Abs(start.Value - extra.StartTimeTicks.Value) >= TimeSpan.TicksPerMillisecond))
                        extra = null;
                    path ??= extra?.FilePath;
                    result.Add(new(id, name, start, usage, memory, path, extra?.CommandLine));
                }
                catch (Exception ex) when (IsUnavailable(ex))
                {
                    // 进程可能在枚举后退出；不让它中断整个列表刷新。
                }
            }
        }
        _previous = current;
        LinkParents(result);
        return result;
    }

    #region 父子关系
    // 父进程 ID 来自创建时的记录。父进程退出后号码还在；号码再分给新进程时，用启动时间拆开，避免挂错人。
    private static void LinkParents(List<ProcessSnapshot> processes)
    {
        Dictionary<int, int> parents = ReadParentProcessIds();
        Dictionary<int, ProcessSnapshot> byId = new(processes.Count);
        foreach (ProcessSnapshot process in processes) byId.TryAdd(process.Id, process);

        int linked = 0;
        int missing = 0;
        int reused = 0;
        int unverified = 0;
        foreach (ProcessSnapshot process in processes)
        {
            if (!parents.TryGetValue(process.Id, out int parentId) || parentId == process.Id)
            {
                unverified++;
                continue;
            }
            if (!byId.TryGetValue(parentId, out ProcessSnapshot? parent))
            {
                missing++;
                continue;
            }
            if (parent.StartTimeTicks is not long parentStart || process.StartTimeTicks is not long childStart)
            {
                unverified++;
                continue;
            }
            // 真正的父进程一定更早启动。当前占用这个 PID 的进程如果更晚，说明号码已被复用。
            if (parentStart >= childStart)
            {
                reused++;
                Trace.WriteLine($"[ProcessTree] 父进程 PID 已复用，不建立关系：子进程 {process.Id} {process.Name}，当前 PID {parentId} {parent.Name}");
                continue;
            }
            process.AssignParent(parentId);
            linked++;
        }
        Trace.WriteLine($"[ProcessTree] 进程 {processes.Count} 个，建立父子关系 {linked} 个，父进程已退出 {missing} 个，PID 复用已拆开 {reused} 个，启动时间无法确认 {unverified} 个");
    }

    private static Dictionary<int, int> ReadParentProcessIds()
    {
        Dictionary<int, int> parents = new();
        nint snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidSnapshot)
        {
            Trace.WriteLine($"[ProcessTree] 无法读取父进程：Win32 错误 {Marshal.GetLastWin32Error()}");
            return parents;
        }
        try
        {
            ProcessEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                Trace.WriteLine($"[ProcessTree] 进程快照为空：结构大小 {entry.Size}，Win32 错误 {Marshal.GetLastWin32Error()}");
                return parents;
            }
            do parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            while (Process32Next(snapshot, ref entry));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ProcessTree] 读取父进程失败：{ex.GetType().Name} {ex.Message}");
            parents.Clear();
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return parents;
    }

    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly nint InvalidSnapshot = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
    #endregion

    public static void Terminate(ProcessSnapshot target)
    {
        if (!target.CanTerminate)
            throw new InvalidOperationException("不能结束该进程：它是受保护进程、本程序或无法确认身份的进程。");

        using var process = Process.GetProcessById(target.Id);
        if (process.StartTime.ToUniversalTime().Ticks != target.StartTimeTicks)
            throw new InvalidOperationException("原进程已退出，PID 已被重新使用。请刷新后重试。");

        process.Kill(); // 不连带结束子进程。
    }

    private static Dictionary<int, SearchDetails> ReadSearchDetails()
    {
        var details = new Dictionary<int, SearchDetails>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT ProcessId, CreationDate, ExecutablePath, CommandLine FROM Win32_Process",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2) });
            using var processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                using (process)
                {
                    try
                    {
                        var id = Convert.ToInt32(process["ProcessId"]);
                        var created = process["CreationDate"] as string;
                        long? start = string.IsNullOrEmpty(created) ? null :
                            ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime().Ticks;
                        details[id] = new(start, process["ExecutablePath"] as string, process["CommandLine"] as string);
                    }
                    catch (Exception ex) when (IsSearchUnavailable(ex)) { }
                }
            }
        }
        catch (Exception ex) when (IsSearchUnavailable(ex))
        {
            // WMI 不可用或权限不足时，仍保留基础进程名、PID 和路径搜索。
        }
        return details;
    }

    private static bool IsSearchUnavailable(Exception ex) =>
        IsUnavailable(ex) || ex is ManagementException or COMException or ArgumentException or TimeoutException;

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (IsUnavailable(ex)) { return null; }
    }

    private static bool IsUnavailable(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException;
}
