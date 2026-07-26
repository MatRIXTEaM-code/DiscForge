// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Iso;
using DiscForge.Core.Raw;

namespace DiscForge.Core.PlayStation;

/// <summary>
/// Build a raw Mode 2/2352 bin + cue from a folder — the psxbuild job. A
/// PlayStation (or any CD-XA) data disc stores its ISO 9660 filesystem in
/// Mode 2 Form 1 sectors: 2048 user bytes wrapped in sync, header, an 8-byte
/// subheader, EDC and ECC. This lays down that wrapper around a cooked ISO built
/// by <see cref="IsoBuilder"/>, so a tree of files becomes a bootable-shaped raw
/// image that emulators and burners accept, and that DiscForge itself can browse
/// straight back with RawTrackReader.
///
/// It builds the data track only — a single Mode 2/2352 track from LBA 0, which
/// is the shape of essentially every game disc's filesystem. Audio tracks and
/// the license/boot specifics of a runnable disc are out of scope; this makes a
/// faithful *data* image, not a signed one.
/// </summary>
public static class PsxImageBuilder
{
    public const int RawSectorSize = 2352;
    private const int UserSize = 2048;

    /// <summary>Wrap a cooked 2048-per-sector ISO into a raw Mode 2/2352 image.
    /// Each ISO sector becomes one Form 1 sector at the matching LBA.</summary>
    public static byte[] FromIso(ReadOnlySpan<byte> iso2048)
    {
        if (iso2048.Length % UserSize != 0)
            throw new ArgumentException(
                $"ISO is {iso2048.Length:N0} bytes, not a whole number of {UserSize}-byte sectors.",
                nameof(iso2048));

        int sectors = iso2048.Length / UserSize;
        var bin = new byte[(long)sectors * RawSectorSize <= int.MaxValue
            ? sectors * RawSectorSize
            : throw new NotSupportedException("Image exceeds the in-memory build limit.")];

        var sec = new byte[RawSectorSize];
        for (int k = 0; k < sectors; k++)
        {
            CookForm1(sec, iso2048.Slice(k * UserSize, UserSize), k);
            Array.Copy(sec, 0, bin, k * RawSectorSize, RawSectorSize);
        }
        return bin;
    }

    /// <summary>The cue for a single Mode 2/2352 data track starting at LBA 0.</summary>
    public static string CueFor(string binFileName) =>
        $"FILE \"{binFileName}\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n";

    /// <summary>
    /// Build a raw Mode 2/2352 bin + cue for the files under
    /// <paramref name="folder"/>, written to <paramref name="outBin"/> and
    /// <paramref name="outCue"/>. Returns the number of sectors written.
    /// </summary>
    public static int BuildFromFolder(string folder, string volumeId, string outBin, string outCue)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");

        var nodes = ReadFolder(folder);
        if (nodes.Count == 0)
            throw new InvalidDataException("The folder is empty — nothing to build.");

        var built = IsoBuilder.BuildTree(volumeId, nodes);
        var bin = FromIso(built.Image);

        File.WriteAllBytes(outBin, bin);
        File.WriteAllText(outCue, CueFor(Path.GetFileName(outBin)));
        return bin.Length / RawSectorSize;
    }

    private static List<IsoBuilder.Node> ReadFolder(string dir)
    {
        var nodes = new List<IsoBuilder.Node>();
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(p => p, StringComparer.Ordinal))
            nodes.Add(IsoBuilder.Node.Dir(Path.GetFileName(sub), ReadFolder(sub)));
        foreach (var file in Directory.GetFiles(dir).OrderBy(p => p, StringComparer.Ordinal))
            nodes.Add(IsoBuilder.Node.FromPath(file));
        return nodes;
    }

    // ---- sector cooking ----------------------------------------------------

    /// <summary>Cook one Mode 2 Form 1 sector: sync, header, subheader, the 2048
    /// user bytes, then EDC + ECC.</summary>
    internal static void CookForm1(Span<byte> sector, ReadOnlySpan<byte> user2048, long lba)
    {
        sector.Clear();

        // Sync: 00 FF×10 00.
        sector[0] = 0x00;
        for (int i = 1; i <= 10; i++) sector[i] = 0xFF;
        sector[11] = 0x00;

        // Header: absolute M:S:F (LBA + 2s lead-in) in BCD, then mode 2.
        long abs = lba + 150;
        int f = (int)(abs % 75);
        long s2 = abs / 75;
        int s = (int)(s2 % 60);
        int m = (int)(s2 / 60);
        sector[12] = Bcd(m);
        sector[13] = Bcd(s);
        sector[14] = Bcd(f);
        sector[15] = 0x02;

        // Subheader (8 bytes, written twice): file 0, channel 0, submode 0x08
        // (data), coding 0. XA repeats the 4-byte subheader for resilience.
        Span<byte> sub = stackalloc byte[4] { 0x00, 0x00, 0x08, 0x00 };
        sub.CopyTo(sector.Slice(16, 4));
        sub.CopyTo(sector.Slice(20, 4));

        user2048.CopyTo(sector.Slice(24, UserSize));

        EdcEcc.FillMode2Form1(sector);
    }

    private static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));
}
