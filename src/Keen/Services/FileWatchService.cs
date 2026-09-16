using Keen.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace Keen.Services;

// 文件监听(不变量⑦):每个不同父目录一个 FileSystemWatcher(复数 Filters);Changed/Created/Renamed →
// 管线防抖。Error → 1s 退避重建 + catch-up。60s 健康轮询双向:目录消失 → 标 Missing 并释放 FSW;
// 目录回来 → 重新武装 + catch-up;文件本身消失 → 标 Missing。睡眠/唤醒经 PowerModeChanged 重武装。
// 同目录改名:Renamed 事件的旧路径命中被监控文件时,经 FileMoved 事件上报由 WatchService 跟随。
internal sealed class FileWatchService : IDisposable
{
    private readonly BackupPipeline _pipeline;
    private readonly ILogger<FileWatchService> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DirWatch> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, WatchedFile> _files = new();
    private readonly Timer _healthPoll;

    public FileWatchService(BackupPipeline pipeline, ILogger<FileWatchService> log)
    {
        _pipeline = pipeline;
        _log = log;
        _healthPoll = new Timer(
            _ => HealthPoll(),
            null,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60)
        );
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "无法挂载电源事件钩子(睡眠/唤醒恢复将退化为下次轮询)");
        }
    }

    private sealed class DirWatch
    {
        public FileSystemWatcher Fsw = null!;
        public Dictionary<string, Guid> NameToGuid = new(StringComparer.OrdinalIgnoreCase);
    }

    // 同目录改名(旧路径命中被监控文件)时触发:(guid, 新完整路径)。由 WatchService 跟随更新。
    public event Action<Guid, string>? FileMoved;

    // 改名跟随的宽限期:旧名被改名挪走后,等 2s——期内旧名重建 = 保存套路(挪走旧文件+原名写新文件),
    // 取消跟随、继续盯旧名;期内没回来 = 真改名,才提交跟随。
    private sealed class PendingMove
    {
        public Guid Guid;
        public string NewPath = "";
        public Timer Timer = null!;
    }

    private readonly Dictionary<Guid, PendingMove> _pending = new();
    private static readonly TimeSpan MoveGrace = TimeSpan.FromSeconds(2);

    public void Add(WatchedFile wf)
    {
        lock (_gate)
        {
            _files[wf.Guid] = wf;
            EnsureDir(Path.GetDirectoryName(wf.CurrentPath) ?? "");
        }
    }

    public void Remove(Guid guid)
    {
        lock (_gate)
        {
            if (_pending.Remove(guid, out var pm))
            {
                try
                {
                    pm.Timer.Dispose();
                }
                catch { }
            }
            if (!_files.TryGetValue(guid, out var wf))
                return;
            var dir = Path.GetDirectoryName(wf.CurrentPath) ?? "";
            _files.Remove(guid);
            if (_dirs.ContainsKey(dir))
                EnsureDir(dir);
        }
        _pipeline.MarkHealth(guid, FileHealth.Watching, null);
    }

    // 按当前 _files 重建某目录的 watcher;目录不存在则静默跳过(由健康轮询标记 Missing,
    // 并在目录回来时重武装)——构造 FSW 对缺失目录会直接抛异常,曾导致启动崩溃(评审致命#2)。
    private void EnsureDir(string dir)
    {
        if (_dirs.TryGetValue(dir, out var old))
        {
            try
            {
                old.Fsw.Dispose();
            }
            catch { }
            _dirs.Remove(dir);
        }
        if (!Directory.Exists(dir))
            return;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameToGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var wf in _files.Values)
        {
            var d = Path.GetDirectoryName(wf.CurrentPath) ?? "";
            if (!dir.Equals(d, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileName(wf.CurrentPath);
            names.Add(name);
            nameToGuid[name] = wf.Guid;
        }
        if (names.Count == 0)
            return;

        var fsw = new FileSystemWatcher(dir)
        {
            NotifyFilter =
                NotifyFilters.LastWrite
                | NotifyFilters.FileName
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            InternalBufferSize = 32 * 1024,
            IncludeSubdirectories = false,
        };
        foreach (var n in names)
            fsw.Filters.Add(n);
        fsw.Changed += (_, e) => Route(e.FullPath);
        fsw.Created += (_, e) => Route(e.FullPath);
        fsw.Renamed += (_, e) => OnRenamed(e);
        fsw.Error += OnError;
        fsw.EnableRaisingEvents = true;
        _dirs[dir] = new DirWatch { Fsw = fsw, NameToGuid = nameToGuid };
    }

    private void Route(string fullPath)
    {
        Guid guid;
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(fullPath) ?? "";
            var name = Path.GetFileName(fullPath);
            if (!_dirs.TryGetValue(dir, out var dw))
                return;
            if (!dw.NameToGuid.TryGetValue(name, out guid))
                return;
            // 旧名在宽限期内出现事件(重建/改动)= 上一条改名是保存套路,取消待定的跟随
            if (_pending.Remove(guid, out var pm))
            {
                try
                {
                    pm.Timer.Dispose();
                }
                catch { }
            }
        }
        _pipeline.OnFileChanged(guid);
    }

    private void CommitMove(Guid guid)
    {
        string? newPath = null;
        lock (_gate)
        {
            if (_pending.Remove(guid, out var pm))
            {
                try
                {
                    pm.Timer.Dispose();
                }
                catch { }
                if (_files.ContainsKey(guid))
                    newPath = pm.NewPath;
            }
        }
        if (newPath is null)
            return;
        try
        {
            FileMoved?.Invoke(guid, newPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FileMoved 处理出错");
        }
    }

    // Renamed:新路径命中被监控名 = 原子保存(temp → 目标名),照常防抖;
    // 旧路径命中被监控名而新名不同 = 改名挪走,进宽限期(2s 内旧名重建则判定为保存套路,不跟随)。
    private void OnRenamed(RenamedEventArgs e)
    {
        Route(e.FullPath);

        Guid guid;
        lock (_gate)
        {
            var oldDir = Path.GetDirectoryName(e.OldFullPath) ?? "";
            var oldName = Path.GetFileName(e.OldFullPath);
            if (!_dirs.TryGetValue(oldDir, out var dw))
                return;
            if (!dw.NameToGuid.TryGetValue(oldName, out guid))
                return;
            if (string.Equals(e.FullPath, e.OldFullPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (_pending.TryGetValue(guid, out var prev))
            {
                try
                {
                    prev.Timer.Dispose();
                }
                catch { }
            }
            var pm = new PendingMove { Guid = guid, NewPath = e.FullPath };
            pm.Timer = new Timer(_ => CommitMove(guid), null, MoveGrace, Timeout.InfiniteTimeSpan);
            _pending[guid] = pm;
        }
    }

    private async void OnError(object? sender, ErrorEventArgs e)
    {
        try
        {
            var fsw = (FileSystemWatcher)sender!;
            var dir = fsw.Path;
            _log.LogWarning(e.GetException(), "FSW error on {Dir}, 1s 后重建 + catch-up", dir);
            try
            {
                fsw.Dispose();
            }
            catch { }
            await Task.Delay(1000);
            List<WatchedFile> snap;
            lock (_gate)
            {
                EnsureDir(dir);
                snap = FilesInDir(dir);
            }
            foreach (var wf in snap)
                _pipeline.EnqueueCatchup(wf.Guid, wf.CurrentPath, wf.DisplayName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OnError 处理出错");
        }
    }

    private void HealthPoll()
    {
        var missing = new List<(Guid guid, string? msg)>();
        var rearmDirs = new List<string>();
        lock (_gate)
        {
            foreach (var wf in _files.Values)
            {
                var dir = Path.GetDirectoryName(wf.CurrentPath) ?? "";
                if (!Directory.Exists(dir))
                {
                    if (_dirs.TryGetValue(dir, out var dw))
                    {
                        try
                        {
                            dw.Fsw.Dispose();
                        }
                        catch { }
                        _dirs.Remove(dir);
                    }
                    missing.Add((wf.Guid, "父目录不存在（被改名/移动？）"));
                    continue;
                }
                if (!_dirs.ContainsKey(dir))
                {
                    if (!rearmDirs.Contains(dir))
                        rearmDirs.Add(dir);
                }
                else if (!File.Exists(wf.CurrentPath))
                {
                    missing.Add((wf.Guid, "文件不存在（被删除/改名？）"));
                }
            }
            foreach (var d in rearmDirs)
            {
                try
                {
                    EnsureDir(d);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "重武装 {Dir} 失败", d);
                }
            }
        }
        foreach (var d in rearmDirs)
        {
            foreach (var wf in FilesInDir(d))
            {
                _pipeline.EnqueueCatchup(wf.Guid, wf.CurrentPath, wf.DisplayName);
                _pipeline.MarkHealth(wf.Guid, FileHealth.Watching, null);
            }
        }
        foreach (var (guid, msg) in missing)
            _pipeline.MarkHealth(guid, FileHealth.Missing, msg);
    }

    private async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;
        try
        {
            _log.LogInformation("电源恢复,重建监听 + 全量 catch-up");
            List<WatchedFile> snap;
            lock (_gate)
            {
                foreach (var d in _dirs.Values)
                {
                    try
                    {
                        d.Fsw.Dispose();
                    }
                    catch { }
                }
                _dirs.Clear();
                // 逐个武装,单个坏目录不中断后面的健康文件(评审高危:恢复循环中断)
                foreach (var wf in _files.Values)
                {
                    try
                    {
                        EnsureDir(Path.GetDirectoryName(wf.CurrentPath) ?? "");
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "恢复时无法武装 {Path}", wf.CurrentPath);
                    }
                }
                snap = _files.Values.ToList();
            }
            foreach (var wf in snap)
                _pipeline.EnqueueCatchup(wf.Guid, wf.CurrentPath, wf.DisplayName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "电源恢复处理出错");
        }
    }

    private List<WatchedFile> FilesInDir(string dir) =>
        _files
            .Values.Where(w =>
                (Path.GetDirectoryName(w.CurrentPath) ?? "").Equals(
                    dir,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

    public void Dispose()
    {
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }
        _healthPoll.Dispose();
        lock (_gate)
        {
            foreach (var pm in _pending.Values)
            {
                try
                {
                    pm.Timer.Dispose();
                }
                catch { }
            }
            _pending.Clear();
            foreach (var d in _dirs.Values)
            {
                try
                {
                    d.Fsw.Dispose();
                }
                catch { }
            }
            _dirs.Clear();
        }
    }
}
