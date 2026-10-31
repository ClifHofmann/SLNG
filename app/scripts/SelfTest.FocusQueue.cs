using System;
using System.Collections.Generic;
using SLNG.Core;

namespace SLNG.App;

public partial class SelfTest
{
    /// <summary>FEAT-PERF-27: the main-thread work queue serves the focus avatar's items ahead of older ones, and keeps
    /// the order of everything else.</summary>
    private static Check CheckFocusQueue()
    {
        const string Name = "work queue: the focus avatar's items run first";
        var focusAgent = Guid.Parse("f0c05a11-0000-0000-0000-000000000001");
        var otherAgent = Guid.Parse("f0c05a11-0000-0000-0000-000000000002");
        var ran = new List<string>();
        var before = PrioritySubject.Focus;
        try
        {
            PrioritySubject.Set(Guid.Empty);
            // Drain whatever the boot left, so only these items are in the lane.
            MainThreadWorkQueue.Pump(double.MaxValue);

            for (int i = 0; i < 4; i++)
            {
                int n = i;
                MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => ran.Add("other" + n),
                    label: "selftest.focus", subject: otherAgent);
            }
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => ran.Add("focus0"),
                label: "selftest.focus", subject: focusAgent);
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => ran.Add("focus1"),
                label: "selftest.focus", subject: focusAgent);

            PrioritySubject.Set(focusAgent); // set AFTER queuing: the already waiting items are re-ranked
            int pending = MainThreadWorkQueue.PendingFor(focusAgent);
            MainThreadWorkQueue.Pump(double.MaxValue);

            string order = string.Join(",", ran);
            bool ok = pending == 2 && order == "focus0,focus1,other0,other1,other2,other3";
            return new Check(Name, ok, ok ? $"order {order}" : $"pending={pending}, order {order}");
        }
        finally
        {
            PrioritySubject.Set(before);
        }
    }
}
