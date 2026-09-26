// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// The map format is GNU ddrescue's documented "mapfile" (ddrescue manual, "Mapfile structure"), so a
// rescue can be continued in either program and inspected with ddrescuelog. No ddrescue code is used.

using System.Globalization;
using System.Text;

namespace DiscForge.Core.Rescue;

/// <summary>State of a stretch of the disc, using ddrescue's status characters.</summary>
public enum RescueStatus : byte
{
    NonTried = (byte)'?',
    NonTrimmed = (byte)'*',
    NonScraped = (byte)'/',
    BadSector = (byte)'-',
    Finished = (byte)'+',
}

/// <summary>One contiguous run of the map, in bytes.</summary>
public readonly record struct RescueBlock(long Pos, long Size, RescueStatus Status)
{
    public long End => Pos + Size;
}

/// <summary>
/// Which parts of a disc have been copied, which failed and how far each failed part has been
/// retried — a ddrescue-compatible mapfile. Positions and sizes are in bytes. The blocks always cover
/// the whole disc, in order, with no gaps or overlaps.
/// </summary>
public sealed class RescueMap
{
    private readonly List<RescueBlock> _blocks = new();

    /// <summary>Size of the rescue domain in bytes (the whole disc).</summary>
    public long Size { get; private set; }

    /// <summary>Where the rescue was when the map was saved (ddrescue's current_pos).</summary>
    public long CurrentPos { get; set; }
    /// <summary>ddrescue's current_status: '?' copying, '*' trimming, '/' scraping, '-' retrying, '+' finished.</summary>
    public char CurrentStatus { get; set; } = '?';
    public int CurrentPass { get; set; } = 1;

    public IReadOnlyList<RescueBlock> Blocks => _blocks;

    private RescueMap() { }

    /// <summary>A fresh map: the whole disc not tried yet.</summary>
    public static RescueMap CreateNew(long size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var m = new RescueMap { Size = size };
        m._blocks.Add(new RescueBlock(0, size, RescueStatus.NonTried));
        return m;
    }

    // ------------------------------------------------------------------ queries

    public long Total(RescueStatus s)
    {
        long t = 0;
        foreach (var b in _blocks) if (b.Status == s) t += b.Size;
        return t;
    }

    public int Count(RescueStatus s) => _blocks.Count(b => b.Status == s);

    public long Rescued => Total(RescueStatus.Finished);
    public bool IsComplete => _blocks.Count == 1 && _blocks[0].Status == RescueStatus.Finished;

    /// <summary>Number of separate bad areas (runs of bad-sector), as ddrescue counts them.</summary>
    public int BadAreas => Count(RescueStatus.BadSector);

    public RescueStatus StatusAt(long pos) => _blocks[IndexAt(pos)].Status;

    private int IndexAt(long pos)
    {
        if (pos < 0 || pos >= Size) throw new ArgumentOutOfRangeException(nameof(pos));
        int lo = 0, hi = _blocks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_blocks[mid].Pos <= pos) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Snapshot of the blocks with a given status (safe to modify the map while iterating it).</summary>
    public List<RescueBlock> BlocksWith(RescueStatus s) => _blocks.Where(b => b.Status == s).ToList();

    /// <summary>The block containing <paramref name="pos"/>.</summary>
    public RescueBlock BlockAt(long pos) => _blocks[IndexAt(pos)];

    // ------------------------------------------------------------------ changes

    /// <summary>Mark [pos, pos+size) with <paramref name="status"/>, splitting and merging blocks as needed.</summary>
    public void Set(long pos, long size, RescueStatus status)
    {
        if (size <= 0) return;
        if (pos < 0 || pos + size > Size) throw new ArgumentOutOfRangeException(nameof(pos));
        long end = pos + size;
        int first = IndexAt(pos);
        int last = IndexAt(end - 1);
        var repl = new List<RescueBlock>(3);
        var a = _blocks[first];
        if (a.Pos < pos) repl.Add(a with { Size = pos - a.Pos });
        repl.Add(new RescueBlock(pos, size, status));
        var z = _blocks[last];
        if (z.End > end) repl.Add(new RescueBlock(end, z.End - end, z.Status));
        _blocks.RemoveRange(first, last - first + 1);
        _blocks.InsertRange(first, repl);
        // Merge with neighbours of the same status.
        int lo = Math.Max(0, first - 1), hi = Math.Min(_blocks.Count - 1, first + repl.Count);
        for (int i = hi; i > lo; i--)
        {
            if (_blocks[i].Status == _blocks[i - 1].Status)
            {
                _blocks[i - 1] = _blocks[i - 1] with { Size = _blocks[i - 1].Size + _blocks[i].Size };
                _blocks.RemoveAt(i);
            }
        }
    }

