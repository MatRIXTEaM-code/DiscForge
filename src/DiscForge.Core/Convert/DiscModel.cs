// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Chd;
using DiscForge.Core.Ciso;
using DiscForge.Core.Cue;
using DiscForge.Core.Gdi;
using DiscForge.Core.Mds;
using DiscForge.Core.Nrg;
using DiscForge.Core.Wbfs;

namespace DiscForge.Core.Convert;

/// <summary>
/// Raised when a disc image cannot be read into, or written out of, the
/// canonical <see cref="DiscModel"/> — an unknown extension, a malformed image,
/// or a conversion the source/target format cannot represent.
/// </summary>
public sealed class DiscConvertException(string message) : Exception(message);

/// <summary>
/// One track of a <see cref="DiscModel"/>. <see cref="Data"/> is the track's
/// <em>content</em> sectors laid out back-to-back at <see cref="SectorSize"/>
/// bytes each, exactly as they sit in a BIN (audio little-endian). A pregap is
/// carried as the <see cref="PregapSectors"/> count rather than as bytes in
/// <see cref="Data"/> — the same convention DiscForge's CDI &lt;-&gt; BIN/CUE
/// bridge uses, so silence pregaps are regenerated on write rather than stored.
/// </summary>
public sealed record DiscModelTrack
{
    public required int Number { get; init; }
    /// <summary>1-based session (1 unless a multisession source says otherwise).</summary>
    public int Session { get; init; } = 1;
    public required CueTrackType Type { get; init; }
    /// <summary>Length of the (generated) pregap that precedes the content, in sectors.</summary>
    public int PregapSectors { get; init; }
    /// <summary>Stored sector size in bytes (2048/2336/2352), matching <see cref="Type"/>.</summary>
    public required int SectorSize { get; init; }
    /// <summary>The track's content sectors, <see cref="SectorSize"/> bytes each.</summary>
    public required byte[] Data { get; init; }

    public int SectorCount => Data.Length / SectorSize;
}

/// <summary>
/// The canonical, format-neutral disc image every reader parses INTO and every
/// writer emits FROM: an in-memory equivalent of a BIN/CUE — an ordered list of
/// tracks, each raw sectors at its stored size. This is the hub of the
/// conversion star: N formats attach as spokes, so any supported format converts
/// to any other through a single Read -> model -> Write path instead of an
/// N×N pairwise matrix.
/// </summary>
public sealed record DiscModel
{
    public required IReadOnlyList<DiscModelTrack> Tracks { get; init; }
    /// <summary>Optional free-text comment / disc title carried through where a format supports it.</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// The universal disc-image conversion hub. <see cref="Read"/> detects the input
/// by extension and parses it into a <see cref="DiscModel"/>; <see cref="Write"/>
/// detects the output by extension and emits from the model; <see cref="Convert"/>
/// is simply Write(Read()). Every branch REUSES DiscForge's own clean-room
/// readers/writers (ChdExtractor/ChdWriter, CdiConverter, NrgConverter,
/// CisoImage, WbfsReader, MdsConverter, GdiConverter, CloneCdReader, …) — the hub
/// is glue, not a reimplementation. Formats whose native reader/writer works with
/// files on disk are bridged through a private temp directory that materialises
/// the model as a BIN/CUE and is cleaned up afterwards.
/// </summary>
public static class DiscConverter
{
    private const CdiVersion DefaultCdiVersion = CdiVersion.V35;

    // ---- public API ---------------------------------------------------------

    /// <summary>Read <paramref name="inPath"/> and write it as <paramref name="outPath"/>,
    /// routing through the canonical model. Formats are chosen by extension.</summary>
    public static void Convert(string inPath, string outPath)
        => Write(Read(inPath), outPath);

