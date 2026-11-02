using System;
using System.Collections.Generic;

namespace DutyMechanic.Dungeons;

// A marker can disappear before its impact. Retain the first unambiguous owner
// for the measured delivery window, and do not renew a lingering visual forever.
internal sealed class VaultSpreadState
{
    internal uint Target { get; private set; }
    internal DateTime Until { get; private set; }
    internal bool Resolved { get; private set; }
    internal bool Active(DateTime now) => Target != 0 && !Resolved && now < Until;

    internal void Observe(DateTime now, IReadOnlyList<uint> marked)
    {
        if (Target == 0 && marked.Count == 1)
        {
            Target = marked[0];
            Until = now.AddSeconds(7.6);
            Resolved = false;
        }
        else if (Target != 0 && !Resolved && now >= Until)
            Resolved = true;
        if (Resolved && marked.Count == 0) Reset();
    }

    internal void Reset()
    {
        Target = 0;
        Until = DateTime.MinValue;
        Resolved = false;
    }
}