    // ------------------------------------------------------------------ text format

    /// <summary>Read a ddrescue mapfile. Numbers may be decimal, 0x hex or 0 octal, as ddrescue allows.</summary>
    public static RescueMap Parse(string text)
    {
        var m = new RescueMap();
        bool haveStatus = false;
        long expect = 0;
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!haveStatus)
            {
                if (parts.Length < 2) throw new FormatException($"Line {lineNo}: expected the status line (position, status, pass).");
                m.CurrentPos = ParseNumber(parts[0], lineNo);
                if (parts[1].Length != 1 || "?*/-FG+".IndexOf(parts[1][0]) < 0)
                    throw new FormatException($"Line {lineNo}: unknown current status '{parts[1]}'.");
                m.CurrentStatus = parts[1][0];
                m.CurrentPass = parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pass) ? pass : 1;
                haveStatus = true;
                continue;
            }
            if (parts.Length < 3) throw new FormatException($"Line {lineNo}: expected position, size and status.");
            long pos = ParseNumber(parts[0], lineNo), size = ParseNumber(parts[1], lineNo);
            if (parts[2].Length != 1 || "?*/-+".IndexOf(parts[2][0]) < 0)
                throw new FormatException($"Line {lineNo}: unknown block status '{parts[2]}'.");
            if (pos != expect) throw new FormatException($"Line {lineNo}: blocks must be contiguous (expected position 0x{expect:X}).");
            if (size <= 0) throw new FormatException($"Line {lineNo}: block size must be positive.");
            var st = (RescueStatus)(byte)parts[2][0];
            if (m._blocks.Count > 0 && m._blocks[^1].Status == st)
                m._blocks[^1] = m._blocks[^1] with { Size = m._blocks[^1].Size + size };
            else m._blocks.Add(new RescueBlock(pos, size, st));
            expect = pos + size;
        }
        if (!haveStatus) throw new FormatException("Empty mapfile.");
        if (m._blocks.Count == 0) throw new FormatException("The mapfile lists no blocks.");
        m.Size = expect;
        return m;
    }

    private static long ParseNumber(string s, int lineNo)
    {
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (s.Length > 1 && s[0] == '0') return System.Convert.ToInt64(s[1..], 8);
            return long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException($"Line {lineNo}: '{s}' is not a number.");
        }
    }

    public static RescueMap Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The mapfile text, in ddrescue's layout.</summary>
    public string Format(string? commandLine = null, string? statusMessage = null, DateTime? started = null)
    {
        var sb = new StringBuilder();
        sb.Append("# Mapfile. Created by DiscForge (GNU ddrescue mapfile format)\n");
        if (commandLine is not null) sb.Append("# Command line: ").Append(commandLine).Append('\n');
        if (started is not null) sb.Append("# Start time:   ").Append(started.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# Current time: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        if (statusMessage is not null) sb.Append("# ").Append(statusMessage).Append('\n');
        sb.Append("# current_pos  current_status  current_pass\n");
        sb.Append(CultureInfo.InvariantCulture, $"0x{CurrentPos:X8}     {CurrentStatus}               {CurrentPass}\n");
        sb.Append("#      pos        size  status\n");
        foreach (var b in _blocks)
            sb.Append(CultureInfo.InvariantCulture, $"0x{b.Pos:X8}  0x{b.Size:X8}  {(char)b.Status}\n");
        return sb.ToString();
    }

    /// <summary>Save atomically (write a temporary file, keep the previous copy as .bak, then swap).</summary>
    public void Save(string path, string? commandLine = null, string? statusMessage = null, DateTime? started = null)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Format(commandLine, statusMessage, started), new UTF8Encoding(false));
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Every sector that isn't finished (for DiscForge's bad-sector sidecar).</summary>
    public IEnumerable<long> UnfinishedSectors(int sectorSize)
    {
        foreach (var b in _blocks)
        {
            if (b.Status == RescueStatus.Finished) continue;
            for (long s = b.Pos / sectorSize; s * sectorSize < b.End; s++) yield return s;
        }
    }
}
