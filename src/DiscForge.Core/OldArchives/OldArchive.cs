// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Ace;

namespace DiscForge.Core.OldArchives;

/// <summary>A file or folder inside an old archive.</summary>
public sealed record OldEntry(
    int Index,
    string Name,
    long Size,
    long PackedSize,
    DateTime Modified,
    bool IsDirectory,
    bool IsEncrypted,
    string Method,
    string Comment = "",
    int VolumeCount = 1)
{
    /// <summary>Format-specific data the reader needs to decode the entry.</summary>
    internal object? Tag { get; init; }
}

/// <summary>What happened to one entry during a test or extraction.</summary>
public sealed record OldEntryResult(OldEntry Entry, bool Ok, string? OutputPath, string? Error);

public sealed record OldRunResult(IReadOnlyList<OldEntryResult> Entries)
{
    public int Ok => Entries.Count(e => e.Ok);
    public int Failed => Entries.Count(e => !e.Ok);
    public bool AllOk => Entries.All(e => e.Ok);
}

public class OldArchiveException(string message) : Exception(message);

/// <summary>Missing or wrong password.</summary>
public sealed class OldArchivePasswordException(string message) : OldArchiveException(message);

/// <summary>
/// A read-only archive in one of the DOS-era formats DiscForge can unpack: ACE, LHA/LZH (and LArc),
/// ARJ and ZOO. Open with <see cref="Open"/>; list <see cref="Entries"/>; test or extract. Every
/// extraction cleans stored names and refuses to write outside the chosen folder.
/// </summary>
public abstract class OldArchive : IDisposable
{
    protected readonly List<OldEntry> EntryList = new();
    protected readonly List<string> WarningList = new();
    protected readonly List<string> VolumeList = new();
    protected readonly List<Stream> OpenStreams = new();

    /// <summary>"ACE", "LHA", "ARJ" or "ZOO".</summary>
    public abstract string Format { get; }
    public IReadOnlyList<OldEntry> Entries => EntryList;
    public IReadOnlyList<string> Warnings => WarningList;
    /// <summary>The files the archive was read from (more than one for a multi-volume set).</summary>
    public IReadOnlyList<string> VolumePaths => VolumeList;
    public virtual string Comment => "";
    /// <summary>One line describing the archive (format, version, flags).</summary>
    public abstract string Description { get; }
    /// <summary>Byte offset of the archive inside the first file (non-zero for self-extractors).</summary>
    public long StartOffset { get; protected set; }
    public bool AnyEncrypted => EntryList.Any(e => e.IsEncrypted);

    /// <summary>Decode one entry to <paramref name="output"/>, checking its checksum (throw on mismatch).</summary>
    protected abstract void DecodeEntry(OldEntry entry, Stream output, string? password);

    // ------------------------------------------------------------------------------ opening

    /// <summary>Extensions DiscForge treats as old-archive files (for folder sweeps and disc images).</summary>
    public static bool HasArchiveExtension(string path)
    {
        string e = Path.GetExtension(path).ToLowerInvariant();
        return e is ".ace" or ".lzh" or ".lha" or ".lzs" or ".arj" or ".zoo" || IsNumberedVolume(path);
    }

    /// <summary>.c00–.c99 (ACE) and .a01–.a99 (ARJ) continuation volumes.</summary>
    public static bool IsNumberedVolume(string path)
    {
        string e = Path.GetExtension(path);
        return e.Length == 4 && (e[1] is 'c' or 'C' or 'a' or 'A') && char.IsAsciiDigit(e[2]) && char.IsAsciiDigit(e[3]);
    }

