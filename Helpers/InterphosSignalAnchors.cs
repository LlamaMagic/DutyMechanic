using System;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;

namespace DutyMechanic.Helpers;

/// <summary>Finds the native instruction contracts used by the Interphos signal readers.</summary>
public static class InterphosSignalAnchors
{
    // These masks follow decoded whole instructions. Wildcards cover RIP and
    // branch displacements and field operands, which are checked separately.
    private const string Layout = "48 8B 15 ?? ?? ?? ?? 48 8B 42 ?? 48 85 C0 74 ?? 0F B6 D1 48 8B 88 ?? ?? ?? ?? E9 ?? ?? ?? ??";
    private const string Store = "48 8B 01 FF 50 ?? 0F B7 8B ?? ?? ?? ?? 48 89 84 CB ?? ?? ?? ?? 66 FF 83 ?? ?? ?? ??";
    private const string CopyAnchor = "48 8B D9 48 89 01 48 8D 41 ?? B9 ?? ?? ?? ?? 48 8D 80 ?? ?? ?? ?? 0F 10 02 48 8D 92 ?? ?? ?? ?? 0F 11 40 ??";

    private static readonly int[] LayoutPattern = Parse(Layout);
    private static readonly int[] StorePattern = Parse(Store);
    private static readonly int[] CopyPattern = Parse(CopyAnchor);

    /// <summary>Holds unique code locations for scene layout and Divide target validation. Zero means unavailable.</summary>
    /// <param name="LayoutRva">The layout reader instruction RVA.</param>
    /// <param name="WorldRva">The singleton RVA resolved by its RIP-relative load.</param>
    /// <param name="StoreRva">The Divide target store instruction RVA.</param>
    /// <param name="CopyRva">The Divide creation-data copy instruction RVA.</param>
    public readonly record struct Locations(int LayoutRva, int WorldRva, int StoreRva, int CopyRva);

    /// <summary>Scans the attached executable file off the bot thread and returns unique compatible anchors.</summary>
    /// <param name="path">Path of the executable associated with the current attachment.</param>
    /// <returns>Locations to revalidate against the loaded module before reading signals.</returns>
    public static Task<Locations> FindAsync(string path) => Task.Run(() =>
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        foreach (var section in pe.PEHeaders.SectionHeaders)
        {
            if (section.Name != ".text" || (section.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0)
                continue;
            var code = pe.GetSectionData(section.VirtualAddress).GetContent().AsSpan();
            int layout = FindUnique(code, Layout);
            int store = FindUnique(code, Store);
            int copy = FindUnique(code, CopyAnchor);
            bool divide = store >= 0 && copy >= 0 && MatchesDivide(code[store..], code[copy..]);
            int layoutRva = layout < 0 ? 0 : section.VirtualAddress + layout;
            int worldRva = layoutRva == 0 ? 0 : ResolveLayout(code.Slice(layout, Math.Min(31, code.Length - layout)), layoutRva);
            // The RIP target must land in readable data in this image.
            if (worldRva != 0 && !IsReadableData(pe.PEHeaders, worldRva)) worldRva = 0;
            return new Locations(worldRva == 0 ? 0 : layoutRva, worldRva,
                divide ? section.VirtualAddress + store : 0,
                divide ? section.VirtualAddress + copy : 0);
        }
        return default;
    });

    /// <summary>Verifies the loaded layout instructions and extracts the global pointer RVA.</summary>
    /// <param name="code">Loaded bytes beginning at the candidate instruction.</param>
    /// <param name="rva">The instruction's module-relative address.</param>
    /// <returns>The referenced singleton RVA, or zero if the layout contract changed.</returns>
    public static int ResolveLayout(ReadOnlySpan<byte> code, int rva)
    {
        if (!Matches(code, LayoutPattern) || code[10] != 0x20 ||
            BitConverter.ToInt32(code.Slice(22, 4)) != 0x88 || code[15] != 0x0F)
            return 0;
        long target = (long)rva + 7 + BitConverter.ToInt32(code.Slice(3, 4));
        return target is > 0 and <= int.MaxValue ? (int)target : 0;
    }

    /// <summary>Checks the loaded Divide store and copy instructions before reading target identities.</summary>
    /// <param name="store">Bytes at the virtual identity call and count-indexed store.</param>
    /// <param name="copy">Bytes at the constructor's embedded creation-data copy.</param>
    /// <returns>True when slot, fields, embedding and both copy strides still agree.</returns>
    public static bool MatchesDivide(ReadOnlySpan<byte> store, ReadOnlySpan<byte> copy)
        => Matches(store, StorePattern) && Matches(copy, CopyPattern) &&
            store[5] == 8 && BitConverter.ToInt32(store.Slice(9, 4)) == 0xD0 &&
            BitConverter.ToInt32(store.Slice(17, 4)) == 0x120 &&
            BitConverter.ToInt32(store.Slice(24, 4)) == 0xD0 &&
            copy[9] == 0x10 && BitConverter.ToInt32(copy.Slice(11, 4)) == 3 &&
            BitConverter.ToInt32(copy.Slice(18, 4)) == 0x80 &&
            BitConverter.ToInt32(copy.Slice(28, 4)) == 0x80 && copy[35] == 0x80;
    private static bool IsReadableData(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var flags = section.SectionCharacteristics;
            long offset = (long)rva - section.VirtualAddress;
            if ((flags & SectionCharacteristics.MemRead) != 0 &&
                (flags & SectionCharacteristics.MemExecute) == 0 &&
                offset >= 0 && offset <= section.VirtualSize - 8)
                return true;
        }
        return false;
    }
    private static int FindUnique(ReadOnlySpan<byte> code, params string[] signatures)
    {
        int match = -1;
        foreach (string signature in signatures)
        {
            var pattern = Parse(signature);
            int start = 0;
            while (start <= code.Length - pattern.Length)
            {
                int index = code[start..].IndexOf((byte)pattern[0]);
                if (index < 0) break;
                start += index;
                if (Matches(code[start..], pattern))
                {
                    if (match >= 0) return -1;
                    match = start;
                }
                start++;
            }
        }
        return match;
    }

    private static int[] Parse(string signature) => Array.ConvertAll(signature.Split(' '),
        token => token == "??" ? -1 : Convert.ToByte(token, 16));

    private static bool Matches(ReadOnlySpan<byte> code, int[] pattern)
    {
        if (code.Length < pattern.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i] >= 0 && pattern[i] != code[i]) return false;
        return true;
    }
}
