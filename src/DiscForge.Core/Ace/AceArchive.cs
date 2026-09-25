// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// ACE archive reading (list, test, extract; never create). Header layout from Marcel Lemke's public
// ACE technical note; behaviour details (the advert byte, volume naming, member continuation, comment
// coding, filename clean-up) cross-checked against acefile by Daniel Roethlisberger, BSD 2-clause —
// see AceCodec.cs and NOTICE.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Ace;

/// <summary>One file or folder stored in an ACE archive (possibly split over several volumes).</summary>
public sealed record AceMember(
    int Index,
    string Name,
    byte[] RawName,
    long Size,
    long PackedSize,
    DateTime Modified,
    uint Attributes,
    uint Crc32,
    int CompressionType,
    int CompressionQuality,
    int DictionaryBits,
    bool IsEncrypted,
    string Comment,
    int FirstVolume,
    int VolumeCount)
{
    public bool IsDirectory => (Attributes & 0x10) != 0;

    public string MethodText => CompressionType switch
    {
        AceDecompressor.CompStored => "stored",
        AceDecompressor.CompLz77 => "LZ77 (ACE 1.0)",
        AceDecompressor.CompBlocked => "blocked (ACE 2.0)",
        _ => $"unknown ({CompressionType})",
    };

    internal List<(int Volume, long Offset, long Length)> Segments { get; init; } = new();
}

/// <summary>What happened to one member during test or extraction.</summary>
public sealed record AceMemberResult(AceMember Member, bool Ok, string? OutputPath, string? Error);

/// <summary>Result of testing or extracting a whole archive.</summary>
public sealed record AceRunResult(IReadOnlyList<AceMemberResult> Members)
{
    public int Ok => Members.Count(m => m.Ok);
    public int Failed => Members.Count(m => !m.Ok);
    public bool AllOk => Members.All(m => m.Ok);
}

/// <summary>Wrong or missing password for an encrypted member.</summary>
public sealed class AcePasswordException(string message) : Exception(message);

/// <summary>
/// A read-only ACE 1.0/2.0 archive: single file, self-extracting (.exe with the archive inside), or a
/// multi-volume set (name.ace, name.c00, name.c01 …). Solid archives are decoded in order. Encrypted
/// members need the password. Extraction cleans every stored name and refuses to write anywhere outside
/// the chosen folder.
/// </summary>
public sealed class AceArchive : IDisposable
{
    private const int TypeMain = 0, TypeFile32 = 1, TypeRecovery32 = 2, TypeFile64 = 3, TypeRecovery64A = 4, TypeRecovery64B = 5;
    private const int FlagAddSize = 1, FlagComment = 2, Flag64Bit = 4;
    private const int FlagNtSecurity = 1 << 10, FlagMultiVolume = 1 << 11, FlagAdvert = 1 << 12, FlagContPrev = 1 << 12;
    private const int FlagRecovery = 1 << 13, FlagContNext = 1 << 13, FlagLocked = 1 << 14, FlagPassword = 1 << 14, FlagSolid = 1 << 15;
    private const int FlagSfx = 1 << 9;
    private static readonly byte[] Magic = "**ACE**"u8.ToArray();
    public const int DefaultSearch = 524288;

    private readonly List<Volume> _volumes = new();
    private readonly List<AceMember> _members = new();
    private readonly List<string> _warnings = new();
    private readonly Encoding? _forcedEncoding;

    public IReadOnlyList<AceMember> Members => _members;
    public IReadOnlyList<string> VolumePaths => _volumes.Select(v => v.Path).ToList();
    /// <summary>Things that didn't stop the archive opening but are worth telling the user.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public bool IsSolid { get; private set; }
    public bool IsMultiVolume { get; private set; }
    public bool IsLocked { get; private set; }
    public bool IsSelfExtracting { get; private set; }
    public bool HasRecoveryRecord { get; private set; }
    /// <summary>ACE version needed to extract (10 = 1.0, 20 = 2.0).</summary>
    public int VersionNeeded { get; private set; }
    /// <summary>ACE version that made the archive.</summary>
    public int VersionMadeBy { get; private set; }
    public string HostOs { get; private set; } = "";
    public DateTime Created { get; private set; }
    public string Comment { get; private set; } = "";
    /// <summary>The "unregistered version" advert text shareware WinAce put in archives, if any.</summary>
    public string Advert { get; private set; } = "";
    /// <summary>Offset of the archive inside the first file (non-zero for self-extractors).</summary>
    public long StartOffset { get; private set; }
    public bool AnyEncrypted => _members.Any(m => m.IsEncrypted);

