using System;
using System.Threading;
using SLNG.Core;

namespace SLNG.App;

public partial class SelfTest
{
    /// <summary>FEAT-PERF-32 step 1: under --diag the queue files how long an item waited from enqueue to run, per label.</summary>
    private static Check CheckWorkQueueWaitIsMeasured()
    {
        const string Name = "work queue: wait from enqueue to run is measured per label";
        const string Label = "selftest.wait";
        bool verbose = Diag.Verbose;
        try
        {
            MainThreadWorkQueue.Pump(double.MaxValue);
            MainThreadWorkQueue.TakeWaits(); // start from empty

            Diag.Verbose = true;
            for (int i = 0; i < 3; i++)
                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => { }, label: Label);
            Thread.Sleep(40);
            MainThreadWorkQueue.Pump(double.MaxValue);

            var waits = MainThreadWorkQueue.TakeWaits();
            waits.TryGetValue(Label, out var w);
            bool ok = w.Count == 3 && w.P50 >= 35 && w.Max >= 35 && w.Max < 5000;

            // Not under --diag: the clock is not read and nothing is filed.
            Diag.Verbose = false;
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => { }, label: Label);
            Thread.Sleep(5);
            MainThreadWorkQueue.Pump(double.MaxValue);
            bool silent = !MainThreadWorkQueue.TakeWaits().ContainsKey(Label);

            ok &= silent;
            return new Check(Name, ok, ok
                ? $"3 items waited p50 {w.P50:F0} ms, max {w.Max:F0} ms; nothing filed without --diag"
                : $"n={w.Count} (want 3), p50 {w.P50:F1} / max {w.Max:F1} ms (want >= 35), silent without --diag={silent}");
        }
        finally
        {
            Diag.Verbose = verbose;
        }
    }
}
