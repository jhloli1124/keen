using Keen.Models;
using Microsoft.Extensions.Logging;

namespace Keen.Services;

// 集中被监控文件的增删 + 启动武装。主窗、IPC、恢复都走这里,避免逻辑重复。
// 「移除」= 软删除(is_active=0):停止监控,但 watched_file 行与全部 version 历史(含 blob)保留。
// 「彻底删除」= Purge:排空管线 → 删历史行 + blob + watched_file 行 + guid 目录整树。
internal sealed class WatchService
{
    private readonly VaultIndex _index;
    private readonly BackupPipeline _pipeline;
    private readonly FileWatchService _watcher;
    private readonly ConfigService _config;
    private readonly VaultStore _store;
    private readonly ILogger<WatchService> _log;

    // UI 线程(按钮/拖拽)与 IPC 线程(右键)可能并发添加同一文件;串行化查-插,防双记录(评审并发#4)
    private readonly SemaphoreSlim _addGate = new(1, 1);

    public WatchService(
        VaultIndex index,
        BackupPipeline pipeline,
        FileWatchService watcher,
        ConfigService config,
        VaultStore store,
        ILogger<WatchService> log
    )
    {
        _index = index;
        _pipeline = pipeline;
        _watcher = watcher;
        _config = config;
        _store = store;
        _log = log;
        _watcher.FileMoved += OnFileMoved;
    }

    public event Action<WatchedFile>? FileAdded;
    public event Action<WatchedFile>? FileReactivated;
    public event Action<Guid>? FileDeactivated; // 软删除(留历史)
    public event Action<Guid>? FileRemoved; // 彻底删除(清历史)

    // 被监控文件被同目录改名:(guid, 新路径, 新显示名)。UI 更新行用。
    public event Action<Guid, string, string>? FileMovedUi;

