# 进程管理器（Windows / WinUI 3）

基于 C#、.NET 10、Windows App SDK 1.8 的非打包桌面应用，支持 Windows 10 2004（19041）及以上版本。

## 基本功能

- 显示进程图标、名称、PID、CPU、工作集内存、可执行文件路径。图标异步加载并按路径缓存，无法读取时显示默认图标。
- 每 2 秒后台刷新，可暂停或手动刷新；可按进程名称（带或不带 `.exe`）、PID、可执行文件完整路径、目录片段、命令行参数及参数中的脚本/文件路径搜索，不区分大小写，兼容 `/` 和 `\`。
- 搜索命中内容以金色背景、黑色文字高亮，覆盖名称、PID、路径及悬停提示中的完整路径、命令行；清空搜索后恢复。多个关键词及重叠命中均支持。
- 搜索支持环境变量（如 `%LOCALAPPDATA%`）和多个关键词（空格分隔，需全部匹配）；包含空格的完整路径可加双引号。悬停路径可查看完整路径及命令行。通过 WMI 补充路径、命令行；权限不足或 WMI 不可用时自动退回基础数据。不扫描磁盘，也不能查找未出现在可执行路径或命令行中的任意已打开文件。
- 点击名称、PID、CPU、内存列头排序，再次点击切换升序/降序，箭头标示当前方向。名称和 PID 默认升序，CPU 和内存默认降序。
- 搜索时名称、PID、路径等关键属性命中的进程排在前面，仅命令行参数命中的排在后面；输入搜索词后自动选中第一个匹配项，可直接 `Alt+E` 结束。
- 搜索词可点“添加常用”保存为常用搜索标签，点击标签即执行该搜索，点标签上的 `×` 删除。标签保存在 `%LOCALAPPDATA%\ProcessManager\quick-search.json`，重启后自动恢复。
- 点击顶部“打开项目目录”，使用 Windows 资源管理器打开本项目根目录（构建时记录的源码目录）；目录不存在或启动失败时显示错误。
- 输入程序名称或路径及可选参数启动程序。
- 支持多选（Ctrl 或 Shift 加点击）和 `Alt+E` 快捷键，结束按钮立即强制结束所有选中的可结束进程（不结束子进程），不再弹确认框；未能结束的进程会汇总提示权限不足或已退出的原因。

CPU 按全部逻辑处理器归一化，首次采样显示 `—`。不可访问的数据也显示 `—` 或“不可访问”。系统进程、当前应用及无法确认启动时间的进程不能结束。结束前复核启动时间，避免误结束复用 PID 的进程。

## 构建与运行

需要 Windows、.NET 10 SDK 和 Windows SDK / C++ 构建工具（可通过 Visual Studio 的 Windows 应用开发工作负载安装）。首次构建需要访问 NuGet。

在仓库根目录执行：

```powershell
dotnet build .\ProcessManager-Win\ProcessManager.Win.csproj -c Debug -p:Platform=x64
.\builds\ProcessManager-Win\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ProcessManager.Win.exe
```

发布独立运行版本（包含 .NET 和 Windows App SDK 运行库）：

```powershell
dotnet publish .\ProcessManager-Win\ProcessManager.Win.csproj -c Release -p:Platform=x64
```

发布目录：`builds/ProcessManager-Win/publish/win-x64/`。所有编译和中间产物均位于根目录 `builds/` 下。ARM64 构建将 `Platform` 改为 `ARM64`。

通常不需要管理员权限；管理其他用户的进程可能需要以管理员身份运行。强制结束进程会丢失未保存的数据，请谨慎操作。
