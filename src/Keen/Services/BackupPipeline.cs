using System.Collections.Concurrent;
using System.Threading.Channels;
using Keen.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace Keen.Services;

// 5 段备份管线(不变量①~⑦)。每个被监控文件一个有界 Channel + 单消费者;全局 SemaphoreSlim(4) 节流。
// ① 500ms 尾沿防抖 → ② 写静止/锁重试(20 次 ~30s)+ FileId 校验打开 → ④ 流复制+SHA256 → ⑤ 持久落库+插索引。
// 消费循环内 try/catch,单个坏作业不杀消费线程。
// CaptureDirectAsync:供恢复(PreRestoreSnapshot)等需要同步等待结果的特殊作业用,走同一通道串行。
// 失败标记:重试耗尽的作业留 marker,由 _failureRetry 定时重投,补 AV 锁静默丢版的洞。
internal sealed class BackupPipeline : IDisposable
{
    private readonly VaultStore _store;
    private readonly VaultIndex _index;
    private readonly ILogger<BackupPipeline> _log;

    private readonly SemaphoreSlim _globalThrottle = new(4);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, FileState> _states = new();
    private readonly ConcurrentDictionary<Guid, Timer> _debounce = new();
    private readonly object _debounceLock = new(); // FSW 事件并发触发时保证防抖 timer 换装原子
    private readonly Random _jitter = new();
    private readonly ConcurrentDictionary<Guid, FailureMarker> _failures = new();
    private readonly Timer _failureRetry;

    public event Action<VersionEntry>? VersionCaptured;
    public event Action<Guid, FileHealth, string?>? HealthChanged;

    // 捕获失败(重试耗尽)时触发,供通知用
    public event Action<Guid, string>? CaptureFailed;