    public async Task InitializeAsync()
    {
        var files = await _index.LoadActiveWatchedFilesAsync();
        foreach (var wf in files)
        {
            await _pipeline.RegisterAsync(wf.Guid, wf.CurrentPath, wf.DisplayName);
            // 单个坏路径(如盘不在)不能让整个启动崩掉
            try
            {
                _watcher.Add(wf);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "启动时无法武装监听:{Path}", wf.CurrentPath);
            }
        }
    }

    // 返回 null = 拒绝(UNC / 保险库内 / 目录 / 已存在 / 非法路径)。
    public async Task<WatchedFile?> AddFileAsync(string path)
    {
        await _addGate.WaitAsync();
        try
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }

            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            // 用「当前生效的保险库根」(而非 config 里的新根)做排除,防止改库位置后重启前把库内文件存进库(评审不变量⑥)
            foreach (var root in VaultRoots())
            {
                if (
                    full.StartsWith(
                        root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    ) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                )
                    return null;
            }
            if (Directory.Exists(full))
                return null;

            // 已存在(含已软删除的):直接重新激活,复用原历史
            var existing = await _index.LoadAllWatchedFilesAsync();
            var hit = existing.FirstOrDefault(w =>
                string.Equals(w.CurrentPath, full, StringComparison.OrdinalIgnoreCase)
            );
            if (hit is not null)
            {
                if (!hit.IsActive)
                {
                    await _index.ReactivateWatchedFileAsync(hit.Guid);
                    await _pipeline.StopFileAsync(hit.Guid, TimeSpan.FromSeconds(2)); // 清可能的死状态
                    await _pipeline.RegisterAsync(hit.Guid, hit.CurrentPath, hit.DisplayName);
                    _watcher.Add(hit);
                    // 重新激活也补一版基线(停监控期间的改动不该留空档)
                    _pipeline.Enqueue(
                        new BackupJob
                        {
                            WatchedGuid = hit.Guid,
                            SourcePath = hit.CurrentPath,
                            DisplayName = hit.DisplayName,
                        }
                    );
                    hit.IsActive = true;
                    FileReactivated?.Invoke(hit);
                }
                return hit;
            }

            var wf = new WatchedFile
            {
                Guid = Guid.CreateVersion7(),
                CurrentPath = full,
                DisplayName = Path.GetFileName(full),
                AddedAtTicks = DateTime.UtcNow.Ticks,
                IsActive = true,
            };
            await _index.AddWatchedFileAsync(wf);
            await _pipeline.RegisterAsync(wf.Guid, wf.CurrentPath, wf.DisplayName);
            _watcher.Add(wf);
            // 添加即存第一版(当前状态作为基线),否则要等下次保存才有版本。
            _pipeline.Enqueue(
                new BackupJob
                {
                    WatchedGuid = wf.Guid,
                    SourcePath = wf.CurrentPath,
                    DisplayName = wf.DisplayName,
                }
            );
            FileAdded?.Invoke(wf);
            _log.LogInformation("已添加监控:{Path}", full);
            return wf;
        }
        finally
        {
            _addGate.Release();
        }
    }

    private List<string> VaultRoots()
    {
        var roots = new List<string> { Path.GetFullPath(_store.Root) };
        try
        {
            var cfgRoot = Path.GetFullPath(_config.Current.VaultRoot);
            if (!roots.Contains(cfgRoot, StringComparer.OrdinalIgnoreCase))
                roots.Add(cfgRoot);
        }
        catch { }
        return roots;
    }

    // 软删除:停监控,留历史。
    public async Task RemoveAsync(Guid guid)
    {
        await _index.DeactivateWatchedFileAsync(guid);
        await _pipeline.StopFileAsync(guid, TimeSpan.FromSeconds(2));
        _watcher.Remove(guid);
        FileDeactivated?.Invoke(guid);
    }

    // 重新监控(复用原 GUID + 历史)。
    public async Task<bool> ReactivateAsync(Guid guid)
    {
        var wf = await _index.GetWatchedFileAsync(guid);
        if (wf is null)
            return false;
        await _index.ReactivateWatchedFileAsync(guid);
        await _pipeline.StopFileAsync(guid, TimeSpan.FromSeconds(2)); // 清可能的死状态
        await _pipeline.RegisterAsync(guid, wf.CurrentPath, wf.DisplayName);
        _watcher.Add(wf);
        wf.IsActive = true;
        _pipeline.Enqueue(
            new BackupJob
            {
                WatchedGuid = guid,
                SourcePath = wf.CurrentPath,
                DisplayName = wf.DisplayName,
            }
        );
        FileReactivated?.Invoke(wf);
        return true;
    }

    // 彻底删除:排空在途捕获 → 删历史行 + blob → 删 guid 目录整树(兜住晚到的孤儿 blob)。
    public async Task PurgeAsync(Guid guid)
    {
        var wf = await _index.GetWatchedFileAsync(guid);
        await _pipeline.StopFileAsync(guid, TimeSpan.FromSeconds(3));
        _watcher.Remove(guid);
        var rels = await _index.PurgeWatchedFileAsync(guid);
        foreach (var rel in rels)
            _store.DeleteBlob(rel);
        try
        {
            var guidDir = Path.Combine(_store.Root, guid.ToString());
            if (Directory.Exists(guidDir))
                Directory.Delete(guidDir, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "清理 {Guid} 的版本目录失败", guid);
        }
        _ = wf; // 保留查询以防未来需要;当前未用路径信息
        FileRemoved?.Invoke(guid);
    }

    // 同目录改名跟随:更新 DB 路径 + 管线路径 + 重建 watcher 过滤器。
    private async void OnFileMoved(Guid guid, string newPath)
    {
        try
        {
            var wf = await _index.GetWatchedFileAsync(guid);
            if (wf is null)
                return;
            var name = Path.GetFileName(newPath);
            var oldPath = wf.CurrentPath;
            await _index.UpdateWatchedFilePathAsync(guid, newPath, name);
            _pipeline.UpdatePath(guid, newPath, name);
            _watcher.Remove(guid); // 用旧路径定位旧目录,重建其过滤器
            wf.CurrentPath = newPath;
            wf.DisplayName = name;
            _watcher.Add(wf); // 按新路径武装(可能换了目录)
            _pipeline.Enqueue(
                new BackupJob
                {
                    WatchedGuid = guid,
                    SourcePath = newPath,
                    DisplayName = name,
                }
            ); // 改名即存一版新名基线
            FileMovedUi?.Invoke(guid, newPath, name);
            _log.LogInformation("被监控文件改名:{Old} -> {New}", oldPath, newPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "跟随重命名失败 {Guid}", guid);
        }
    }
}
