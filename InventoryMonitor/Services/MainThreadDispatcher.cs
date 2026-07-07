using System.Collections.Concurrent;

namespace InventoryMonitor.Services;

/// <summary>
/// Marshals work onto the main server thread. Terraria's player arrays and packet sends are not
/// thread-safe; in-game commands already run on the main thread, but REST callbacks run on the
/// HTTP listener thread and must hop over. <see cref="Process"/> is pumped once per GameUpdate.
/// </summary>
public sealed class MainThreadDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private volatile int _mainThreadId = -1;

    /// <summary>Records the calling thread as "main". Call from within GameUpdate.</summary>
    public void CaptureMainThread() => _mainThreadId = Environment.CurrentManagedThreadId;

    public bool OnMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>Drains queued work. Must be called on the main thread (from GameUpdate).</summary>
    public void Process()
    {
        while (_queue.TryDequeue(out var action))
        {
            try { action(); }
            catch { /* per-job exceptions are surfaced to the caller via Invoke's capture */ }
        }
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the main thread and returns its result. Executes inline
    /// when already on the main thread; otherwise enqueues and blocks (the caller's thread) until
    /// the next GameUpdate pumps it, or the timeout elapses.
    /// </summary>
    public T Invoke<T>(Func<T> func, int timeoutMs)
    {
        if (OnMainThread)
            return func();

        using var done = new ManualResetEventSlim(false);
        T result = default!;
        Exception? error = null;

        _queue.Enqueue(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });

        if (!done.Wait(timeoutMs))
            throw new TimeoutException("Main-thread operation timed out.");

        if (error is not null)
            throw error;

        return result;
    }

    public void Invoke(Action action, int timeoutMs) =>
        Invoke<object?>(() => { action(); return null; }, timeoutMs);
}
