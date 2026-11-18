using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DutyMechanic;

// Optional, read-only visit handoff. Unknown must not be interpreted as manual
// ownership: an older or broken Farmer may already have sent its farewell.
public static class ProfilePartyChat
{
    public enum Result { NoOwner, FarmerOwns, Unknown }

    // An unknown bridge cannot safely imply that the profile owns a farewell.
    public static bool UseProfileFarewell(uint dutyId) =>
        Resolve(AppDomain.CurrentDomain.GetAssemblies(), dutyId) == Result.NoOwner;

    // A positive visit owner identifies the protected live-party handoff even
    // if the roster temporarily disappears during wipe recovery.
    public static bool FarmerOwnsVisit(uint dutyId) =>
        Resolve(AppDomain.CurrentDomain.GetAssemblies(), dutyId) == Result.FarmerOwns;
    public static Result Resolve(IEnumerable<Assembly> assemblies, uint dutyId)
    {
        bool legacyFarmer = false, failed = false;
        foreach (Assembly assembly in assemblies)
        {
            try
            {
                Type? bridge = assembly.GetType("PandaFarmerWPF.Runtime.DutyPartyMessageOwnership", false);
                if (bridge == null)
                {
                    if (string.Equals(assembly.GetName().Name, "PandaFarmerWPF", StringComparison.OrdinalIgnoreCase)) legacyFarmer = true;
                    continue;
                }
                MethodInfo? method = bridge.GetMethod("OwnsVisit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(uint) }, null);
                if (method == null || method.ReturnType != typeof(bool)) { failed = true; continue; }
                if ((bool)method.Invoke(null, new object[] { dutyId })!) return Result.FarmerOwns;
            }
            catch (Exception) { failed = true; }
        }
        if (failed || legacyFarmer) return Result.Unknown;
        return Result.NoOwner;
    }
}
