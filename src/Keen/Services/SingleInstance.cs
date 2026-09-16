namespace Keen.Services;

// 单实例闸(M1 只做 Mutex;M2 起再加对 keen.sqlite 的独占文件锁,覆盖跨会话/提权场景)。
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Keen-SingleInstance-v1";
    private Mutex? _mutex;
    private bool _owned;

    // 返回 false 表示已有另一个实例持有;调用方应直接退出。
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);
        _owned = createdNew;
        return createdNew;
    }

    public void Dispose()
    {
        if (_mutex is null)
            return;
        try
        {
            if (_owned)
                _mutex.ReleaseMutex();
        }
        catch { }
        _mutex.Dispose();
    }
}