    private sealed class Volume
    {
        public required string Path;
        public required FileStream Stream;
        public int Number;
        public bool MultiVolume;
        public readonly List<(int Flags, byte[] RawName, AceMember Draft)> Files = new();
    }

    private AceArchive(Encoding? encoding) { _forcedEncoding = encoding; }

    // ------------------------------------------------------------------------------------ detection

    /// <summary>True if the file looks like an ACE archive: "**ACE**" at offset 7 with a valid main
    /// header, or (for self-extractors) such a header within the first 512 KiB.</summary>
    public static bool IsAceFile(string path, int search = DefaultSearch)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return FindMainHeader(fs, search) >= 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Quick check on the first bytes of a file (no SFX search).</summary>
    public static bool HasAceSignature(ReadOnlySpan<byte> head) => head.Length >= 14 && head.Slice(7, 7).SequenceEqual(Magic);

    private static long FindMainHeader(Stream fs, int search)
    {
        if (ValidMainHeaderAt(fs, 0)) return 0;
        if (search <= 0) return -1;
        var buf = new byte[(int)Math.Min(search, fs.Length)];
        fs.Position = 0;
        int n = ReadFully(fs, buf, 0, buf.Length);
        int pos = 8;
        while (pos < n)
        {
            int k = buf.AsSpan(pos, n - pos).IndexOf(Magic);
            if (k < 0) return -1;
            int at = pos + k;
            if (ValidMainHeaderAt(fs, at - 7)) return at - 7;
            pos = at + 1;
        }
        return -1;
    }

    private static bool ValidMainHeaderAt(Stream fs, long offset)
    {
        if (offset < 0 || offset + 4 > fs.Length) return false;
        fs.Position = offset;
        var head = new byte[4];
        if (ReadFully(fs, head, 0, 4) < 4) return false;
        int size = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        if (size < 26 || offset + 4 + size > fs.Length) return false;
        var body = new byte[size];
        if (ReadFully(fs, body, 0, size) < size) return false;
        return (AceCrc32.Compute(body) & 0xFFFF) == BinaryPrimitives.ReadUInt16LittleEndian(head) && body[0] == TypeMain;
    }

    // ------------------------------------------------------------------------------------ opening

