using Keen.Models;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace Keen.Services;

// 保留策略清理(#1):按 keepLast / maxAgeDays 删旧版本(blob + DB 行)。
// 启动后 10 分钟首跑,之后每 12 小时;也可由设置窗「立即清理一次」带参数手动触发。
// 作用于全部文件(含已停止监控的)——否则停止文件的 blob 永远不被清理,库无限膨胀。
internal sealed class RetentionService : IDisposable
{
    private readonly VaultIndex _index;
    private readonly VaultStore _store;
    private readonly ConfigService _config;
    private readonly ILogger<RetentionService> _log;
    private readonly Timer _timer;

    public RetentionService(
        VaultIndex index,
        VaultStore store,
        ConfigService config,
        ILogger<RetentionService> log
    )
    {
        _index = index;
        _store = store;
        _config = config;
        _log = log;
        _timer = new(_ => _ = RunAsync(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(12));
    }

    public async Task RunAsync() =>
        await RunAsync(_config.Current.RetainKeepLast, _config.Current.RetainMaxAgeDays);

    // 带参数版本:设置窗「立即清理一次」用当前 UI 值跑,不落盘任何设置。
    public async Task RunAsync(int keep, int age)
    {
        if (keep <= 0 && age <= 0)
            return;
        try
        {
            var files = await _index.LoadAllWatchedFilesAsync();
            int total = 0;
            foreach (var wf in files)
            {
                var rels = await _index.PruneAsync(wf.Guid, keep, age);
                foreach (var rel in rels)
                    _store.DeleteBlob(rel);
                total += rels.Count;
            }
            if (total > 0)
                _log.LogInformation("保留策略清理了 {N} 个旧版本", total);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "保留策略清理出错");
        }
    }

    public void Dispose() => _timer.Dispose();
}