    public BackupPipeline(VaultStore store, VaultIndex index, ILogger<BackupPipeline> log)
    {
        _store = store;
        _index = index;
        _log = log;
        _failureRetry = new Timer(
            _ => RetryFailures(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        );
    }

    private sealed class FileState
    {
        public Guid Guid;
        public string Path = "";
        public string DisplayName = "";
        public Channel<BackupJob> Channel = null!;
        public Task Consumer = Task.CompletedTask;
        public int Seq;
        public long LastTicks;
        public string? LastSha;
        public long LastSize;
    }

    internal sealed class FailureMarker
    {
        public Guid Guid;
        public string Path = "";
        public string DisplayName = "";
        public string Error = "";
    }

    public bool IsRegistered(Guid guid) => _states.ContainsKey(guid);

    // 同目录改名跟踪:更新管线内的路径/显示名(不动通道与消费者)。
    public void UpdatePath(Guid guid, string path, string displayName)
    {
        if (_states.TryGetValue(guid, out var st))
        {
            st.Path = path;
            st.DisplayName = displayName;
        }
    }

    public async Task RegisterAsync(Guid guid, string path, string displayName)
    {
        if (_states.ContainsKey(guid))
            return;
        var st = new FileState
        {
            Guid = guid,
            Path = path,
            DisplayName = displayName,
        };
        st.Channel = Channel.CreateBounded<BackupJob>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        await ReSeedStateAsync(st);
        if (_states.TryAdd(guid, st))
            st.Consumer = Task.Run(() => ConsumeAsync(st));
    }

    private async Task ReSeedStateAsync(FileState st)
    {
        var last = await _index.GetLastVersionAsync(st.Guid);
        if (last is { } lv)
        {
            st.LastSha = lv.Sha256;
            st.LastSize = lv.SizeBytes;
            st.LastTicks = lv.CapturedAtTicks;
            st.Seq = lv.Seq;
        }
    }

    public async Task ReSeedBaselineAsync(Guid guid)
    {
        if (_states.TryGetValue(guid, out var st))
            await ReSeedStateAsync(st);
    }

    // 停止某文件:关通道(拒新作业)→ 等在途作业排空(带超时)→ 移除状态。
    // 移除是关键:否则重新 RegisterAsync 会因 ContainsKey 早退,留下打不进作业的死通道(评审致命#1)。
    public async Task StopFileAsync(Guid guid, TimeSpan drainTimeout)
    {
        if (_debounce.TryRemove(guid, out var t))
            t.Dispose();
        if (_states.TryRemove(guid, out var st))
        {
            st.Channel.Writer.TryComplete();
            try
            {
                await Task.WhenAny(st.Consumer, Task.Delay(drainTimeout));
            }
            catch { }
        }
        _failures.TryRemove(guid, out _);
    }

    public void OnFileChanged(Guid guid)
    {
        if (!_states.TryGetValue(guid, out var st))
        {
            // 激活文件的监听事件到了、管线状态却不在 = 断链,之前这里是静默 return,
            // 追踪死亡零痕迹。记下来让它现形。
            _log.LogWarning("收到 {Guid} 的变更事件,但管线状态不存在(已停止或断链)", guid);
            return;
        }
        var path = st.Path;
        var name = st.DisplayName;
        Timer t;
        lock (_debounceLock)
        {
            if (_debounce.TryGetValue(guid, out var prev))
                prev.Dispose();
            t = new Timer(
                _ =>
                    Enqueue(
                        new BackupJob
                        {
                            WatchedGuid = guid,
                            SourcePath = path,
                            DisplayName = name,
                        }
                    ),
                null,
                500,
                Timeout.Infinite
            );
            _debounce[guid] = t;
        }
    }

    public void EnqueueCatchup(Guid guid, string path, string displayName) =>
        Enqueue(
            new BackupJob
            {
                WatchedGuid = guid,
                SourcePath = path,
                DisplayName = displayName,
            }
        );

    public void Enqueue(BackupJob job)
    {
        if (_states.TryGetValue(job.WatchedGuid, out var st))
        {
            if (!st.Channel.Writer.TryWrite(job))
                _log.LogWarning("队列已满,丢弃 {Guid} 的作业(消费者被长时间阻塞)", job.WatchedGuid);
        }
    }

    public async Task<VersionEntry?> CaptureDirectAsync(
        Guid guid,
        string path,
        string displayName,
        VersionKind kind,
        bool bypassDedupe,
        CancellationToken ct
    )
    {
        if (!_states.TryGetValue(guid, out var st))
            throw new InvalidOperationException("文件未在管线注册：" + guid);
        var tcs = new TaskCompletionSource<VersionEntry?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var job = new BackupJob
        {
            WatchedGuid = guid,
            SourcePath = path,
            DisplayName = displayName,
            Kind = kind,
            BypassDedupe = bypassDedupe,
            Tcs = tcs,
        };
        if (!st.Channel.Writer.TryWrite(job))
            return null;
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    public void MarkHealth(Guid guid, FileHealth h, string? msg) =>
        HealthChanged?.Invoke(guid, h, msg);

    // 周期性重投失败标记,让被 AV 锁等临时原因弄丢的版本有机会补上。
    private void RetryFailures()
    {
        if (_failures.IsEmpty)
            return;
        foreach (var (guid, m) in _failures)
            Enqueue(
                new BackupJob
                {
                    WatchedGuid = guid,
                    SourcePath = m.Path,
                    DisplayName = m.DisplayName,
                }
            );
    }

    private async Task ConsumeAsync(FileState st)
    {
        var token = _cts.Token;
        await foreach (var job in st.Channel.Reader.ReadAllAsync(token))
        {
            VersionEntry? result = null;
            try
            {
                result = await ProcessJobAsync(st, job);
            }
            catch (OperationCanceledException)
            {
                job.Tcs?.TrySetCanceled();
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "处理 {Guid} 的备份作业失败", st.Guid);
                HealthChanged?.Invoke(st.Guid, FileHealth.Degraded, ex.Message);
            }
            job.Tcs?.TrySetResult(result);
        }
    }

    private async Task<VersionEntry?> ProcessJobAsync(FileState st, BackupJob job)
    {
        await _globalThrottle.WaitAsync(_cts.Token);
        HealthChanged?.Invoke(st.Guid, FileHealth.Syncing, null);
        try
        {
            const int maxAttempts = 20;
            for (int attempt = 0; attempt < maxAttempts && !_cts.IsCancellationRequested; attempt++)
            {
                try
                {
                    var info = new FileInfo(job.SourcePath);
                    if (!info.Exists)
                    {
                        HealthChanged?.Invoke(st.Guid, FileHealth.Missing, "源文件不存在");
                        _failures.TryRemove(st.Guid, out _);
                        return null;
                    }

                    // 大文件(>100MB)写静止:2 样本长度稳定,≥1s。每次尝试都查,
                    // 否则增长中的文件会在第 2 次尝试被中途开抄(评审撕裂版洞)。
                    if (info.Length > 100L << 20)
                    {
                        long s1 = info.Length;
                        await Task.Delay(1000, _cts.Token);
                        var info2 = new FileInfo(job.SourcePath);
                        if (!info2.Exists)
                        {
                            HealthChanged?.Invoke(st.Guid, FileHealth.Missing, null);
                            _failures.TryRemove(st.Guid, out _);
                            return null;
                        }
                        if (info2.Length != s1)
                        {
                            await Task.Delay(DelayFor(attempt), _cts.Token);
                            continue;
                        }
                    }

                    using var src = SourceFile.OpenVerified(job.SourcePath);

                    int seq = st.Seq + 1;
                    long ticks = Math.Max(st.LastTicks, DateTime.UtcNow.Ticks);
                    if (ticks <= st.LastTicks)
                        ticks = st.LastTicks + 1;
                    var month = new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM");
                    var ext = Path.GetExtension(job.DisplayName);
                    var rel = $"{st.Guid}/{month}/{ticks:D19}_{seq:D4}{ext}";

                    var copy = await _store.WriteBlobAsync(src, rel, progress: null, _cts.Token);

                    // 内容判重(始终启用):新捕获的 SHA-256 与上一版相同 → 不落库(blob 删掉、不插行)。
                    // 复制时哈希本来就是顺手算的,零额外成本。这挡住消费被卡后积压作业
                    // 连发一串字节相同版本的噪音;PreRestoreSnapshot 等语义标记(bypass)不受影响。
                    if (!job.BypassDedupe && st.LastSha == copy.sha256)
                    {
                        _store.DeleteBlob(rel);
                        st.LastTicks = ticks;
                        _failures.TryRemove(st.Guid, out _);
                        HealthChanged?.Invoke(st.Guid, FileHealth.Watching, null);
                        return null;
                    }

                    var entry = await _index.InsertVersionAsync(
                        st.Guid,
                        ticks,
                        seq,
                        job.Kind,
                        rel,
                        job.SourcePath,
                        job.DisplayName,
                        copy.size,
                        copy.sha256
                    );
                    st.Seq = seq;
                    st.LastSha = copy.sha256;
                    st.LastSize = copy.size;
                    st.LastTicks = ticks;
                    _failures.TryRemove(st.Guid, out _);
                    VersionCaptured?.Invoke(entry);
                    HealthChanged?.Invoke(st.Guid, FileHealth.Watching, null);
                    return entry;
                }
                catch (FileNotFoundException)
                {
                    HealthChanged?.Invoke(st.Guid, FileHealth.Missing, null);
                    _failures.TryRemove(st.Guid, out _);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // 注意:不加 when(attempt<max-1) 过滤——最后一次尝试的异常必须落到循环外的
                // 「重试耗尽」路径(写失败标记 + 通知),否则失败标记机制被整个绕过(评审高危#2)。
                catch (StaleHandleException)
                {
                    if (attempt >= maxAttempts - 1)
                        break;
                    await Task.Delay(DelayFor(attempt), _cts.Token);
                }
                catch (IOException ex)
                {
                    if (attempt >= maxAttempts - 1)
                        break;
                    _log.LogWarning(
                        "捕获重试 {Guid} attempt {A}: {Msg}",
                        st.Guid,
                        attempt,
                        ex.Message
                    );
                    await Task.Delay(DelayFor(attempt), _cts.Token);
                }
                catch (SqliteException ex)
                {
                    if (attempt >= maxAttempts - 1)
                        break;
                    _log.LogWarning(
                        "索引写入重试 {Guid} attempt {A}: {Msg}",
                        st.Guid,
                        attempt,
                        ex.Message
                    );
                    await Task.Delay(DelayFor(attempt), _cts.Token);
                }
            }
            // 重试耗尽:留失败标记,等 _failureRetry 重投;通知 + 健康降级。
            _failures[st.Guid] = new FailureMarker
            {
                Guid = st.Guid,
                Path = job.SourcePath,
                DisplayName = job.DisplayName,
                Error = "重试耗尽",
            };
            HealthChanged?.Invoke(st.Guid, FileHealth.Failing, "重试耗尽，等待自动重投");
            CaptureFailed?.Invoke(st.Guid, job.DisplayName);
            return null;
        }
        finally
        {
            _globalThrottle.Release();
        }
    }

    private int DelayFor(int attempt)
    {
        int b = Math.Min(100 << attempt, 2000);
        lock (_jitter)
            return b + (int)(_jitter.NextDouble() * b * 0.2);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        foreach (var st in _states.Values)
            st.Channel.Writer.TryComplete();
        foreach (var t in _debounce.Values)
            t.Dispose();
        _debounce.Clear();
        try
        {
            await Task.WhenAll(_states.Values.Select(s => s.Consumer));
        }
        catch { }
    }

    public void Dispose()
    {
        _failureRetry.Dispose();
        _cts.Dispose();
        _globalThrottle.Dispose();
    }
}
