// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Identify;
using DiscForge.Core.Partition;
using DiscForge.Core.Floppy;
using DiscForge.Core.Rom;
using DiscForge.Core.GameAudio;
using DiscForge.Core.Wbfs;
using DiscForge.Core.CdInteractive;
using DiscForge.Core.Saves;
using DiscForge.Core.Saturn;
using DiscForge.Core.Fat;
using DiscForge.Core.Iso;
using DiscForge.Core.Hfs;
using DiscForge.Core.GameCube;
using DiscForge.Core.Audio;

namespace DiscForge.App.Views;

/// <summary>
/// Drop any file in and DiscForge examines it: first it says what the file is (the
/// universal identifier), then it routes to the matching reader and shows the parsed
/// detail — a cartridge ROM's header and hashes, a disk image's partition table, a
/// floppy or CD-i directory, a memory card's save list, an audio file's tags. One
/// tile over all the readers; each detail provider is guarded, so a file that looks
/// like one thing but isn't simply produces no extra detail rather than an error.
/// (The "Inspect" tile is the dedicated CDI track viewer; this is the everything view.)
/// </summary>
internal sealed class ExamineView : UserControl
{
    // Read the whole file into memory only for the small-format readers (ROMs,
    // floppies, saves, audio). A large disc image is examined via its stream only.
    private const long MaxInMemoryBytes = 128L * 1024 * 1024;

    private readonly Label _result = new()
    {
        AutoSize = false, Location = new Point(12, 60), Size = new Size(712, 28), Font = Theme.UiBold,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono,
        Location = new Point(12, 96), Size = new Size(712, 344),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = Color.White,
    };

