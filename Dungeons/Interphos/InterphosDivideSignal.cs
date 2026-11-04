using ff14bot;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>Read-only target identities from the reviewed line-marker layout.</summary>
/// <remarks>Offline semantic anchors are unique in Global, CN, TC and prior Global;
/// this runtime intentionally enables only the live-validated Global image. No
/// hooks or native calls. Hashing runs off-thread; all game reads stay on the pulse.</remarks>
internal sealed class InterphosDivideSignal
{
    private const string Image = "46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A";
    private GreyMagic.ExternalProcessMemory memory;
    private Task<string> hash;
    private int pid;
    private bool rejected, validated;

    internal void Prepare()
    {
        if (hash != null || rejected)
        {
            return;
        }

        memory = Core.Memory;
        pid = memory.Process.Id;
        string path = memory.Process.MainModule.FileName;
        hash = Task.Run(() => { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); });
        _ = hash.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private bool Anchor(IntPtr image, int rva, string pattern)
    {
        string[] tokens = pattern.Split(' ');
        byte[] bytes = memory.ReadBytes(image + rva, tokens.Length);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] != "??" && bytes[i] != Convert.ToByte(tokens[i], 16))
            {
                return false;
            }
        }

        return true;
    }

    internal bool TryRead(BattleCharacter boss, out InterphosDivide.Marker[] markers)
    {
        markers = [];
        if (rejected || hash == null || !hash.IsCompleted)
        {
            return false;
        }

        if (!hash.IsCompletedSuccessfully || hash.Result != Image || !ReferenceEquals(memory, Core.Memory) || memory.Process.Id != pid)
        {
            _ = hash.Exception;
            rejected = true;
            return false;
        }
        if (boss == null || !boss.IsValid || boss.BaseId != 0x41C0)
        {
            return false;
        }

        using (memory.TemporaryCacheState(false))
        {
            if (!validated)
            {
                IntPtr image = memory.Process.MainModule.BaseAddress;
                // Operand-derived masks preserve the target virtual-call/store
                // and embedded creation-data copy. Read8/32 operands must still
                // resolve virtual slot 8, D0/120 fields and 10-byte embedding.
                validated = Anchor(image, 0x38B8F2, "48 8B 01 FF 50 ?? 0F B7 8B ?? ?? ?? ?? 48 89 84 CB ?? ?? ?? ?? 66 FF 83 ?? ?? ?? ??") &&
                    Anchor(image, 0x390B86, "48 8B D9 48 89 01 48 8D 41 ?? B9 ?? ?? ?? ?? 48 8D 80 ?? ?? ?? ?? 0F 10 02 48 8D 92 ?? ?? ?? ?? 0F 11 40 ??") &&
                    memory.Read<byte>(image + 0x38B8F2 + 5) == 8 && memory.Read<int>(image + 0x38B8F2 + 9) == 0xD0 &&
                    memory.Read<int>(image + 0x38B8F2 + 17) == 0x120 && memory.Read<int>(image + 0x38B8F2 + 24) == 0xD0 &&
                    memory.Read<byte>(image + 0x390B86 + 9) == 0x10 && memory.Read<int>(image + 0x390B86 + 11) == 3 &&
                    memory.Read<int>(image + 0x390B86 + 18) == 0x80;
                if (!validated)
                {
                    rejected = true;
                    return false;
                }
            }
            var result = new List<InterphosDivide.Marker>();
            var slots = boss.VfxContainer.Vfx;
            // Creation shifts the oldest of slots 10..12. Identity stays ulong:
            // non-entity GameObjectIds must never be truncated into a party ID.
            for (int slot = 10; slot <= 12 && slot < slots.Length; slot++)
            {
                var vfx = slots[slot];
                if (vfx == null || !vfx.IsValid || vfx.Id != 521)
                {
                    continue;
                }

                IntPtr ptr = vfx.Pointer;
                ushort count = memory.Read<ushort>(ptr + 0xE0);
                if (count > 16)
                {
                    return false;
                }
                // Constructor copies creation data at+10: D0/120 becomeE0/130.
                for (int i = 0; i < count; i++)
                {
                    result.Add(new(ptr.ToInt64(), memory.Read<ulong>(ptr + 0x130 + i * 8)));
                }

                if (memory.Read<ushort>(ptr + 0xE0) != count || !vfx.IsValid || vfx.Id != 521)
                {
                    return false;
                }
            }
            markers = result.ToArray();
            return true;
        }
    }

    internal void Reset()
    {
        memory = null;
        hash = null;
        pid = 0;
        rejected = validated = false;
    }
}
