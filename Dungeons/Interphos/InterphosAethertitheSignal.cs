using ff14bot;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>Reads the reviewed scene's currently instantiated cone timeline.</summary>
/// <remarks>
/// Two captured pulls support the center and east mappings; the west mapping is
/// reference-derived and still requires a live test. This reader uses only the
/// reviewed Global image, validates each lookup and returns no sector on ambiguity.
/// A timeline is a candidate pre-impact signal, not an effect receipt.
/// </remarks>
internal sealed class InterphosAethertitheSignal
{
    private const string Image = "46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A";
    // The decoded anchor is unique in the reviewed executable and resolves this
    // singleton through RIP-relative addressing; all operand bytes were masked.
    private const int AnchorRva = 0x7555B0, WorldRva = 0x2AB8B60;
    private const uint GroupId = 0x9DFE5B;
    private static readonly byte[] AnchorFixed = Convert.FromHexString("488B15488B424885C0740FB6D1488B88E9");
    private static readonly int[] AnchorOffsets = [0, 1, 2, 7, 8, 9, 11, 12, 13, 14, 16, 17, 18, 19, 20, 21, 26];
    private GreyMagic.ExternalProcessMemory memory;
    private Task<string> hash;
    private int pid;
    private bool rejected;

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

    internal bool TryRead(out int index)
    {
        index = 0;
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
        using (memory.AcquireFrame())
        using (memory.TemporaryCacheState(false))
        {
            IntPtr image = memory.Process.MainModule.BaseAddress;
            byte[] anchor = memory.ReadBytes(image + AnchorRva, 31);
            for (int i = 0; i < AnchorOffsets.Length; i++)
            {
                if (anchor[AnchorOffsets[i]] != AnchorFixed[i])
                {
                    rejected = true;
                    return false;
                }
            }

            if (AnchorRva + 7 + BitConverter.ToInt32(anchor, 3) != WorldRva)
            {
                rejected = true;
                return false;
            }
            IntPtr world = memory.Read<IntPtr>(image + WorldRva);
            if (world == IntPtr.Zero)
            {
                return false;
            }

            IntPtr layout = memory.Read<IntPtr>(world + 0x20);
            if (layout == IntPtr.Zero || memory.Read<int>(layout + 0x18) != 7 || memory.Read<uint>(layout + 0x20) != 1202)
            {
                return false;
            }

            IntPtr type = Find(memory.Read<IntPtr>(layout + 0x268), 6, false);
            if (type == IntPtr.Zero)
            {
                return false;
            }

            IntPtr groups = memory.Read<IntPtr>(type + 0x28);
            if (groups == IntPtr.Zero)
            {
                return false;
            }

            IntPtr node = Find(memory.Read<IntPtr>(groups), (ulong)GroupId << 32, true);
            if (node == IntPtr.Zero)
            {
                return false;
            }

            IntPtr group = memory.Read<IntPtr>(node + 0x28);
            if (group == IntPtr.Zero || memory.Read<byte>(group + 0x19) != 6 ||
                memory.Read<uint>(group + 0x1C) != GroupId || memory.Read<uint>(group + 0x20) != 0)
            {
                return false;
            }

            int last = (int)(memory.Read<uint>(group + 0x138) & 15);
            if (last is < 10 or > 12)
            {
                return false;
            }

            byte[] mapping = memory.ReadBytes(group + 0x154, 16);
            if (mapping[10] == 0 || mapping[11] == 0 || mapping[12] == 0 ||
                mapping[10] == mapping[11] || mapping[10] == mapping[12] || mapping[11] == mapping[12])
            {
                return false;
            }

            IntPtr begin = memory.Read<IntPtr>(group + 0xD8), end = memory.Read<IntPtr>(group + 0xE0);
            long bytes = end.ToInt64() - begin.ToInt64();
            if (bytes <= 0 || bytes > 256 || bytes % 8 != 0 || begin == IntPtr.Zero)
            {
                return false;
            }

            int active = 0, seen = 0;
            for (int i = 0; i < bytes / 8; i++)
            {
                IntPtr child = memory.Read<IntPtr>(begin + i * 8);
                if (child == IntPtr.Zero || memory.Read<IntPtr>(child + 0x38) != group)
                {
                    return false;
                }

                uint subId = memory.Read<uint>(child + 0x20);
                for (int sector = 10; sector <= 12; sector++)
                {
                    // This top-level group's children place their timeline byte
                    // in the high sub-ID byte; live inspection confirmed parent
                    // and sub-ID identities for all 13 observed children.
                    if (subId != (uint)mapping[sector] << 24)
                    {
                        continue;
                    }

                    int bit = 1 << (sector - 10);
                    if ((seen & bit) != 0)
                    {
                        return false;
                    }

                    seen |= bit;
                    if (memory.Read<IntPtr>(child + 0x30) != IntPtr.Zero)
                    {
                        active |= bit;
                    }
                }
            }
            if (seen != 7 || active != 1 << (last - 10))
            {
                return false;
            }

            index = last;
            return true;
        }
    }

    private IntPtr Find(IntPtr head, ulong key, bool wide)
    {
        if (head == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr node = memory.Read<IntPtr>(head + 8);
        for (int depth = 0; depth < 64 && node != IntPtr.Zero && node != head; depth++)
        {
            if (memory.Read<byte>(node + 0x19) != 0)
            {
                return IntPtr.Zero;
            }

            ulong current = wide ? memory.Read<ulong>(node + 0x20) : memory.Read<byte>(node + 0x20);
            if (current == key)
            {
                return node;
            }

            node = memory.Read<IntPtr>(node + (current < key ? 16 : 0));
        }
        return IntPtr.Zero;
    }

    internal void Reset()
    {
        memory = null;
        hash = null;
        rejected = false;
        pid = 0;
    }
}