    /// <summary>Which format a file is, or null. Looks at the content, not the name (self-extracting
    /// .exe archives are found by searching the start of the file).</summary>
    public static string? Detect(string path)
    {
        try
        {
            if (AceArchive.IsAceFile(path)) return "ACE";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (ZooArchive.Probe(fs)) return "ZOO";
            if (ArjArchive.FindStart(fs) >= 0) return "ARJ";
            if (LhaArchive.FindStart(fs) >= 0) return "LHA";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Open any supported archive (or any volume of a multi-volume set).</summary>
    public static OldArchive Open(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // A continuation volume: open the set from its first volume.
        if (IsNumberedVolume(path))
        {
            if (ext[1] == 'c') return new AceOldArchive(AceArchive.Open(path));
            string first = ArjArchive.FirstVolumeFor(path);
            return ArjArchive.OpenFile(first);
        }
        switch (Detect(path))
        {
            case "ACE": return new AceOldArchive(AceArchive.Open(path));
            case "ZOO": return ZooArchive.OpenFile(path);
            case "ARJ": return ArjArchive.OpenFile(path);
            case "LHA": return LhaArchive.OpenFile(path);
        }
        throw new OldArchiveException("Not an ACE, LHA/LZH, ARJ or ZOO archive DiscForge can read.");
    }

    // ------------------------------------------------------------------------------ reading

    public virtual OldRunResult TestAll(string? password = null, IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
        => Run(null, password, false, progress, ct);

    public virtual OldRunResult ExtractAll(string destination, string? password = null, bool overwrite = false,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        return Run(destination, password, overwrite, progress, ct);
    }

    /// <summary>Decode one entry to a stream.</summary>
    public virtual void Extract(OldEntry entry, Stream output, string? password = null)
    {
        if (entry.IsDirectory) return;
        DecodeEntry(entry, output, password);
    }

    protected OldRunResult Run(string? destination, string? password, bool overwrite,
        IProgress<(int Done, int Total, string Name)>? progress, CancellationToken ct)
    {
        var results = new List<OldEntryResult>();
        var dirTimes = new List<(string, DateTime)>();
        for (int k = 0; k < EntryList.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var e = EntryList[k];
            progress?.Report((k, EntryList.Count, e.Name));
            string? outPath = null;
            try
            {
                if (destination is null)
                {
                    if (!e.IsDirectory) DecodeEntry(e, Stream.Null, password);
                    results.Add(new OldEntryResult(e, true, null, null));
                    continue;
                }
                outPath = SafeExtract.ContainedPath(destination, e.Name);
                if (e.IsDirectory)
                {
                    Directory.CreateDirectory(outPath);
                    dirTimes.Add((outPath, e.Modified));
                    results.Add(new OldEntryResult(e, true, outPath, null));
                    continue;
                }
                if (!overwrite && File.Exists(outPath))
                {
                    results.Add(new OldEntryResult(e, false, outPath, "already exists (not overwritten)"));
                    continue;
                }
                SafeExtract.WriteFile(outPath, e.Modified, s => DecodeEntry(e, s, password));
                results.Add(new OldEntryResult(e, true, outPath, null));
            }
            catch (Exception ex) when (ex is OldArchiveException or AceFormatException or IOException or UnauthorizedAccessException
                                       or EndOfStreamException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                string msg = ex is IndexOutOfRangeException or ArgumentOutOfRangeException ? "the compressed data is damaged" : ex.Message;
                results.Add(new OldEntryResult(e, false, outPath, msg));
            }
        }
        foreach (var (p, t) in dirTimes)
            try { Directory.SetLastWriteTime(p, t); } catch (IOException) { } catch (ArgumentException) { }
        progress?.Report((EntryList.Count, EntryList.Count, ""));
        return new OldRunResult(results);
    }

    /// <summary>Check a decoded entry's size and checksum.</summary>
    private protected static void Verify(OldEntry e, ChecksumStream cs, uint expected, string crcName)
    {
        if (cs.Count != e.Size)
            throw new OldArchiveException($"Decompressed {cs.Count:N0} of {e.Size:N0} bytes — the file is damaged.");
        if (cs.Crc != expected)
        {
            if (e.IsEncrypted) throw new OldArchivePasswordException("Wrong password (or the file is damaged).");
            throw new OldArchiveException($"{crcName} doesn't match (expected {expected:X}, got {cs.Crc:X}) — the file is damaged.");
        }
    }

    protected FileStream OpenVolume(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        OpenStreams.Add(fs);
        VolumeList.Add(path);
        return fs;
    }

    public virtual void Dispose()
    {
        foreach (var s in OpenStreams) s.Dispose();
        OpenStreams.Clear();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>Stored names: UTF-8 when valid, else Shift-JIS when valid (Japanese LHA archives),
    /// else DOS code page 437.</summary>
    internal static string DecodeName(ReadOnlySpan<byte> raw, bool tryShiftJis)
    {
        int nul = raw.IndexOf((byte)0);
        if (nul >= 0) raw = raw[..nul];
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { }
        if (tryShiftJis)
        {
            try
            {
                var sjis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                _ = AceArchive.Cp437;   // registers the code-page provider
                return sjis.GetString(raw);
            }
            catch (DecoderFallbackException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return AceArchive.Cp437.GetString(raw);
    }

    internal static DateTime FromDos(uint dos) => AceArchive.FromDos(dos);

    internal static DateTime FromUnix(uint t)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime; }
        catch (ArgumentOutOfRangeException) { return new DateTime(1980, 1, 1); }
    }

    internal static int ReadFully(Stream s, byte[] buf, int off, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, off + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>Shared safe-extraction rules for every old-archive format.</summary>
public static class SafeExtract
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Turn a stored name into a safe relative path with '/' separators: both slash kinds split folders;
    /// drive letters, colons, control and wildcard characters, "." and ".." parts and leading separators
    /// are removed ("a/../b" resolves to "b" but never climbs above the top); Windows device names get a
    /// leading underscore; trailing dots and spaces are trimmed. May return "".
    /// </summary>
    public static string SanitizeRelativePath(string name)
    {
        int nul = name.IndexOf('\0');
        if (nul >= 0) name = name[..nul];
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (ch < 32 || ch == 127 || ":<>\"?*|".IndexOf(ch) >= 0) continue;
            sb.Append(ch == '\\' ? '/' : ch);
        }
        var parts = new List<string>();
        foreach (var rawPart in sb.ToString().Split('/'))
        {
            string trimmed = rawPart.Trim(' ');
            if (trimmed == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (trimmed.Trim('.').Trim(' ').Length == 0) continue;
            string part = rawPart.TrimEnd('.', ' ').TrimStart(' ');
            if (part.Length == 0) continue;
            string stem = part.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem)) part = "_" + part;
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    /// <summary>Full output path for <paramref name="relative"/> under <paramref name="root"/>; throws
    /// if it would land anywhere else or pass through an existing link or junction.</summary>
    public static string ContainedPath(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root);
        string rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative)) throw new OldArchiveException($"Refusing absolute path \"{relative}\".");
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSep, cmp)) throw new OldArchiveException($"Refusing to write outside the output folder: \"{relative}\".");
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > fullRoot.Length; dir = Path.GetDirectoryName(dir))
        {
            var di = new DirectoryInfo(dir);
            if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new OldArchiveException($"Refusing to write through the link \"{dir}\".");
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new OldArchiveException($"Refusing to overwrite the link \"{full}\".");
        return full;
    }

