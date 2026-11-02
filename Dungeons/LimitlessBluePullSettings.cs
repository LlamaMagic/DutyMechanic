using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DutyMechanic.Dungeons;

// A split tank must not automatically claim the other tank's add. Own only
// temporary, in-memory settings and never save the character's preferences.
internal sealed class LimitlessBluePullSettings
{
    private readonly List<(object Owner, PropertyInfo Property, bool Original)> owned = new();
    internal bool Active => owned.Count != 0;

    internal bool Apply(object instance, string[] names)
    {
        if (Active && ReferenceEquals(owned[0].Owner, instance)) return true;
        // A hot-reloaded routine can replace its singleton mid-phase. Release
        // the old owner before leasing the new one, never reuse its snapshot.
        if (Active) Restore();
        if (instance == null || names == null || names.Length == 0) return false;
        var properties = names.Select(n => instance.GetType().GetProperty(n)).ToArray();
        if (properties.Any(p => p == null || p.PropertyType != typeof(bool) || !p.CanRead || !p.CanWrite)) return false;
        try
        {
            foreach (var property in properties)
            {
                bool original = (bool)property.GetValue(instance);
                owned.Add((instance, property, original));
                property.SetValue(instance, false);
            }
            return true;
        }
        catch { Restore(); return false; }
    }

    internal void Restore()
    {
        foreach (var item in owned)
        {
            try
            {
                // Preserve a later change away from the forced value.
                if (item.Property.GetValue(item.Owner) is false)
                    item.Property.SetValue(item.Owner, item.Original);
            }
            catch { /* A replaced routine may already have disposed its settings. */ }
        }
        owned.Clear();
    }
}