    /// <summary>Open an ACE archive. For a multi-volume set, pass any volume: DiscForge starts from
    /// the .ace file if it is there and picks up .c00, .c01 … automatically.</summary>
    /// <param name="encoding">Filename encoding; null = UTF-8 if the name is valid UTF-8, else code page 437 (DOS).</param>
    public static AceArchive Open(string path, Encoding? encoding = null)
    {
        var archive = new AceArchive(encoding);
        try
        {
            string first = FirstVolumeFor(path);
            archive.LoadVolume(first, DefaultSearch);
            if (archive._volumes[0].MultiVolume)
            {
                string? next = first;
                while ((next = NextVolumeName(next!)) is not null && File.Exists(next))
                {
                    try { archive.LoadVolume(next, 0); }
                    catch (AceFormatException ex) { archive._warnings.Add($"{System.IO.Path.GetFileName(next)}: {ex.Message}"); break; }
                }
            }
            archive.BuildMembers();
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>name.c03 → name.ace (when it exists, whatever its case); otherwise the path itself.</summary>
    private static string FirstVolumeFor(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        if (ext.Length == 4 && (ext[1] == 'c' || ext[1] == 'C') && char.IsAsciiDigit(ext[2]) && char.IsAsciiDigit(ext[3]))
        {
            string stem = path[..^4];
            foreach (var cand in new[] { stem + ".ace", stem + ".ACE", stem + ".Ace" })
                if (File.Exists(cand)) return cand;
        }
        return path;
    }

    /// <summary>ACE's volume naming: name.ace → name.c00 → name.c01 … (case follows the existing file).</summary>
    internal static string? NextVolumeName(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        string stem = path[..^ext.Length];
        int n = 0;
        if (ext.Length == 4 && (ext[1] == 'c' || ext[1] == 'C') && int.TryParse(ext.AsSpan(2), out int cur)) n = cur + 1;
        if (n > 99) return null;
        string lower = $"{stem}.c{n:00}", upper = $"{stem}.C{n:00}";
        if (File.Exists(lower)) return lower;
        if (File.Exists(upper)) return upper;
        return lower;
    }

    private void LoadVolume(string path, int search)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        var vol = new Volume { Path = path, Stream = fs };
        _volumes.Add(vol);
        long start = FindMainHeader(fs, search);
        if (start < 0)
            throw new AceFormatException(search > 0
                ? "This isn't an ACE archive (no ACE header in the first 512 KB)."
                : "No ACE header at the start of this volume.");
        if (_volumes.Count == 1) StartOffset = start;
        fs.Position = start;
        bool haveMain = false;
        while (fs.Position < fs.Length)
        {
            long at = fs.Position;
            try
            {
                ParseHeader(vol, ref haveMain);
            }
            catch (AceFormatException ex) when (haveMain)
            {
                _warnings.Add($"{System.IO.Path.GetFileName(path)}: stopped reading at offset {at:N0} — {ex.Message} (the file may be truncated or have junk on the end).");
                break;
            }
        }
        if (!haveMain) throw new AceFormatException("ACE main header is missing.");
    }

    private void ParseHeader(Volume vol, ref bool haveMain)
    {
        var fs = vol.Stream;
        var head = new byte[4];
        if (ReadFully(fs, head, 0, 4) < 4) throw new AceFormatException("Truncated header.");
        ushort hcrc = BinaryPrimitives.ReadUInt16LittleEndian(head);
        int hsize = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        var b = new byte[hsize];
        if (ReadFully(fs, b, 0, hsize) < hsize) throw new AceFormatException("Truncated header.");
        if ((AceCrc32.Compute(b) & 0xFFFF) != hcrc) throw new AceFormatException("Header checksum doesn't match.");
        if (hsize < 3) throw new AceFormatException("Header too short.");
        int type = b[0];
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(1));
        int i = 3;

        void Need(int n) { if (i + n > b.Length) throw new AceFormatException("Truncated header."); }

        switch (type)
        {
            case TypeMain:
            {
                if (haveMain) throw new AceFormatException("Two main headers in one volume.");
                Need(23);
                if (!b.AsSpan(3, 7).SequenceEqual(Magic)) throw new AceFormatException("Main header without the **ACE** signature.");
                int ever = b[10], cver = b[11], host = b[12], volNo = b[13];
                uint dt = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(14));
                i = 26;
                // The advert length byte is always there (contrary to the 1.2 tech note), as in real archives.
                Need(1);
                int avsz = b[i++];
                Need(avsz);
                string advert = Encoding.Latin1.GetString(b, i, avsz);
                i += avsz;
                string comment = "";
                if ((flags & FlagComment) != 0)
                {
                    Need(2);
                    int cmsz = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i)); i += 2;
                    Need(cmsz);
                    comment = DecodeComment(b.AsSpan(i, cmsz));
                    i += cmsz;
                }
                vol.Number = volNo;
                vol.MultiVolume = (flags & FlagMultiVolume) != 0;
                if (_volumes.Count == 1)
                {
                    VersionNeeded = ever;
                    VersionMadeBy = cver;
                    HostOs = HostName(host);
                    Created = FromDos(dt);
                    Advert = advert.TrimEnd('\0');
                    Comment = comment;
                    IsSolid = (flags & FlagSolid) != 0;
                    IsMultiVolume = vol.MultiVolume;
                    IsLocked = (flags & FlagLocked) != 0;
                    IsSelfExtracting = (flags & FlagSfx) != 0;
                    HasRecoveryRecord = (flags & FlagRecovery) != 0;
                }
                haveMain = true;
                break;
            }
            case TypeFile32:
            case TypeFile64:
            {
                if (!haveMain) throw new AceFormatException("File header before the main header.");
                if ((flags & FlagAddSize) == 0) throw new AceFormatException("File header without a data size.");
                long pack, orig;
                if ((flags & Flag64Bit) != 0)
                {
                    if (type != TypeFile64) throw new AceFormatException("64-bit flag in a 32-bit header.");
                    Need(16);
                    pack = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i));
                    orig = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i + 8));
                    i += 16;
                }
                else
                {
                    if (type != TypeFile32) throw new AceFormatException("32-bit flag in a 64-bit header.");
                    Need(8);
                    pack = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));
                    orig = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i + 4));
                    i += 8;
                }
                if (pack < 0 || orig < 0) throw new AceFormatException("Impossible member size.");
                Need(20);
                uint dt = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));
                uint attr = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i + 4));
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i + 8));
                int comptype = b[i + 12], compqual = b[i + 13];
                int prms = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i + 14));
                int fnsz = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i + 18));
                i += 20;
                Need(fnsz);
                var raw = b.AsSpan(i, fnsz).ToArray();
                i += fnsz;
                string comment = "";
                if ((flags & FlagComment) != 0)
                {
                    Need(2);
                    int cmsz = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i)); i += 2;
                    Need(cmsz);
                    comment = DecodeComment(b.AsSpan(i, cmsz));
                    i += cmsz;
                }
                if ((flags & FlagNtSecurity) != 0)
                {
                    Need(2);
                    int nssz = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i)); i += 2;
                    Need(nssz);
                    i += nssz;   // NTFS permissions: not restored
                }
                long dataOffset = fs.Position;
                if (dataOffset + pack > fs.Length)
                    throw new AceFormatException($"Member data runs past the end of the file ({pack:N0} bytes declared).");
                var draft = new AceMember(0, "", raw, orig, pack, FromDos(dt), attr, crc, comptype, compqual,
                    (prms & 15) + 10, (flags & FlagPassword) != 0, comment, _volumes.Count - 1, 1);
                draft.Segments.Add((_volumes.Count - 1, dataOffset, pack));
                vol.Files.Add((flags, raw, draft));
                fs.Position = dataOffset + pack;
                break;
            }
            case TypeRecovery32:
            case TypeRecovery64A:
            case TypeRecovery64B:
            {
                if ((flags & FlagAddSize) == 0) throw new AceFormatException("Recovery header without a data size.");
                long size;
                if ((flags & Flag64Bit) != 0) { Need(8); size = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i)); }
                else { Need(4); size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i)); }
                if (size < 0 || fs.Position + size > fs.Length) throw new AceFormatException("Recovery record runs past the end of the file.");
                fs.Position += size;   // recovery data is only needed for repairs
                break;
            }
            default:
            {
                long add = 0;
                if ((flags & FlagAddSize) != 0)
                {
                    if ((flags & Flag64Bit) != 0) { Need(8); add = (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i)); }
                    else { Need(4); add = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i)); }
                }
                if (add < 0 || fs.Position + add > fs.Length) throw new AceFormatException("Unknown block runs past the end of the file.");
                fs.Position += add;
                break;
            }
        }
    }

    private void BuildMembers()
    {
        // Check the volumes follow on from each other.
        for (int v = 1; v < _volumes.Count; v++)
        {
            if (!_volumes[v].MultiVolume)
                throw new AceFormatException($"{System.IO.Path.GetFileName(_volumes[v].Path)} is not part of a multi-volume archive.");
            if (_volumes[v].Number != _volumes[v - 1].Number + 1)
                throw new AceFormatException($"{System.IO.Path.GetFileName(_volumes[v].Path)} is volume {_volumes[v].Number + 1}, expected volume {_volumes[v - 1].Number + 2}.");
        }
        if (_volumes[0].MultiVolume && _volumes[0].Number > 0)
            _warnings.Add($"This is volume {_volumes[0].Number + 1} of a set and the first volume isn't here. Files that started in an earlier volume are skipped" +
                          (IsSolid ? "; because the archive is solid, the rest probably can't be unpacked either." : "."));

        AceMember? current = null;
        for (int v = 0; v < _volumes.Count; v++)
        {
            foreach (var (flags, raw, draft) in _volumes[v].Files)
            {
                if (current is null)
                {
                    if ((flags & FlagContPrev) != 0)
                    {
                        if (_members.Count > 0 || v > 0)
                            throw new AceFormatException("A volume starts in the middle of a file that was never started.");
                        continue;   // the rest of a file from a volume we don't have
                    }
                    current = draft with { Index = _members.Count, Name = CleanName(raw, _members.Count), Segments = new(draft.Segments) };
                }
                else
                {
                    if ((flags & FlagContPrev) == 0)
                        throw new AceFormatException($"{current.Name} continues into the next volume, but that volume starts a new file.");
                    if (!raw.AsSpan().SequenceEqual(current.RawName))
                        throw new AceFormatException($"{current.Name} continues in the next volume under a different name.");
                    var segs = current.Segments.ToList();
                    segs.AddRange(draft.Segments);
                    // The last part carries the CRC of the whole file.
                    current = current with { PackedSize = current.PackedSize + draft.PackedSize, Crc32 = draft.Crc32, VolumeCount = current.VolumeCount + 1, Segments = segs };
                }
                if ((flags & FlagContNext) == 0)
                {
                    _members.Add(current);
                    current = null;
                }
            }
        }
        if (current is not null)
        {
            string missing = NextVolumeName(_volumes[^1].Path) is { } nv ? System.IO.Path.GetFileName(nv) : "the next volume";
            _warnings.Add($"{current.Name} continues in {missing}, which isn't here — it can't be unpacked until you add it.");
            _incomplete = current;
        }
    }

    private AceMember? _incomplete;

    /// <summary>The last file, if it continues in a volume that is missing.</summary>
    public AceMember? IncompleteMember => _incomplete;

    // ------------------------------------------------------------------------------------ names

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private string DecodeName(byte[] raw)
    {
        int nul = Array.IndexOf(raw, (byte)0);
        var span = nul >= 0 ? raw.AsSpan(0, nul) : raw.AsSpan();
        if (_forcedEncoding is not null) return _forcedEncoding.GetString(span);
        return DecodeAuto(span);
    }

    /// <summary>UTF-8 if the bytes are valid UTF-8 (always true for plain ASCII), otherwise the DOS
    /// code page 437 that WinAce and DOS ACE wrote names in.</summary>
    internal static string DecodeAuto(ReadOnlySpan<byte> span)
    {
        try { return new UTF8Encoding(false, true).GetString(span); }
        catch (DecoderFallbackException) { return Cp437.GetString(span); }
    }

    private static Encoding? _cp437;
    internal static Encoding Cp437
    {
        get
        {
            if (_cp437 is null)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _cp437 = Encoding.GetEncoding(437);
            }
            return _cp437;
        }
    }

    private string CleanName(byte[] raw, int index)
    {
        string s = SanitizeRelativePath(DecodeName(raw));
        return s.Length == 0 ? $"file{index:0000}" : s;
    }

    /// <summary>
    /// Turn a stored name into a safe relative path using '/' separators: both slash kinds split
    /// folders; drive letters, colons, control and wildcard characters, "." and ".." parts and leading
    /// separators are removed; Windows device names (CON, NUL, COM1 …) get a leading underscore; and
    /// trailing dots and spaces are trimmed. May return "" (the caller then invents a name).
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
                // Resolve "a/../b" the way the archive meant it, but never climb above the top.
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (trimmed.Trim('.').Trim(' ').Length == 0) continue;   // "", ".", "...", ". ." and similar
            string part = rawPart.TrimEnd('.', ' ').TrimStart(' ');
            if (part.Length == 0) continue;
            string stem = part.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem)) part = "_" + part;
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    /// <summary>Full output path for <paramref name="relative"/> under <paramref name="root"/>, or an
    /// exception if it would land anywhere else (belt and braces after <see cref="SanitizeRelativePath"/>).</summary>
    public static string ContainedPath(string root, string relative)
    {
        string fullRoot = System.IO.Path.GetFullPath(root);
        string rootWithSep = fullRoot.EndsWith(System.IO.Path.DirectorySeparatorChar) ? fullRoot : fullRoot + System.IO.Path.DirectorySeparatorChar;
        if (System.IO.Path.IsPathRooted(relative)) throw new AceFormatException($"Refusing absolute path \"{relative}\".");
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSep, cmp)) throw new AceFormatException($"Refusing to write outside the output folder: \"{relative}\".");
        // Don't follow a link or junction that already exists inside the output folder.
        for (var dir = System.IO.Path.GetDirectoryName(full); dir is not null && dir.Length > fullRoot.Length; dir = System.IO.Path.GetDirectoryName(dir))
        {
            var di = new DirectoryInfo(dir);
            if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new AceFormatException($"Refusing to write through the link \"{dir}\".");
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new AceFormatException($"Refusing to overwrite the link \"{full}\".");
        return full;
    }

    // ------------------------------------------------------------------------------------ reading

    /// <summary>Test every member (decompress and check CRC) without writing anything.</summary>
    public AceRunResult TestAll(string? password = null, IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
        => Run(null, password, overwrite: false, progress, ct);

    /// <summary>Extract every member into <paramref name="destination"/>. Existing files are left
    /// alone (reported as failures) unless <paramref name="overwrite"/> is set.</summary>
    public AceRunResult ExtractAll(string destination, string? password = null, bool overwrite = false,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        return Run(destination, password, overwrite, progress, ct);
    }

    private AceRunResult Run(string? destination, string? password, bool overwrite,
        IProgress<(int Done, int Total, string Name)>? progress, CancellationToken ct)
    {
        var results = new List<AceMemberResult>();
        var dec = new AceDecompressor();
        var dirTimes = new List<(string Path, DateTime Time)>();
        for (int k = 0; k < _members.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var m = _members[k];
            progress?.Report((k, _members.Count, m.Name));
            string? outPath = null;
            try
            {
                if (destination is not null)
                {
                    outPath = ContainedPath(destination, m.Name);
                    if (m.IsDirectory)
                    {
                        Directory.CreateDirectory(outPath);
                        dirTimes.Add((outPath, m.Modified));
                        results.Add(new AceMemberResult(m, true, outPath, null));
                        continue;
                    }
                    if (!overwrite && File.Exists(outPath))
                    {
                        // In a solid archive the data still has to be decoded to keep the stream in step.
                        if (IsSolid) Decode(dec, m, password, Stream.Null);
                        results.Add(new AceMemberResult(m, false, outPath, "already exists (not overwritten)"));
                        continue;
                    }
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
                    string tmp = outPath + ".dfpart";
                    try
                    {
                        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                            Decode(dec, m, password, fs);
                        File.Move(tmp, outPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    try { File.SetLastWriteTime(outPath, m.Modified); } catch (IOException) { } catch (ArgumentException) { }
                    results.Add(new AceMemberResult(m, true, outPath, null));
                }
                else
                {
                    if (!m.IsDirectory) Decode(dec, m, password, Stream.Null);
                    results.Add(new AceMemberResult(m, true, null, null));
                }
            }
            catch (Exception ex) when (ex is AceFormatException or AcePasswordException or IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                results.Add(new AceMemberResult(m, false, outPath, ex.Message));
            }
        }
        if (_incomplete is not null)
            results.Add(new AceMemberResult(_incomplete, false, null, "continues in a volume that isn't here"));
        foreach (var (p, t) in dirTimes)
            try { Directory.SetLastWriteTime(p, t); } catch (IOException) { } catch (ArgumentException) { }
        progress?.Report((_members.Count, _members.Count, ""));
        return new AceRunResult(results);
    }

    /// <summary>Decompress one member to <paramref name="output"/>. In a solid archive the members before
    /// it are decoded first (to nowhere), because each one continues from the last.</summary>
    public void Extract(AceMember member, Stream output, string? password = null)
    {
        var dec = new AceDecompressor();
        if (IsSolid)
            foreach (var m in _members)
            {
                if (m.Index == member.Index) break;
                if (!m.IsDirectory) Decode(dec, m, password, Stream.Null);
            }
        Decode(dec, member, password, output);
    }

    private void Decode(AceDecompressor dec, AceMember m, string? password, Stream output)
    {
        if (m.IsDirectory || m.Size == 0) return;
        Stream packed = new SegmentStream(_volumes, m.Segments);
        if (m.IsEncrypted)
        {
            if (string.IsNullOrEmpty(password)) throw new AcePasswordException("This file is password-protected — enter the password.");
            packed = new AceDecryptingStream(packed, new AceBlowfish(Encoding.UTF8.GetBytes(password)));
        }
        uint crc;
        try
        {
            crc = dec.Decompress(m.CompressionType, packed, m.Size, output);
        }
        catch (Exception ex) when (m.IsEncrypted && ex is AceFormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new AcePasswordException("Wrong password (or the file is damaged).");
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new AceFormatException("The compressed data is damaged.");
        }
        if (crc != m.Crc32)
        {
            if (m.IsEncrypted) throw new AcePasswordException("Wrong password (or the file is damaged) — CRC doesn't match.");
            throw new AceFormatException($"CRC doesn't match (expected {m.Crc32:X8}, got {crc:X8}) — the file is damaged.");
        }
    }

    /// <summary>Reads a member's packed bytes across one or more volumes.</summary>
    private sealed class SegmentStream : Stream
    {
        private readonly List<Volume> _vols;
        private readonly List<(int Volume, long Offset, long Length)> _segs;
        private int _seg;
        private long _inSeg;

        public SegmentStream(List<Volume> vols, List<(int, long, long)> segs) { _vols = vols; _segs = segs; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_seg < _segs.Count)
            {
                var (v, off, len) = _segs[_seg];
                long left = len - _inSeg;
                if (left <= 0) { _seg++; _inSeg = 0; continue; }
                var fs = _vols[v].Stream;
                fs.Position = off + _inSeg;
                int n = fs.Read(buffer, offset, (int)Math.Min(count, left));
                if (n <= 0) throw new EndOfStreamException("ACE volume ended early.");
                _inSeg += n;
                return n;
            }
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>Archive and file comments: a small LZP-style coder over the LZ77 main Huffman code.</summary>
    internal static string DecodeComment(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";
        try
        {
            var bs = new AceBitStream(new MemoryStream(data.ToArray()));
            int want = (int)bs.Read(15);
            var tree = AceHuffmanTree.Read(bs, AceLz77.MaxCodeWidth, AceLz77.NumMainCodes);
            var outp = new List<byte>(want);
            var htab = new int[511];
            while (outp.Count < want)
            {
                int src = 0;
                if (outp.Count > 1)
                {
                    int h = outp[^1] + outp[^2];
                    src = htab[h];
                    htab[h] = outp.Count;
                }
                int code = tree.ReadSymbol(bs);
                if (code < 256) outp.Add((byte)code);
                else
                    for (int k = 0; k < code - 256 + 2 && outp.Count < want; k++)
                    {
                        if (src + k >= outp.Count) throw new AceFormatException("Bad comment data.");
                        outp.Add(outp[src + k]);
                    }
            }
            var bytes = outp.ToArray();
            string s;
            try { s = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { s = Cp437.GetString(bytes); }
            return s.TrimEnd('\0').Replace("\r\n", "\n").Replace('\r', '\n');
        }
        catch (Exception ex) when (ex is AceFormatException or EndOfStreamException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return "(comment could not be decoded)";
        }
    }

    private static readonly string[] Hosts =
        { "MS-DOS", "OS/2", "Win32", "Unix", "Mac OS", "Win NT", "Primos", "Apple GS", "Atari", "VAX VMS", "Amiga", "NeXT", "Linux" };

    private static string HostName(int host) => host < Hosts.Length ? Hosts[host] : $"host {host}";

    internal static DateTime FromDos(uint dos)
    {
        try
        {
            return new DateTime((int)((dos >> 25) & 0x7F) + 1980, (int)((dos >> 21) & 0x0F), (int)((dos >> 16) & 0x1F),
                (int)((dos >> 11) & 0x1F), (int)((dos >> 5) & 0x3F), (int)(dos & 0x1F) * 2, DateTimeKind.Local);
        }
        catch (ArgumentOutOfRangeException) { return new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local); }
    }

    public static string VersionText(int v) => v <= 0 ? "?" : $"{v / 10}.{v % 10}";

    private static int ReadFully(Stream s, byte[] buf, int off, int count)
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

    public void Dispose()
    {
        foreach (var v in _volumes) v.Stream.Dispose();
        _volumes.Clear();
    }
}