    public ExamineView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) Examine(f[0]); };

        Controls.Add(new Label { Text = "File:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Choose file…", Location = new Point(12, 34), Width = 130, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => Choose();

        Controls.Add(open);
        Controls.Add(_result);
        Controls.Add(_log);

        _result.Text = "Choose or drop a file to examine it.";
        _result.ForeColor = Color.Gray;
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Choose()
    {
        using var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (dlg.ShowDialog() == DialogResult.OK) Examine(dlg.FileName);
    }

    private void Examine(string path)
    {
        AppSettings.LastImageDirectory = Path.GetDirectoryName(path);
        _log.Clear();
        try
        {
            using var fs = File.OpenRead(path);

            var id = FormatIdentifier.Identify(fs);
            _result.Text = id.Recognised ? $"{id.Name} — {id.Detail}" : "Unrecognised format";
            _result.ForeColor = id.Recognised ? Color.FromArgb(0x20, 0x70, 0x20) : Color.FromArgb(0xA0, 0x60, 0x00);
            Log($"{Path.GetFileName(path)}  ({fs.Length:N0} bytes)");
            Log($"Identified: {id.Name}" + (id.Detail.Length > 0 ? $" — {id.Detail}" : "") + $"  [{id.Category}]");
            Log("");
            StatusBus.Report($"{Path.GetFileName(path)}: {id.Name}");

            int shown = 0;
            shown += StreamDetail(fs, CdiDetail);
            shown += StreamDetail(fs, PartitionDetail);
            shown += StreamDetail(fs, WbfsDetail);
            shown += StreamDetail(fs, CdInteractiveDetail);
            shown += StreamDetail(fs, AdxDetail);
            shown += StreamDetail(fs, RockRidgeDetail);

            if (fs.Length <= MaxInMemoryBytes)
            {
                byte[] data = File.ReadAllBytes(path);
                shown += ByteDetail(data, RomDetail);
                shown += ByteDetail(data, N64CicDetail);
                shown += ByteDetail(data, FloppyDetail);
                shown += ByteDetail(data, FatDetail);
                shown += ByteDetail(data, HfsResourceDetail);
                shown += ByteDetail(data, TplDetail);
                shown += ByteDetail(data, AudioDetail);
                shown += ByteDetail(data, HdcdDetail);
                shown += ByteDetail(data, SaveDetail);
            }
            shown += SafeDetail(() => SaturnDiscDetail(path));

            if (shown == 0)
                Log("(No extended details for this format — see the identification above.)");
        }
        catch (Exception ex)
        {
            _result.Text = "Could not examine this file.";
            _result.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30);
            Log($"Error: {ex.Message}");
            AppLog.WriteException("examine", ex);
        }
    }

    // ---- detail providers (each returns text, or null when it doesn't apply) ----

    private int StreamDetail(FileStream fs, Func<FileStream, string?> provider)
    {
        try { fs.Position = 0; var s = provider(fs); if (s is not null) { Log(s); Log(""); return 1; } }
        catch { /* not this format */ }
        return 0;
    }

    private int ByteDetail(byte[] data, Func<byte[], string?> provider)
    {
        try { var s = provider(data); if (s is not null) { Log(s); Log(""); return 1; } }
        catch { /* not this format */ }
        return 0;
    }

    private int SafeDetail(Func<string?> provider)
    {
        try { var s = provider(); if (s is not null) { Log(s); Log(""); return 1; } }
        catch { }
        return 0;
    }

    private static string? PartitionDetail(FileStream fs)
    {
        var disk = PartitionTable.Read(fs);
        if (disk.Partitions.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine($"── Partition table ({disk.Scheme}) ──");
        if (disk.DiskGuid is not null) sb.AppendLine($"   Disk GUID: {disk.DiskGuid}");
        foreach (var p in disk.Partitions)
            sb.AppendLine($"   [{p.Index}] {p.TypeName,-22} {Mb(p.SizeBytes),10}  @ {p.StartByte:N0}" +
                          (p.FileSystem.Length > 0 ? $"  fs={p.FileSystem}" : "") + (p.Bootable ? "  *boot" : ""));
        return sb.ToString().TrimEnd();
    }

    private static string? CdiDetail(FileStream fs)
    {
        // Only claim a CDI when the trailer parses — otherwise this isn't one.
        CdiImage img;
        try { img = CdiParser.Parse(fs); }
        catch (CdiFormatException) { return null; }

        var sb = new StringBuilder();
        sb.AppendLine($"── DiscJuggler CDI ({img.Version}) — {img.Sessions.Count} session(s), {img.TrackCount} track(s) ──");
        foreach (var t in img.AllTracks.Take(40))
            sb.AppendLine($"   Track {t.Number,2}  session {t.SessionIndex}  {t.Mode,-6} {(int)t.SectorSize}B/s  " +
                          $"{t.LengthSectors:N0} sectors" +
                          (t.SourceFilename is { Length: > 0 } src ? $"  [{src}]" : ""));
        return sb.ToString().TrimEnd();
    }

    private static string? WbfsDetail(FileStream fs)
    {
        if (!WbfsReader.IsWbfs(fs)) return null;
        fs.Position = 0;
        var w = WbfsReader.Read(fs);
        var sb = new StringBuilder();
        sb.AppendLine($"── WBFS ({w.Discs.Count} disc(s)) ──");
        foreach (var d in w.Discs) sb.AppendLine($"   slot {d.Slot}: {d.GameId}  {d.Title}");
        return sb.ToString().TrimEnd();
    }

    private static string? CdInteractiveDetail(FileStream fs)
    {
        if (!CdInteractiveReader.IsCdInteractive(fs)) return null;
        fs.Position = 0;
        var cdi = CdInteractiveReader.Read(fs);
        var sb = new StringBuilder();
        sb.AppendLine($"── CD-i ({cdi.Kind}) ──");
        sb.AppendLine($"   Volume: {cdi.VolumeId}   System: {cdi.SystemId}" +
                      (cdi.ApplicationId.Length > 0 ? $"   App: {cdi.ApplicationId}" : ""));
        AppendEntries(sb, cdi.Filesystem.Entries.Select(e => (e.Path, e.IsDirectory, (long)e.Size)));
        return sb.ToString().TrimEnd();
    }

    private static string? AdxDetail(FileStream fs)
    {
        if (!AdxReader.IsAdx(fs)) return null;
        fs.Position = 0;
        var a = AdxReader.ReadInfo(fs);
        double secs = a.SampleRate > 0 ? a.TotalSamples / (double)a.SampleRate : 0;
        return $"── ADX audio ──\n   {a.Channels} ch, {a.SampleRate} Hz, {a.TotalSamples:N0} samples ({secs:0.0}s), encoding {a.Encoding}";
    }

    private static string? RomDetail(byte[] data)
    {
        var rid = RomIdentify.Identify(data);
        if (rid.Platform is "Unknown" or "") return null;
        var sb = new StringBuilder();
        sb.AppendLine($"── Cartridge ROM: {rid.Platform} ──");
        if (rid.Title.Length > 0) sb.AppendLine($"   Title:  {rid.Title}");
        if (rid.GameCode.Length > 0) sb.AppendLine($"   Code:   {rid.GameCode}");
        if (rid.Region.Length > 0) sb.AppendLine($"   Region: {rid.Region}");
        foreach (var kv in rid.Extra) sb.AppendLine($"   {kv.Key}: {kv.Value}");
        try
        {
            var h = RomHashes.Compute(data, rid);
            sb.AppendLine($"   CRC32: {h.Crc32:X8}   MD5: {h.Md5}");
            sb.AppendLine($"   SHA1:  {h.Sha1}");
        }
        catch { }
        foreach (var w in rid.Warnings) sb.AppendLine($"   ! {w}");
        return sb.ToString().TrimEnd();
    }

    private static string? FloppyDetail(byte[] data)
    {
        if (D64Reader.IsD64(data))
        {
            var d = D64Reader.Read(data);
            var sb = new StringBuilder();
            sb.AppendLine($"── C64 D64: \"{d.DiskName}\" (id {d.DiskId}, {d.Tracks} tracks) ──");
            foreach (var e in d.Files.Take(80))
                sb.AppendLine($"   {e.Name,-18} {e.Type,-4} {e.SizeBlocks,4} blk");
            return sb.ToString().TrimEnd();
        }
        if (Fat12Reader.IsFat12(data))
        {
            var d = Fat12Reader.Read(data);
            var sb = new StringBuilder();
            sb.AppendLine($"── DOS FAT12: \"{d.VolumeLabel}\" ──");
            AppendEntries(sb, d.Entries.Select(e => (e.Path, e.IsDirectory, (long)e.Size)));
            return sb.ToString().TrimEnd();
        }
        if (AdfReader.IsAdf(data))
        {
            var d = AdfReader.Read(data);
            var sb = new StringBuilder();
            sb.AppendLine($"── Amiga ADF: \"{d.DiskName}\" ({(d.Ffs ? "FFS" : "OFS")}) ──");
            AppendEntries(sb, d.Entries.Select(e => (e.Path, e.IsDirectory, e.Size)));
            return sb.ToString().TrimEnd();
        }
        return null;
    }

    private static string? AudioDetail(byte[] data)
    {
        if (PsfReader.IsPsf(data))
        {
            var p = PsfReader.Read(data);
            var sb = new StringBuilder();
            sb.AppendLine($"── {p.SystemName} sound (PSF v0x{p.PsfVersion:X2}) ──");
            foreach (var kv in p.Tags) sb.AppendLine($"   {kv.Key}: {kv.Value}");
            return sb.ToString().TrimEnd();
        }
        if (SpcReader.IsSpc(data))
        {
            var s = SpcReader.Read(data);
            return $"── SNES SPC ──\n   Song: {s.SongTitle}\n   Game: {s.GameTitle}\n   Artist: {s.Artist}\n   Dumped: {s.DumpDate}";
        }
        if (NsfReader.IsNsf(data))
        {
            var n = NsfReader.Read(data);
            return $"── NES NSF ──\n   {n.SongName} — {n.Artist}\n   {n.Copyright}\n   {n.TotalSongs} song(s), {(n.IsPal ? "PAL" : "NTSC")}" +
                   (n.ExpansionChips.Count > 0 ? $", chips: {string.Join(", ", n.ExpansionChips)}" : "");
        }
        if (VgmReader.IsVgm(data))
        {
            var v = VgmReader.Read(data);
            double secs = v.TotalSamples / 44100.0;
            return $"── VGM (v{v.Version}) ──\n   {v.Tags.GameName} / {v.Tags.TrackName} — {v.Tags.Author}\n" +
                   $"   {secs:0.0}s, chips: {string.Join(", ", v.Chips)}";
        }
        return null;
    }

    private static string? SaveDetail(byte[] data)
    {
        if (N64ControllerPak.IsControllerPak(data))
        {
            var pak = N64ControllerPak.Read(data);
            var sb = new StringBuilder();
            sb.AppendLine($"── N64 Controller Pak ({pak.Notes.Count} note(s)) ──");
            foreach (var n in pak.Notes) sb.AppendLine($"   {n.GameCode}  {n.Name}");
            return sb.ToString().TrimEnd();
        }
        if (SaturnSaveReader.IsSaturnBackup(data))
        {
            var b = SaturnSaveReader.Read(data);
            var sb = new StringBuilder();
            sb.AppendLine($"── Saturn backup ({b.Saves.Count} save(s)) ──");
            foreach (var s in b.Saves) sb.AppendLine($"   {s.Name,-12} {s.Comment,-12} {s.DataSize:N0} B");
            return sb.ToString().TrimEnd();
        }
        return null;
    }

    private static string? SaturnDiscDetail(string path)
    {
        var h = SaturnDisc.Identify(path);
        if (h is null) return null;
        return $"── Sega Saturn disc ──\n   {h.Title}\n   {h.ProductNumber} {h.Version}   " +
               $"regions: {(h.Regions.Count > 0 ? string.Join(", ", h.Regions) : "?")}   released: {h.ReleaseDate}";
    }

    // ---- new-in-1.12 readers ----

    private static string? N64CicDetail(byte[] data)
    {
        if (N64Cic.DetectOrder(data) is null) return null;
        var info = N64Cic.Analyze(data);
        var sb = new StringBuilder();
        sb.AppendLine($"── N64 boot security (.{info.ByteOrder}) ──");
        sb.AppendLine($"   CIC: {info.Cic ?? "unrecognised"}" + (info.CicRegion is { } r ? $"  ({r})" : ""));
        sb.AppendLine($"   Bootcode CRC-32: {info.BootcodeCrc32:X8}");
        sb.AppendLine("   Boot checksum: " + info.CrcValid switch
        {
            true => "OK — CRC1/CRC2 match; image intact.",
            false => "MISMATCH — modified or bad dump.",
            null => "not checked (unknown CIC or ROM < 1 MiB).",
        });
        return sb.ToString().TrimEnd();
    }

    private static string? FatDetail(byte[] data)
    {
        if (!FatReader.IsFat(data)) return null;
        var vol = FatReader.Read(data);
        if (vol.Type == FatType.Fat12) return null;   // FAT12 floppies are covered by FloppyDetail
        var sb = new StringBuilder();
        sb.AppendLine($"── {vol.Type}: \"{vol.VolumeLabel}\" ──");
        AppendEntries(sb, vol.Entries.Select(e => (e.Path, e.IsDirectory, e.Size)));
        return sb.ToString().TrimEnd();
    }

    private static string? RockRidgeDetail(FileStream fs)
    {
        DiscForge.Core.Iso.IsoDirectory listing;
        try { listing = IsoReader.Read(fs, IsoReader.NamePreference.Iso9660); }
        catch (IsoFormatException) { return null; }
        if (!listing.RockRidge) return null;

        var rows = listing.Entries.Where(e => e.RockRidge is { Present: true }).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"── Rock Ridge (POSIX) — volume \"{listing.VolumeId}\", {rows.Count} entr(y/ies) ──");
        int n = 0;
        foreach (var e in rows)
        {
            if (n++ >= 60) { sb.AppendLine("   … (more)"); break; }
            var rr = e.RockRidge!;
            string mode = rr.ModeString.Length > 0 ? rr.ModeString : (e.IsDirectory ? "d?????????" : "-?????????");
            string owner = rr.Uid is { } u && rr.Gid is { } g ? $"{u}:{g}" : "";
            string link = rr.SymlinkTarget is { } t ? $" -> {t}" : "";
            sb.AppendLine($"   {mode}  {owner,-9} {e.Path}{link}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string? HfsResourceDetail(byte[] data)
    {
        if (!HfsReader.IsHfs(data)) return null;
        var vol = HfsReader.Read(data);
        var withForks = vol.Files.Where(f => f.ResourceSize > 0).ToList();
        if (withForks.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine($"── HFS resource forks — {withForks.Count} file(s) carry one ──");
        int n = 0;
        foreach (var f in withForks)
        {
            if (n++ >= 40) { sb.AppendLine("   … (more)"); break; }
            string types = "";
            try
            {
                var fork = HfsReader.ReadResourceFork(data, vol, f);
                if (HfsResourceFork.Looks(fork))
                    types = "  {" + string.Join(" ", HfsResourceFork.Parse(fork).Types.Take(8)) + "}";
            }
            catch { /* fragmented / unreadable fork — just show the size */ }
            sb.AppendLine($"   {f.ResourceSize,8:N0} B  {f.Path}{types}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string? TplDetail(byte[] data)
    {
        if (!Tpl.IsTpl(data)) return null;
        var tex = Tpl.Read(data);
        var sb = new StringBuilder();
        sb.AppendLine($"── GameCube/Wii TPL — {tex.Count} texture(s) ──");
        foreach (var t in tex.Take(60))
            sb.AppendLine($"   #{t.Index,-3} {t.Width,4}×{t.Height,-4} {t.FormatName}");
        sb.AppendLine("   (use the Textures tile to decode them to PNG)");
        return sb.ToString().TrimEnd();
    }

    private static string? HdcdDetail(byte[] data)
    {
        // Only a WAV carries the header we need; a raw PCM track goes through the CLI/Textures path.
        if (data.Length < 12 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return null;
        var info = WavReader.Read(new MemoryStream(data));
        if (info.BitsPerSample != 16) return null;
        int n = (int)(info.DataLength / 2);
        var pcm = new short[n];
        int off = (int)info.DataOffset;
        for (int i = 0; i < n; i++) pcm[i] = (short)(data[off + i * 2] | (data[off + i * 2 + 1] << 8));
        var r = Hdcd.Scan(pcm, info.Channels);
        return $"── HDCD scan ──\n   Type-B packets: {r.PacketsTypeB}   Type-A: {r.PacketsTypeA} (noise floor ≈ {r.TypeANoiseFloor:F0})\n" +
               $"   {(r.Detected ? "HDCD DETECTED — this audio is HDCD-encoded." : "No HDCD encoding detected.")}";
    }

    // ---- helpers ----

    private static void AppendEntries(StringBuilder sb, IEnumerable<(string Path, bool IsDir, long Size)> entries)
    {
        int n = 0;
        foreach (var e in entries)
        {
            if (n++ >= 100) { sb.AppendLine("   … (more)"); break; }
            sb.AppendLine(e.IsDir ? $"   {e.Path}/" : $"   {e.Path}   {e.Size:N0} B");
        }
    }

    private static string Mb(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
