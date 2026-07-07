using System.Diagnostics;
using InventoryMonitor.Services;
using Xunit;

namespace InventoryMonitor.Tests;

/// <summary>
/// Verifies the thread-marshaling primitive that lets REST callbacks safely reach Terraria state:
/// inline execution on the main thread, cross-thread hand-off via the pump, exception propagation,
/// and the timeout guard.
/// </summary>
public class MainThreadDispatcherTests
{
    /// <summary>Pumps the dispatcher from the calling (test/"main") thread until the task finishes.</summary>
    private static void PumpUntilComplete(MainThreadDispatcher d, Task task, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            d.Process();
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("dispatcher pump did not complete the task in time");
            Thread.Sleep(1);
        }

        task.Wait();
    }

    [Fact]
    public void Invoke_On_Main_Thread_Runs_Inline()
    {
        var d = new MainThreadDispatcher();
        d.CaptureMainThread();

        Assert.True(d.OnMainThread);
        Assert.Equal(7, d.Invoke(() => 7, 1000));
    }

    [Fact]
    public void Invoke_From_Other_Thread_Marshals_Onto_Pumping_Thread()
    {
        var d = new MainThreadDispatcher();
        d.CaptureMainThread();
        int mainThreadId = Environment.CurrentManagedThreadId;

        int ranOnThread = -1;
        var task = Task.Run(() => d.Invoke(() =>
        {
            ranOnThread = Environment.CurrentManagedThreadId;
            return 123;
        }, 5000));

        PumpUntilComplete(d, task);

        Assert.Equal(123, task.Result);
        Assert.Equal(mainThreadId, ranOnThread); // the work ran on the pumping (main) thread
    }

    [Fact]
    public void Invoke_Off_Thread_With_No_Pump_Times_Out()
    {
        var d = new MainThreadDispatcher(); // never captured => calling thread is treated as non-main
        Assert.Throws<TimeoutException>(() => d.Invoke(() => 1, 50));
    }

    [Fact]
    public void Invoke_On_Main_Thread_Propagates_Exception_Directly()
    {
        var d = new MainThreadDispatcher();
        d.CaptureMainThread();

        Assert.Throws<InvalidOperationException>(() => d.Invoke<int>(() => throw new InvalidOperationException("boom"), 1000));
    }

    [Fact]
    public void Invoke_Off_Thread_Propagates_Exception_Through_Pump()
    {
        var d = new MainThreadDispatcher();
        d.CaptureMainThread();

        Exception? captured = null;
        var task = Task.Run(() =>
        {
            try { d.Invoke<int>(() => throw new InvalidOperationException("boom"), 5000); }
            catch (Exception ex) { captured = ex; }
        });

        PumpUntilComplete(d, task);

        var ex = Assert.IsType<InvalidOperationException>(captured);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void Process_Runs_Queued_Work_In_FIFO_Order()
    {
        var d = new MainThreadDispatcher();
        d.CaptureMainThread();

        var order = new List<int>();
        var t1 = Task.Run(() => d.Invoke(() => { lock (order) order.Add(1); return 0; }, 5000));
        Thread.Sleep(20); // ensure t1 enqueues first
        var t2 = Task.Run(() => d.Invoke(() => { lock (order) order.Add(2); return 0; }, 5000));

        PumpUntilComplete(d, Task.WhenAll(t1, t2));

        Assert.Equal(new[] { 1, 2 }, order);
    }
}
