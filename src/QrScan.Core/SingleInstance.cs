namespace QrScan.Core;

/// <summary>
/// 命名 Mutex 单实例保护。用途在进程启动期（早于 DI）→ static。
/// 用 <c>Local\</c> 前缀：每用户独立，多用户同时登录互不干扰。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\QrScan.SingleInstance";

    /// <summary>拿到所有权返回 true 并给出释放句柄；已有实例在运行返回 false。</summary>
    public static bool TryAcquire(out IDisposable? release)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            release = null;
            return false;
        }

        release = new MutexReleaser(mutex);
        return true;
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 未持有所有权，忽略 */ }

            mutex.Dispose();
        }
    }
}
