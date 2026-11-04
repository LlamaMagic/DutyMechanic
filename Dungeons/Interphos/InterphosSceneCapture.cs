using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>Developer-only, read-only snapshots of the observed Aethertithe scene group.</summary>
/// <remarks>
/// This records raw evidence, never a direction or movement request. The reviewed
/// Global image and layout anchor are hash-pinned; other clients fail closed. The
/// indexed scene group and its timeline children are revalidated on each sample.
/// </remarks>
internal sealed class InterphosSceneCapture
{
    private const string ReviewedImage = "46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A";
    // Ghidra-decoded anchor is unique in the reviewed .text section; its RIP-relative
    // load resolves the layout-world singleton. Displacements are intentionally masked.
    private const int AnchorRva = 0x7555B0;
    private const int LayoutWorldRva = 0x2AB8B60;
    // Captured placed-effect ID, not a network packet index or a movement selector.
    private const uint SceneId = 0x9DFE5B;
    private static readonly byte[] AnchorFixed = Convert.FromHexString("488B15488B424885C0740FB6D1488B88E9");
    private static readonly int[] AnchorOffsets = [0, 1, 2, 7, 8, 9, 11, 12, 13, 14, 16, 17, 18, 19, 20, 21, 26];
    private Task<string> hash;
    private GreyMagic.ExternalProcessMemory memory;
    private int pid;
    private bool rejected;
    private double nextSample;
    private string previous;

    internal void Pulse(double now)
    {
        if (rejected || now < nextSample)
        {
            return;
        }

        nextSample = now + 0.2;
        if (hash == null)
        {
            memory = Core.Memory;
            pid = memory.Process.Id;
            string path = memory.Process.MainModule.FileName;
            hash = Task.Run(() => { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); });
            _ = hash.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return;
        }
        if (!hash.IsCompleted)
        {
            return;
        }

        if (!hash.IsCompletedSuccessfully || hash.Result != ReviewedImage || !ReferenceEquals(memory, Core.Memory) || memory.Process.Id != pid)
        {
            _ = hash.Exception;
            rejected = true;
            Logger.Information("[InterphosScene] GAP unsupported image or changed attachment");
            return;
        }
        using (memory.AcquireFrame())
        using (memory.TemporaryCacheState(false))
        {
            var image = memory.Process.MainModule.BaseAddress;
            byte[] anchor = memory.ReadBytes(image + AnchorRva, 31);
            for (int i = 0; i < AnchorOffsets.Length; i++)
            {
                if (anchor[AnchorOffsets[i]] != AnchorFixed[i])
                {
                    throw new InvalidOperationException("layout anchor changed");
                }
            }

            if (AnchorRva + 7 + BitConverter.ToInt32(anchor, 3) != LayoutWorldRva)
            {
                throw new InvalidOperationException("layout anchor target changed");
            }

            IntPtr world = memory.Read<IntPtr>(image + LayoutWorldRva);
            if (world == IntPtr.Zero)
            {
                return;
            }

            IntPtr layout = memory.Read<IntPtr>(world + 0x20);
            if (layout == IntPtr.Zero || memory.Read<int>(layout + 0x18) != 7 || memory.Read<uint>(layout + 0x20) != 1202)
            {
                return;
            }

            IntPtr types = memory.Read<IntPtr>(layout + 0x268);
            IntPtr typeNode = Find(types, 6, false);
            if (typeNode == IntPtr.Zero)
            {
                return;
            }

            IntPtr groups = memory.Read<IntPtr>(typeNode + 0x28);
            if (groups == IntPtr.Zero)
            {
                return;
            }

            IntPtr groupNode = Find(memory.Read<IntPtr>(groups), (ulong)SceneId << 32, true);
            if (groupNode == IntPtr.Zero)
            {
                return;
            }

            IntPtr group = memory.Read<IntPtr>(groupNode + 0x28);
            if (group == IntPtr.Zero || memory.Read<byte>(group + 0x19) != 6 || memory.Read<uint>(group + 0x1C) != SceneId || memory.Read<uint>(group + 0x20) != 0)
            {
                throw new InvalidOperationException("scene group identity changed");
            }

            IntPtr begin = memory.Read<IntPtr>(group + 0xD8), end = memory.Read<IntPtr>(group + 0xE0);
            long length = end.ToInt64() - begin.ToInt64();
            if (length < 0 || length > 256 || length % 8 != 0 || (length != 0 && begin == IntPtr.Zero))
            {
                throw new InvalidOperationException("invalid timeline vector");
            }

            var parts = new List<string> {
                "group=" + Convert.ToHexString(memory.ReadBytes(group + 0x120, 0x60))
            };
            for (int i = 0; i < length / 8; i++)
            {
                IntPtr child = memory.Read<IntPtr>(begin + i * 8);
                if (child == IntPtr.Zero)
                {
                    parts.Add("child=null");
                    continue;
                }
                IntPtr scheduler = memory.Read<IntPtr>(child + 0x30);
                parts.Add("child=" + Convert.ToHexString(memory.ReadBytes(child + 0x18, 0x20)) +
                    ":scheduler=" + (scheduler == IntPtr.Zero ? "null" : Convert.ToHexString(memory.ReadBytes(scheduler, 0x40))));
            }
            string snapshot = string.Join(";", parts);
            if (snapshot == previous)
            {
                return;
            }

            previous = snapshot;
            Logger.Information(FormattableString.Invariant($"[InterphosScene] RAW time={now:F3} {snapshot}"));
        }
    }

    // Both indexes are MSVC ordered trees. A bounded search and nil checks keep
    // a loading or malformed tree from becoming an unbounded diagnostic loop.
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
        hash = null;
        memory = null;
        pid = 0;
        rejected = false;
        nextSample = 0;
        previous = null;
    }
}
