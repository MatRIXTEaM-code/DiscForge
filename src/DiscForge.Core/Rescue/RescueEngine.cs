// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// The rescue strategy follows the phases GNU ddrescue documents in its manual ("Algorithm"):
// copying (with skipping), trimming, scraping and retrying. Written from that description.

using System.Diagnostics;

namespace DiscForge.Core.Rescue;

/// <summary>Something sectors can be read from: a drive, a device or a file.</summary>
public interface IRescueSource
{
    long SectorCount { get; }
    int SectorSize { get; }

    /// <summary>Read <paramref name="count"/> sectors into <paramref name="buffer"/>. Return false on a
    /// read error. Throw <see cref="RescueAbortException"/> for anything that must stop the whole
    /// rescue (for example a copy-protection response, or the disc being removed).</summary>
    bool TryRead(long lba, int count, Span<byte> buffer);
}

/// <summary>Stops the rescue (the map is still saved).</summary>
public sealed class RescueAbortException(string message) : Exception(message);

public sealed record RescueOptions
{
    /// <summary>Sectors per read while copying (default 32 = 64 KiB for 2048-byte sectors).</summary>
    public int ClusterSectors { get; init; } = 32;
    /// <summary>Sectors to skip after the first error while copying; 0 = no skipping.
    /// Null = disc size / 100,000 but at least 32 (64 KiB), as ddrescue does.</summary>
    public long? SkipInitialSectors { get; init; }
    /// <summary>Largest skip. Null = 1% of the disc.</summary>
    public long? SkipMaxSectors { get; init; }
    public bool Trim { get; init; } = true;
    public bool Scrape { get; init; } = true;
    /// <summary>Passes over the remaining bad sectors after scraping (direction alternates). When a
    /// continued rescue has nothing left but bad sectors, one pass is made even if this is 0.</summary>
    public int RetryPasses { get; init; }
    /// <summary>Only rescue part of the disc (sectors). Null = the whole disc.</summary>
    public long? StartSector { get; init; }
    public long? SectorLimit { get; init; }
    /// <summary>How often the map is checkpointed while running.</summary>
    public TimeSpan SaveInterval { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record RescueProgress(
    string Phase,
    int Pass,
    long PositionBytes,
    long Rescued,
    long NonTried,
    long NonTrimmed,
    long NonScraped,
    long BadSector,
    int BadAreas,
    long ReadErrors,
    long TotalBytes,
    TimeSpan Elapsed,
    IReadOnlyList<RescueBlock> Blocks)
{
    public double PercentRescued => TotalBytes == 0 ? 0 : 100.0 * Rescued / TotalBytes;
}

public sealed record RescueResult(RescueMap Map, long ReadErrors, TimeSpan Elapsed, bool Cancelled)
{
    public bool Complete => Map.IsComplete;
}

/// <summary>
/// Copies a damaged disc the way ddrescue does: good areas first, in big reads, jumping away from
/// errors; then narrowing each failed area from its edges; then going over what is left one sector
/// at a time; then (optionally) retrying the bad sectors. The ddrescue-format map records everything,
/// so the rescue can be stopped and resumed, or continued later with another drive.
/// </summary>
public sealed class RescueEngine
{
    private readonly IRescueSource _src;
    private readonly Stream _out;
    private readonly RescueMap _map;
    private readonly RescueOptions _opt;
    private readonly IProgress<RescueProgress>? _progress;
    private readonly Action<RescueMap, string>? _save;
    private readonly CancellationToken _ct;
    private readonly int _ss;
    private readonly byte[] _buf;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _sinceSave = new();
    private readonly Stopwatch _sinceReport = new();
    private long _errors;
    private string _phase = "Copying";
    private int _pass = 1;
    private long _domainStart, _domainEnd;   // bytes

    /// <param name="save">Called to persist the map (periodically, after each phase, and at the end).</param>
    public RescueEngine(IRescueSource source, Stream output, RescueMap map, RescueOptions? options = null,
        IProgress<RescueProgress>? progress = null, Action<RescueMap, string>? save = null, CancellationToken cancel = default)
    {
        _src = source;
        _out = output;
        _map = map;
        _opt = options ?? new RescueOptions();
        _progress = progress;
        _save = save;
        _ct = cancel;
        _ss = source.SectorSize;
        // The map may end mid-sector (a ddrescue map of a file whose size isn't a whole number of sectors).
        if (map.Size > source.SectorCount * _ss || map.Size <= (source.SectorCount - 1) * _ss)
            throw new ArgumentException($"The map is for a {map.Size:N0}-byte disc but this one is {source.SectorCount * (long)_ss:N0} bytes — a different disc?");
        _buf = new byte[Math.Max(1, _opt.ClusterSectors) * _ss];
    }

    public RescueResult Run()
    {
        _clock.Start();
        _sinceSave.Start();
        _sinceReport.Start();
        long startSec = Math.Clamp(_opt.StartSector ?? 0, 0, _src.SectorCount);
        long endSec = _opt.SectorLimit is long lim ? Math.Min(_src.SectorCount, startSec + lim) : _src.SectorCount;
        _domainStart = startSec * _ss;
        _domainEnd = Math.Min(endSec * _ss, _map.Size);
        bool cancelled = false;
        try
        {
            // A map where only bad sectors are left (for example a finished rescue, now continued in
            // another drive) always gives them at least one more try.
            int retries = _opt.RetryPasses;
            if (retries == 0 && _map.Total(RescueStatus.BadSector) > 0 && _map.Total(RescueStatus.NonTried) == 0
                && _map.Total(RescueStatus.NonTrimmed) == 0 && _map.Total(RescueStatus.NonScraped) == 0)
                retries = 1;
            // Resume the copying phase at the pass the map was saved in.
            int firstPass = _map.CurrentStatus == '?' ? Math.Clamp(_map.CurrentPass, 1, 3) : 1;
            if (firstPass <= 1) CopyPass(1, forward: true, skip: true);
            if (firstPass <= 2) CopyPass(2, forward: false, skip: true);
            CopyPass(3, forward: true, skip: false);
            if (_opt.Trim) TrimPhase();
            if (_opt.Scrape) ScrapePhase();
            for (int p = 1; p <= retries; p++) RetryPass(p, forward: p % 2 == 1);
            _map.CurrentStatus = '+';
            _map.CurrentPass = 1;
            _phase = "Finished";
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            Report(force: true);
            _save?.Invoke(_map, StatusMessage(cancelled));
        }
        return new RescueResult(_map, _errors, _clock.Elapsed, cancelled);
    }

    private string StatusMessage(bool cancelled) =>
        cancelled ? $"Interrupted by user during: {_phase}, pass {_pass}" :
        _phase == "Finished" ? "Finished" : $"{_phase}, pass {_pass}";

    // ------------------------------------------------------------------ plumbing

    private bool Read(long sector, int count)
    {
        _ct.ThrowIfCancellationRequested();
        var span = _buf.AsSpan(0, count * _ss);
        bool ok = _src.TryRead(sector, count, span);
        if (ok)
        {
            _out.Position = sector * _ss;
            _out.Write(span);
        }
        else _errors++;
        return ok;
    }

    private void Mark(long sector, long count, RescueStatus s)
    {
        long pos = sector * _ss;
        _map.Set(pos, Math.Min(count * _ss, _map.Size - pos), s);
    }

    private void Tick(long posBytes, char status)
    {
        _map.CurrentPos = posBytes;
        _map.CurrentStatus = status;
        _map.CurrentPass = _pass;
        Report(force: false);
        if (_save is not null && _sinceSave.Elapsed >= _opt.SaveInterval)
        {
            _out.Flush();
            _save(_map, StatusMessage(false));
            _sinceSave.Restart();
        }
    }

    private void Report(bool force)
    {
        if (_progress is null) return;
        if (!force && _sinceReport.ElapsedMilliseconds < 200) return;
        _sinceReport.Restart();
        _progress.Report(new RescueProgress(_phase, _pass, _map.CurrentPos, _map.Rescued,
            _map.Total(RescueStatus.NonTried), _map.Total(RescueStatus.NonTrimmed), _map.Total(RescueStatus.NonScraped),
            _map.Total(RescueStatus.BadSector), _map.BadAreas, _errors, _map.Size, _clock.Elapsed, _map.Blocks.ToArray()));
    }

    /// <summary>Blocks with status <paramref name="s"/>, clipped to the rescue domain, as sector ranges.</summary>
    private List<(long Start, long End)> Ranges(RescueStatus s)
    {
        var list = new List<(long, long)>();
        foreach (var b in _map.BlocksWith(s))
        {
            long a = Math.Max(b.Pos, _domainStart), z = Math.Min(b.End, _domainEnd);
            if (a < z) list.Add((a / _ss, (z + _ss - 1) / _ss));
        }
        return list;
    }

    private bool IsStatus(long sector, RescueStatus s) =>
        sector >= 0 && sector < _src.SectorCount && _map.StatusAt(sector * _ss) == s;

    // ------------------------------------------------------------------ phase 1: copying

    private void CopyPass(int pass, bool forward, bool skip)
    {
        _phase = pass == 3 ? "Copying (sweep)" : forward ? "Copying" : "Copying (backwards)";
        _pass = pass;
        long total = _src.SectorCount;
        long skipInit = _opt.SkipInitialSectors ?? Math.Max(32, total / 100_000);
        long skipMax = Math.Max(skipInit, _opt.SkipMaxSectors ?? Math.Max(skipInit, total / 100));
        if (!skip) skipInit = 0;
        int cluster = Math.Max(1, _opt.ClusterSectors);

        var ranges = Ranges(RescueStatus.NonTried);
        if (!forward) ranges.Reverse();
        long skipSize = skipInit;
        foreach (var (start, end) in ranges)
        {
            if (forward)
            {
                long pos = start;
                while (pos < end)
                {
                    int n = (int)Math.Min(cluster, end - pos);
                    if (!IsStatus(pos, RescueStatus.NonTried)) { pos++; continue; }
                    bool ok = Read(pos, n);
                    Mark(pos, n, ok ? RescueStatus.Finished : RescueStatus.NonTrimmed);
                    pos += n;
                    if (ok) skipSize = skipInit;
                    else if (skipInit > 0)
                    {
                        pos = Math.Min(end, pos + skipSize);   // leave the skipped part non-tried
                        skipSize = Math.Min(skipMax, skipSize * 2);
                    }
                    Tick(pos * _ss, '?');
                }
            }
            else
            {
                long pos = end;
                while (pos > start)
                {
                    int n = (int)Math.Min(cluster, pos - start);
                    long at = pos - n;
                    bool ok = Read(at, n);
                    Mark(at, n, ok ? RescueStatus.Finished : RescueStatus.NonTrimmed);
                    pos = at;
                    // Pass 2 delimits each skipped area from the other end; after the first error in a
                    // block it leaves the rest of that block for the sweep.
                    if (!ok && skipInit > 0) break;
                    Tick(pos * _ss, '?');
                }
            }
        }
        _map.CurrentPass = pass + 1;
        _save?.Invoke(_map, StatusMessage(false));
        _sinceSave.Restart();
    }

    // ------------------------------------------------------------------ phase 2: trimming

    private void TrimPhase()
    {
        _phase = "Trimming";
        _pass = 1;
        foreach (var (start, end) in Ranges(RescueStatus.NonTrimmed))
        {
            long lo = start, hi = end;   // [lo, hi) still non-trimmed
            // Leading edge, forwards — unless it already touches a bad sector.
            if (!IsStatus(start - 1, RescueStatus.BadSector))
            {
                while (lo < hi)
                {
                    bool ok = Read(lo, 1);
                    Mark(lo, 1, ok ? RescueStatus.Finished : RescueStatus.BadSector);
                    lo++;
                    Tick(lo * _ss, '*');
                    if (!ok) break;
                }
            }
            // Trailing edge, backwards.
            if (lo < hi && !IsStatus(end, RescueStatus.BadSector))
            {
                while (hi > lo)
                {
                    hi--;
                    bool ok = Read(hi, 1);
                    Mark(hi, 1, ok ? RescueStatus.Finished : RescueStatus.BadSector);
                    Tick(hi * _ss, '*');
                    if (!ok) break;
                }
            }
            if (lo < hi) Mark(lo, hi - lo, RescueStatus.NonScraped);
        }
        _save?.Invoke(_map, StatusMessage(false));
        _sinceSave.Restart();
    }

    // ------------------------------------------------------------------ phase 3: scraping

    private void ScrapePhase()
    {
        _phase = "Scraping";
        _pass = 1;
        foreach (var (start, end) in Ranges(RescueStatus.NonScraped))
            for (long s = start; s < end; s++)
            {
                bool ok = Read(s, 1);
                Mark(s, 1, ok ? RescueStatus.Finished : RescueStatus.BadSector);
                Tick((s + 1) * _ss, '/');
            }
        _save?.Invoke(_map, StatusMessage(false));
        _sinceSave.Restart();
    }

    // ------------------------------------------------------------------ phase 4: retrying

    private void RetryPass(int pass, bool forward)
    {
        _phase = forward ? "Retrying" : "Retrying (backwards)";
        _pass = pass;
        var sectors = new List<long>();
        foreach (var (start, end) in Ranges(RescueStatus.BadSector))
            for (long s = start; s < end; s++) sectors.Add(s);
        if (!forward) sectors.Reverse();
        foreach (var s in sectors)
        {
            if (Read(s, 1)) Mark(s, 1, RescueStatus.Finished);
            Tick(s * _ss, '-');
        }
        _save?.Invoke(_map, StatusMessage(false));
        _sinceSave.Restart();
    }
}

/// <summary>Rescue from a file or block device (reads that fail with an I/O error count as bad).</summary>
public sealed class FileRescueSource : IRescueSource, IDisposable
{
    private readonly FileStream _fs;

    public FileRescueSource(string path, int sectorSize = 2048)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        SectorSize = sectorSize;
        ByteLength = _fs.Length;
        SectorCount = (ByteLength + sectorSize - 1) / sectorSize;
    }

    /// <summary>The file's exact length (the last sector may be partial).</summary>
    public long ByteLength { get; }

    public long SectorCount { get; }
    public int SectorSize { get; }

    public bool TryRead(long lba, int count, Span<byte> buffer)
    {
        try
        {
            _fs.Position = lba * SectorSize;
            int total = 0;
            while (total < buffer.Length)
            {
                int n = _fs.Read(buffer[total..]);
                if (n <= 0) { buffer[total..].Clear(); break; }   // short last sector
                total += n;
            }
            return true;
        }
        catch (IOException) { return false; }
    }

    public void Dispose() => _fs.Dispose();
}
