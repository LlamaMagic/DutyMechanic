using System;
using System.Collections.Generic;
using ff14bot;
using ff14bot.Objects;
using LlamaLibrary.Memory.Attributes;
using LlamaLibrary.Memory.PatternFinders;

namespace DutyMechanic;

/// <summary>A detached scalar record from a character's incoming-action buffer.</summary>
/// <param name="Sequence">Server action sequence; zero entries are excluded.</param>
/// <param name="Index">Target index within that action.</param>
/// <param name="Action">Numeric action identifier.</param>
/// <param name="Source">Source object identifier.</param>
/// <param name="Target">Recipient object identifier.</param>
public readonly record struct ProfileEffectReceipt(uint Sequence, byte Index, uint Action, ulong Source, ulong Target);

/// <summary>Reads guarded incoming-action receipts for profile mechanics on the bot thread.</summary>
/// <remarks>Results are buffered observations, not proof of event time or stream completeness.
/// Unsupported clients and unreadable actor layouts return false; callers must fail closed.</remarks>
public sealed class ProfileEffectReader
{
    private int slot;
    private DateTime nextInitialization;

    /// <summary>Reads one recipient's buffered entries for an action.</summary>
    /// <param name="actor">Live recipient wrapper, used only during this call.</param>
    /// <param name="action">Action identifier to select.</param>
    /// <param name="receipts">Detached scalar matches; empty when none were found.</param>
    /// <returns>True only when the supported layout was read successfully.</returns>
    public bool TryRead(BattleCharacter actor, uint action, out ProfileEffectReceipt[] receipts)
    {
        receipts = Array.Empty<ProfileEffectReceipt>();
        try
        {
            if (slot == 0 && DateTime.UtcNow >= nextInitialization)
            {
                // A transient initialization failure should not permanently
                // disable a later encounter; throttle repeat scans on failure.
                nextInitialization = DateTime.UtcNow.AddSeconds(5);
                Initialize();
            }
            if (slot == 0 || actor == null || !actor.IsValid || actor.Pointer == IntPtr.Zero) return false;
            using (Core.Memory.AcquireFrame())
            using (Core.Memory.TemporaryCacheState(false))
            {
                var table = Core.Memory.Read<IntPtr>(actor.Pointer);
                if (table == IntPtr.Zero) return false;
                var getter = Core.Memory.Read<IntPtr>(table + slot);
                var module = Core.Memory.Process.MainModule;
                long relative = getter.ToInt64() - module.BaseAddress.ToInt64();
                if (relative < 0 || relative + 8 > module.ModuleMemorySize) return false;
                var code = Core.Memory.ReadBytes(getter, 8);
                // The known getter is LEA RAX,[RCX+2900]; RET. Reject other
                // implementations rather than calling an unknown virtual method.
                if (code.Length != 8 || code[0] != 0x48 || code[1] != 0x8D || code[2] != 0x81 ||
                    code[7] != 0xC3 || BitConverter.ToInt32(code, 3) != 0x2900) return false;
                return Decode(Core.Memory.ReadBytes(actor.Pointer + 0x2900, 0xF00), action, actor.ObjectId, out receipts);
            }
        }
        catch (Exception) { return false; }
    }

    private void Initialize()
    {
        // Reuse Everkeep's instruction-masked allocator anchor and Global-only
        // contract; TC's virtual displacement differs. Require a unique match.
        if (LlamaLibrary.Memory.OffsetManager.ActiveRecord.RegionFlag != OffsetFlags.Global) return;
        const string pattern = "Search FF 90 ? ? ? ? 48 8B C8 E8 ? ? ? ? 48 8B D8 C7 40 ? ? ? ? ? 41 8B 4F ? 89 08";
        using var finder = new GreyMagicPf();
        var hits = finder.SearchMany(pattern);
        var module = Core.Memory.Process.MainModule;
        if (hits == null || hits.Length != 1 || module == null || hits[0].ToInt64() < 0 || hits[0].ToInt64() + 6 > module.ModuleMemorySize) return;
        int resolved = Core.Memory.Read<int>(Core.Memory.GetAbsolute(hits[0]) + 2);
        if (resolved == 0x290) slot = resolved;
    }

    // The validated buffer holds 32 entries of 0x78 bytes. Require the selected
    // recipient and a real sequence; neither a stale entry nor absence is a tell.
    internal static bool Decode(byte[] bytes, uint action, uint recipient, out ProfileEffectReceipt[] receipts)
    {
        receipts = Array.Empty<ProfileEffectReceipt>();
        if (bytes == null || bytes.Length != 0xF00 || action == 0 || recipient == 0) return false;
        var result = new List<ProfileEffectReceipt>();
        for (int offset = 0; offset < bytes.Length; offset += 0x78)
        {
            uint sequence = BitConverter.ToUInt32(bytes, offset);
            uint observedAction = BitConverter.ToUInt32(bytes, offset + 12);
            ulong target = BitConverter.ToUInt64(bytes, offset + 0x28);
            if (sequence == 0 || observedAction != action || target != recipient) continue;
            result.Add(new ProfileEffectReceipt(sequence, bytes[offset + 8], observedAction,
                BitConverter.ToUInt64(bytes, offset + 0x18), target));
        }
        receipts = result.ToArray();
        return true;
    }
}