    /// <summary>Detect the input format by extension and read it into the hub model.</summary>
    public static DiscModel Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new DiscConvertException($"File not found: {path}");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cue" => FromBinCue(File.ReadAllText(path), DirOf(path)),
            ".bin" => ReadLooseBin(path),
            ".chd" => ReadChd(path),
            ".iso" => ReadIso(path),
            ".cso" or ".zso" => ReadCiso(path),
            ".wbfs" => ReadWbfs(path),
            ".cdi" => ReadCdi(path),
            ".nrg" => ReadNrg(path),
            ".mds" => ReadMds(path),
            ".mdf" => ReadMds(Path.ChangeExtension(path, ".mds")),
            ".gdi" => ReadGdi(path),
            ".ccd" => ReadCloneCd(path),
            _ => throw new DiscConvertException(
                $"Unsupported input extension '{ext}'. Known inputs: .cue .bin .chd .iso .cso .zso " +
                ".wbfs .cdi .nrg .mds .gdi .ccd"),
        };
    }

    /// <summary>Detect the output format by extension and emit the hub model to it.</summary>
    public static void Write(DiscModel model, string outPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(outPath);
        if (model.Tracks.Count == 0)
            throw new DiscConvertException("The disc model has no tracks to write.");

        var ext = Path.GetExtension(outPath).ToLowerInvariant();
        switch (ext)
        {
            case ".cue": WriteBinCue(model, outPath); break;
            case ".chd": WriteChd(model, outPath); break;
            case ".iso": WriteIso(model, outPath); break;
            case ".cdi": WriteCdi(model, outPath); break;
            case ".nrg": WriteNrg(model, outPath); break;
            default:
                throw new DiscConvertException(
                    $"Unsupported output extension '{ext}'. Known outputs: .cue .chd .iso .cdi .nrg");
        }
    }

    // ---- BIN/CUE: the canonical bridge --------------------------------------

    /// <summary>Parse a cue and its bin file(s) into the model. Handles both a
    /// single merged bin (tracks split by absolute INDEX positions) and a
    /// one-bin-per-track cue.</summary>
    private static DiscModel FromBinCue(string cueText, string cueDir)
    {
        var sheet = CueSheet.Parse(cueText);
        if (sheet.Tracks.Count == 0)
            throw new DiscConvertException("The cue sheet declares no tracks.");

        // Group tracks by the bin file they live in, preserving cue order.
        var groups = new List<(string file, List<CueTrack> list)>();
        foreach (var t in sheet.Tracks)
        {
            if (groups.Count == 0 || groups[^1].file != t.File) groups.Add((t.File, new List<CueTrack>()));
            groups[^1].list.Add(t);
        }

        var outTracks = new List<DiscModelTrack>();
        foreach (var (file, list) in groups)
        {
            var binPath = Path.Combine(cueDir, file);
            if (!File.Exists(binPath))
                throw new DiscConvertException($"The cue references '{file}' but it was not found in {cueDir}.");
            var bin = File.ReadAllBytes(binPath);

            for (int i = 0; i < list.Count; i++)
            {
                var ct = list[i];
                int ss = SectorSizeFor(ct.Type);
                long fileFrames = bin.Length / ss;

                var idx0 = ct.Indices.FirstOrDefault(x => x.Number == 0);
                var idx1 = ct.Indices.FirstOrDefault(x => x.Number == 1);
                // Content begins at INDEX 01 (or INDEX 00 when a track has no 01).
                long contentStart = (idx1 ?? idx0)?.Time.ToSectors() ?? 0;
                // The region ends where the next track begins (its INDEX 00 if it has a
                // pregap, else 01), or at end-of-file for the last track in this bin.
                long nextStart = fileFrames;
                if (i + 1 < list.Count)
                {
                    var n = list[i + 1];
                    var n0 = n.Indices.FirstOrDefault(x => x.Number == 0);
                    var n1 = n.Indices.FirstOrDefault(x => x.Number == 1);
                    nextStart = (n0 ?? n1)?.Time.ToSectors() ?? fileFrames;
                }

                long contentFrames = nextStart - contentStart;
                if (contentFrames <= 0)
                    throw new DiscConvertException(
                        $"Track {ct.Number} resolves to a non-positive length ({contentFrames} sectors) — the cue's INDEX positions are inconsistent.");

                int pregap = (idx0 is not null && idx1 is not null)
                    ? (int)(idx1.Time.ToSectors() - idx0.Time.ToSectors())
                    : (ct.Pregap is { } pg ? (int)pg.ToSectors() : 0);

                long offset = contentStart * ss;
                long length = contentFrames * ss;
                if (offset + length > bin.Length)
                    throw new DiscConvertException(
                        $"Track {ct.Number} needs {length:N0} bytes at offset {offset:N0}, but '{file}' is only {bin.Length:N0} bytes.");

                var data = new byte[length];
                Array.Copy(bin, offset, data, 0, length);

                outTracks.Add(new DiscModelTrack
                {
                    Number = ct.Number, Session = ct.Session, Type = ct.Type,
                    PregapSectors = pregap, SectorSize = ss, Data = data,
                });
            }
        }

        return new DiscModel { Tracks = outTracks, Comment = sheet.Title };
    }

    /// <summary>Materialise the model as a one-bin-per-track BIN/CUE set in
    /// <paramref name="dir"/> (the shape DiscForge's writers consume), returning the
    /// cue text.</summary>
    private static string ToBinCue(DiscModel model, string dir, string baseName)
    {
        Directory.CreateDirectory(dir);
        var cueTracks = new List<CueTrack>();
        foreach (var t in model.Tracks)
        {
            if (t.Data.Length % t.SectorSize != 0)
                throw new DiscConvertException(
                    $"Track {t.Number}'s data ({t.Data.Length:N0} bytes) is not a whole number of {t.SectorSize}-byte sectors.");
            string binName = $"{baseName}_track{t.Number:D2}.bin";
            File.WriteAllBytes(Path.Combine(dir, binName), t.Data);

            var indices = new List<CueIndex> { new(1, Msf.FromSectors(0)) };
            Msf? pregap = t.PregapSectors > 0 ? Msf.FromSectors(t.PregapSectors) : null;
            cueTracks.Add(new CueTrack
            {
                Number = t.Number, Type = t.Type, File = binName,
                Pregap = pregap, Indices = indices, Session = t.Session,
            });
        }

        var sheet = new CueSheet { Tracks = cueTracks, Title = model.Comment };
        var cueText = sheet.Write();
        File.WriteAllText(Path.Combine(dir, baseName + ".cue"), cueText);
        return cueText;
    }

    private static void WriteBinCue(DiscModel model, string outPath)
    {
        var dir = DirOf(outPath);
        Directory.CreateDirectory(dir);
        ToBinCue(model, dir, Path.GetFileNameWithoutExtension(outPath));
    }

    private static DiscModel ReadLooseBin(string path)
    {
        // A .bin alone carries no track structure. Prefer a sibling cue; otherwise
        // treat the whole file as one raw MODE1/2352 data track.
        var cue = Path.ChangeExtension(path, ".cue");
        if (File.Exists(cue)) return FromBinCue(File.ReadAllText(cue), DirOf(path));

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes.Length % 2352 != 0)
            throw new DiscConvertException(
                $"'{Path.GetFileName(path)}' has no sibling .cue and is not a whole number of 2352-byte sectors; " +
                "supply the .cue to describe its tracks.");
        return new DiscModel
        {
            Tracks = new[]
            {
                new DiscModelTrack
                {
                    Number = 1, Type = CueTrackType.Mode1_2352, SectorSize = 2352, Data = bytes,
                },
            },
        };
    }

    // ---- CHD (CD) -----------------------------------------------------------

    private static DiscModel ReadChd(string path)
    {
        ChdExtractor.CdExtraction ex;
        try { ex = ChdExtractor.ExtractCd(File.ReadAllBytes(path)); }
        catch (ChdFormatException e) { throw new DiscConvertException(e.Message); }

        // ExtractCd yields one merged bin plus a cue that references it. Materialise
        // both in a temp dir and reuse the canonical BIN/CUE reader.
        using var tmp = new TempDir("dforge_hub_chd_");
        var binName = FirstCueFile(ex.Cue) ?? "disc.bin";
        File.WriteAllBytes(Path.Combine(tmp.Path, binName), ex.Bin);
        return FromBinCue(ex.Cue, tmp.Path);
    }

    private static void WriteChd(DiscModel model, string outPath)
    {
        using var tmp = new TempDir("dforge_hub_chd_");
        var cueText = ToBinCue(model, tmp.Path, "disc");
        byte[] chd;
        try { chd = ChdWriter.CreateCdFromBinCue(cueText, tmp.Path); }
        catch (ChdFormatException e) { throw new DiscConvertException(e.Message); }
        File.WriteAllBytes(outPath, chd);
    }

    // ---- ISO (single 2048 data track) --------------------------------------

    private static DiscModel ReadIso(string path)
        => IsoModel(File.ReadAllBytes(path), Path.GetFileName(path));

    private static DiscModel IsoModel(byte[] iso, string name)
    {
        if (iso.Length == 0 || iso.Length % 2048 != 0)
            throw new DiscConvertException(
                $"'{name}' is {iso.Length:N0} bytes, not a whole number of 2048-byte sectors — it may be truncated or not a plain ISO.");
        return new DiscModel
        {
            Tracks = new[]
            {
                new DiscModelTrack
                {
                    Number = 1, Type = CueTrackType.Mode1_2048, SectorSize = 2048, Data = iso,
                },
            },
        };
    }

    private static void WriteIso(DiscModel model, string outPath)
    {
        var data = model.Tracks.Where(t => t.Type != CueTrackType.Audio).ToList();
        if (data.Count == 0)
            throw new DiscConvertException("An ISO needs a data track; this model has only audio.");
        var t = data[0];
        if (t.SectorSize != 2048)
            throw new DiscConvertException(
                $"An ISO holds cooked 2048-byte sectors, but track {t.Number} stores {t.SectorSize}-byte sectors. " +
                "Write it as BIN/CUE (or CHD) instead — extracting user data from raw sectors is out of the hub's scope.");
        File.WriteAllBytes(outPath, t.Data);
    }

    private static DiscModel ReadCiso(string path)
    {
        using var input = File.OpenRead(path);
        using var iso = new MemoryStream();
        try { CisoImage.Decompress(input, iso); }
        catch (CisoFormatException e) { throw new DiscConvertException(e.Message); }
        return IsoModel(iso.ToArray(), Path.GetFileName(path));
    }

    private static DiscModel ReadWbfs(string path)
    {
        using var input = File.OpenRead(path);
        WbfsFile wbfs;
        try { wbfs = WbfsReader.Read(input); }
        catch (WbfsFormatException e) { throw new DiscConvertException(e.Message); }
        if (wbfs.Discs.Count == 0)
            throw new DiscConvertException("The WBFS container holds no discs.");
        using var iso = new MemoryStream();
        WbfsReader.ExtractDisc(input, wbfs.Discs[0], iso);
        return IsoModel(iso.ToArray(), Path.GetFileName(path));
    }

    // ---- CDI (read + write, via the CDI <-> BIN/CUE bridge) -----------------

    private static DiscModel ReadCdi(string path)
    {
        using var fs = File.OpenRead(path);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException e) { throw new DiscConvertException(e.Message); }
        using var tmp = new TempDir("dforge_hub_cdi_");
        var r = CdiConverter.CdiToBinCue(fs, image, tmp.Path, "disc");
        return FromBinCue(r.CueText, tmp.Path);
    }

    private static void WriteCdi(DiscModel model, string outPath)
    {
        using var tmp = new TempDir("dforge_hub_cdi_");
        var cueText = ToBinCue(model, tmp.Path, "disc");
        using var os = File.Create(outPath);
        try { CdiConverter.BinCueToCdi(cueText, tmp.Path, DefaultCdiVersion, os); }
        catch (Exception e) when (e is InvalidDataException or FormatException or FileNotFoundException)
        { throw new DiscConvertException(e.Message); }
    }

    // ---- NRG (read + write, bridged through CDI) ----------------------------

    private static DiscModel ReadNrg(string path)
    {
        using var fs = File.OpenRead(path);
        NrgImage image;
        try { image = NrgParser.Parse(fs); }
        catch (NrgFormatException e) { throw new DiscConvertException(e.Message); }

        using var tmp = new TempDir("dforge_hub_nrg_");
        var cdiPath = Path.Combine(tmp.Path, "disc.cdi");
        using (var cdiOut = File.Create(cdiPath))
            NrgConverter.NrgToCdi(fs, image, DefaultCdiVersion, cdiOut);
        using var cdi = File.OpenRead(cdiPath);
        var cdiImage = CdiParser.Parse(cdi);
        var r = CdiConverter.CdiToBinCue(cdi, cdiImage, tmp.Path, "disc");
        return FromBinCue(r.CueText, tmp.Path);
    }

    private static void WriteNrg(DiscModel model, string outPath)
    {
        using var tmp = new TempDir("dforge_hub_nrg_");
        var cueText = ToBinCue(model, tmp.Path, "disc");
        var cdiPath = Path.Combine(tmp.Path, "disc.cdi");
        using (var cdiOut = File.Create(cdiPath))
        {
            try { CdiConverter.BinCueToCdi(cueText, tmp.Path, DefaultCdiVersion, cdiOut); }
            catch (Exception e) when (e is InvalidDataException or FormatException or FileNotFoundException)
            { throw new DiscConvertException(e.Message); }
        }
        using var cdi = File.OpenRead(cdiPath);
        var cdiImage = CdiParser.Parse(cdi);
        using var os = File.Create(outPath);
        NrgConverter.CdiToNrg(cdi, cdiImage, os);
    }

    // ---- MDS/MDF (read, bridged through CDI) --------------------------------

    private static DiscModel ReadMds(string path)
    {
        if (!File.Exists(path))
            throw new DiscConvertException($"MDS control file not found: {path}");
        MdsImage mds;
        try { mds = MdsParser.Parse(File.ReadAllBytes(path)); }
        catch (MdsFormatException e) { throw new DiscConvertException(e.Message); }
        var mdfPath = MdsConverter.DefaultMdfPath(path);

        using var tmp = new TempDir("dforge_hub_mds_");
        var cdiPath = Path.Combine(tmp.Path, "disc.cdi");
        try
        {
            using (var cdiOut = File.Create(cdiPath))
                MdsConverter.MdsToCdi(mds, mdfPath, DefaultCdiVersion, cdiOut);
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException or EndOfStreamException)
        { throw new DiscConvertException(e.Message); }

        using var cdi = File.OpenRead(cdiPath);
        var cdiImage = CdiParser.Parse(cdi);
        var r = CdiConverter.CdiToBinCue(cdi, cdiImage, tmp.Path, "disc");
        return FromBinCue(r.CueText, tmp.Path);
    }

    // ---- GDI (read, bridged through CDI) ------------------------------------

    private static DiscModel ReadGdi(string path)
    {
        using var tmp = new TempDir("dforge_hub_gdi_");
        var cdiPath = Path.Combine(tmp.Path, "disc.cdi");
        try
        {
            using (var cdiOut = File.Create(cdiPath))
                GdiConverter.GdiToCdi(path, DefaultCdiVersion, cdiOut);
        }
        catch (Exception e) when (e is GdiFormatException or FileNotFoundException)
        { throw new DiscConvertException(e.Message); }

        using var cdi = File.OpenRead(cdiPath);
        var cdiImage = CdiParser.Parse(cdi);
        var r = CdiConverter.CdiToBinCue(cdi, cdiImage, tmp.Path, "disc");
        return FromBinCue(r.CueText, tmp.Path);
    }

    // ---- CloneCD (read, straight from the .ccd TOC + .img sidecar) ----------

    private static DiscModel ReadCloneCd(string path)
    {
        CloneCdReader.CcdToc toc;
        try { toc = CloneCdReader.ReadFile(path); }
        catch (CloneCdReader.CcdFormatException e) { throw new DiscConvertException(e.Message); }

        var (imgPath, _) = CloneCdReader.SidecarsFor(path);
        if (!File.Exists(imgPath))
            throw new DiscConvertException($"The CloneCD image sidecar was not found: {imgPath}");

        var outTracks = new List<DiscModelTrack>();
        using var img = File.OpenRead(imgPath);
        foreach (var track in toc.Tracks)
        {
            using var buf = new MemoryStream();
            try { CloneCdReader.ExtractTrack(toc, track, img, buf); }
            catch (CloneCdReader.CcdFormatException e) { throw new DiscConvertException(e.Message); }
            outTracks.Add(new DiscModelTrack
            {
                Number = track.Number,
                Type = track.IsData ? CueTrackType.Mode1_2352 : CueTrackType.Audio,
                SectorSize = 2352,
                Data = buf.ToArray(),
            });
        }
        if (outTracks.Count == 0)
            throw new DiscConvertException("The .ccd TOC declares no tracks.");
        return new DiscModel { Tracks = outTracks };
    }

    // ---- helpers ------------------------------------------------------------

    private static int SectorSizeFor(CueTrackType t) => t switch
    {
        CueTrackType.Mode1_2048 => 2048,
        CueTrackType.Mode2_2336 => 2336,
        _ => 2352,
    };

    private static string DirOf(string path)
        => Path.GetDirectoryName(Path.GetFullPath(path))!;

    private static string? FirstCueFile(string cueText)
    {
        foreach (var raw in cueText.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase)) continue;
            int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
            if (q1 >= 0 && q2 > q1) return line[(q1 + 1)..q2];
        }
        return null;
    }

    /// <summary>A temp directory that recursively deletes itself on Dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string prefix) => Path = Directory.CreateTempSubdirectory(prefix).FullName;
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