    /// <summary>Write via a .dfpart temporary file, renamed into place only if <paramref name="fill"/> succeeds.</summary>
    public static void WriteFile(string outPath, DateTime modified, Action<Stream> fill)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        string tmp = outPath + ".dfpart";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                fill(fs);
            File.Move(tmp, outPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        try { File.SetLastWriteTime(outPath, modified); } catch (IOException) { } catch (ArgumentException) { }
    }
}

/// <summary>ACE through the common interface (the ACE reader keeps its own solid-aware logic).</summary>
public sealed class AceOldArchive : OldArchive
{
    private readonly AceArchive _ace;

    public AceOldArchive(AceArchive ace)
    {
        _ace = ace;
        VolumeList.AddRange(ace.VolumePaths);
        WarningList.AddRange(ace.Warnings);
        StartOffset = ace.StartOffset;
        foreach (var m in ace.Members)
            EntryList.Add(new OldEntry(m.Index, m.Name, m.Size, m.PackedSize, m.Modified, m.IsDirectory, m.IsEncrypted,
                m.IsDirectory ? "" : m.MethodText, m.Comment, m.VolumeCount) { Tag = m });
    }

    public AceArchive Ace => _ace;
    public override string Format => "ACE";
    public override string Comment => _ace.Comment;

    public override string Description
    {
        get
        {
            var bits = new List<string> { $"ACE {AceArchive.VersionText(_ace.VersionNeeded)} archive" };
            if (_ace.IsSolid) bits.Add("solid");
            if (_ace.IsMultiVolume) bits.Add($"{_ace.VolumePaths.Count} volume(s)");
            if (_ace.StartOffset > 0) bits.Add("self-extracting");
            if (_ace.AnyEncrypted) bits.Add("password-protected");
            return string.Join(", ", bits);
        }
    }

    protected override void DecodeEntry(OldEntry entry, Stream output, string? password)
        => Wrap(() => _ace.Extract((AceMember)entry.Tag!, output, password));

    public override void Extract(OldEntry entry, Stream output, string? password = null)
    {
        if (!entry.IsDirectory) DecodeEntry(entry, output, password);
    }

    public override OldRunResult TestAll(string? password = null, IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
        => Map(_ace.TestAll(password, progress, ct));

    public override OldRunResult ExtractAll(string destination, string? password = null, bool overwrite = false,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
        => Map(_ace.ExtractAll(destination, password, overwrite, progress, ct));

    private OldRunResult Map(AceRunResult r)
    {
        var list = new List<OldEntryResult>();
        foreach (var m in r.Members)
        {
            var e = m.Member.Index < EntryList.Count && ReferenceEquals(EntryList[m.Member.Index].Tag, m.Member)
                ? EntryList[m.Member.Index]
                : new OldEntry(m.Member.Index, m.Member.Name, m.Member.Size, m.Member.PackedSize, m.Member.Modified,
                    m.Member.IsDirectory, m.Member.IsEncrypted, m.Member.MethodText) { Tag = m.Member };
            list.Add(new OldEntryResult(e, m.Ok, m.OutputPath, m.Error));
        }
        return new OldRunResult(list);
    }

    private static void Wrap(Action a)
    {
        try { a(); }
        catch (AcePasswordException ex) { throw new OldArchivePasswordException(ex.Message); }
        catch (AceFormatException ex) { throw new OldArchiveException(ex.Message); }
    }

    public override void Dispose()
    {
        _ace.Dispose();
        base.Dispose();
    }
}
