using System;
using System.Reflection;

namespace DutyMechanic.Dungeons;
// Magitek's SCH multi-dot reads Combat.Enemies independently of OrderBot's target
// provider. Use its verified public preference instead of patching private fields or
// replacing the rotation. The setter saves immediately: normal release restores it,
// but an ungraceful process termination can leave multi-dot disabled (safe fallback).
internal sealed class LabyrinthRoutineLease : IDisposable
{
    private readonly object settings;
    private readonly PropertyInfo property;
    private readonly bool previous;
    private bool released;
    internal LabyrinthRoutineLease(object settings, PropertyInfo property)
    {
        this.settings = settings;
        this.property = property;
        previous = (bool)property.GetValue(settings);
        if (previous)
            property.SetValue(settings, false);
    }

    /// <summary>Restores the captured preference unless another owner has already changed it.</summary>
    public void Dispose()
    {
        if (released)
            return;
        // Do not overwrite an explicit user re-enable. Setting false twice also must
        // not lose the original true value during repeated controller pulses.
        if (previous && !(bool)property.GetValue(settings))
            property.SetValue(settings, true);
        released = true;
    }
}
