// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Files;
using DiscForge.Core.Iso;
using DiscForge.Core.Raw;
using DiscForge.Core.Udf;

namespace DiscForge.App.Views;

/// <summary>
/// Copy-protection scan: point it at an image and it reports any recognised
/// scheme fingerprint (LibCrypt, SafeDisc, SecuROM, weak sectors) with guidance
/// on preserving it faithfully. The same <see cref="ProtectionScanner"/> the
/// CLI uses; this is just buttons and an output well. Detection only — DiscForge
/// never circumvents protection.
///
/// Two kinds of evidence go into a verdict, and both matter:
///  - Sector-level traits: deliberate bad EDC, sub-channel irregularities,
///    LibCrypt fingerprints. These are physical and hard to fake.
///  - Marker files: SafeDisc's 00000001.TMP, SecuROM's CMS*.DLL, LaserLock's
///    directory, CD-Cops' CDCOPS.DLL. Weaker on their own — leftovers turn up on
///    unprotected discs — but decisive when a sector trait corroborates them.
/// The file list is therefore walked before scanning, so filename-based
/// detectors have something to work with rather than silently finding nothing.
/// </summary>
internal sealed class ProtectionView : UserControl
{
    private readonly TextBox _path = new() { ReadOnly = true, Width = 440, Font = Theme.Ui };
    private readonly Button _browse = new() { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _scan = new() { Text = "Scan", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _out = new()
    {
        Multiline = true, ReadOnly = true, Font = Theme.Mono, ScrollBars = ScrollBars.Vertical,
        Size = new Size(660, 300),
    };

    public ProtectionView()
    {
        Size = new Size(720, 460);
        BackColor = Color.White;

        var title = new Label
        {
            Text = "Copy-protection scan", Font = Theme.UiBold, AutoSize = true,
            Location = new Point(16, 12),
        };
        var hint = new Label
        {
            Text = "Detects known scheme fingerprints so a backup can preserve them. " +
                   "DiscForge does not circumvent protection.",
            Font = Theme.Ui, AutoSize = false, Size = new Size(680, 32), Location = new Point(16, 34),
        };

        _path.Location = new Point(16, 72);
        _browse.Location = new Point(464, 70);
        _scan.Location = new Point(552, 70);
        _out.Location = new Point(16, 108);

        _browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Disc images (*.cdi;*.iso;*.bin;*.img)|*.cdi;*.iso;*.bin;*.img|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _path.Text = dlg.FileName;
                _scan.Enabled = true;
            }
        };

        _scan.Click += async (_, _) => await ScanAsync();

        Controls.Add(title);
        Controls.Add(hint);
        Controls.Add(_path);
        Controls.Add(_browse);
        Controls.Add(_scan);
        Controls.Add(_out);
    }

    /// <summary>
    /// Every path in the image's filesystem, for the filename-based detectors.
    ///
    /// A disc may carry ISO 9660, UDF, or both; either is enough. Failure here is
    /// never fatal — the sector-level scan is the stronger evidence anyway — but
    /// it IS reported, because "no marker files found" and "no file list was
    /// available" are very different statements and only one of them is a result.
    /// </summary>
    private static (List<string> Files, string Note) ListFiles(string path)
    {
        var names = new List<string>();
        string ext = Path.GetExtension(path);

        try
        {
            if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(path);
                return WalkFilesystem(fs, names);
            }

            if (ext.Equals(".cdi", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(path);
                var image = CdiParser.Parse(fs);
                var track = image.AllTracks.FirstOrDefault(t => t.Mode != CdiTrackMode.Audio);
                if (track is null)
                    return (names, "The image has no data track, so it has no filesystem to list " +
                                   "(audio discs are scanned by sub-channel and sector traits only).");

                using var view = new CdiUserDataStream(fs, track);
                return WalkFilesystem(view, names);
            }

            // A raw .bin/.img is 2352-byte sectors with no container telling us
            // where the user data starts. Cooking that view is the imager's job,
            // not the scanner's — convert it to CDI first and the file list works.
            return (names,
                $"A raw {ext} image has no track table, so the filesystem cannot be located " +
                "reliably. Sector-level detection still runs. Convert to CDI for the " +
                "filename-based checks as well.");
        }
        catch (IsoFormatException ex)
        {
            return (names, $"The ISO 9660 filesystem did not parse ({ex.Message}). " +
                           "Sector-level detection still runs.");
        }
        catch (UdfFormatException ex)
        {
            return (names, $"The UDF filesystem did not parse ({ex.Message}). " +
                           "Sector-level detection still runs.");
        }
        catch (Exception ex)
        {
            return (names, $"Could not list files ({ex.Message}). Sector-level detection still runs.");
        }
    }

    /// <summary>Try ISO 9660 first, then UDF. Whichever answers, take its paths.</summary>
    private static (List<string> Files, string Note) WalkFilesystem(Stream view, List<string> names)
    {
        try
        {
            var dir = IsoReader.Read(view, IsoReader.NamePreference.Auto);
            foreach (var e in dir.Entries)
                if (!e.IsDirectory)
                    names.Add(e.Path);

            string kind = dir.Joliet ? "ISO 9660 + Joliet"
                        : dir.RockRidge ? "ISO 9660 + Rock Ridge"
                        : "ISO 9660";
            return (names, $"{kind}: {names.Count:N0} file(s) listed.");
        }
        catch (IsoFormatException)
        {
            // Not ISO 9660 — a UDF-only disc is entirely normal, especially on DVD.
        }

        view.Position = 0;
        if (!UdfReader.IsUdf(view))
            return (names, "The image has neither an ISO 9660 nor a UDF filesystem, so " +
                           "filename-based checks could not run. Sector-level detection still runs.");

        var vol = UdfReader.Read(view);
        foreach (var e in vol.Entries)
            if (!e.IsDirectory)
                names.Add(e.Path);
        return (names, $"UDF: {names.Count:N0} file(s) listed.");
    }

    private async Task ScanAsync()
    {
        _scan.Enabled = false;
        _out.Text = "Scanning…";
        StatusBus.Report("Scanning for protection fingerprints…");
        string path = _path.Text;

        try
        {
            string report = await Task.Run(() =>
            {
                // Walk the filesystem first: the filename-based detectors are
                // useless without it, and this is where they used to come up
                // empty regardless of what was actually on the disc.
                var (fileNames, fsNote) = ListFiles(path);
                AppLog.Write($"  protection scan '{Path.GetFileName(path)}': {fsNote}");

                using var access = SectorAccess.Open(path);
                var r = ProtectionScanner.Scan(access, fileNames);

                var sb = new StringBuilder();
                sb.AppendLine($"Filesystem: {fsNote}");
                sb.AppendLine();

                if (!r.AnyProtection)
                {
                    sb.AppendLine("No known copy-protection fingerprint detected.");
                    sb.AppendLine();
                    sb.AppendLine("(A clean scan is not a guarantee — it means none of the");
                    sb.AppendLine(" recognised schemes left a detectable signature.)");
                    if (fileNames.Count == 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("Note: no file list was available, so schemes identified by");
                        sb.AppendLine("their marker files could not be checked at all. Only the");
                        sb.AppendLine("sector and sub-channel traits were examined.");
                    }
                    return sb.ToString();
                }

                sb.AppendLine("Copy-protection fingerprint(s) detected:");
                sb.AppendLine();
                foreach (var f in r.Findings)
                {
                    sb.AppendLine($"  {f.Scheme}");
                    sb.AppendLine($"    Evidence: {f.Evidence}");
                    sb.AppendLine($"    Guidance: {f.Guidance}");
                    if (f.SignificantLbas.Count > 0)
                    {
                        var preview = string.Join(", ", f.SignificantLbas.Take(8));
                        sb.AppendLine($"    Sectors:  {preview}" +
                            (f.SignificantLbas.Count > 8 ? $"  (+{f.SignificantLbas.Count - 8} more)" : ""));
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("DiscForge detects protection so a backup can PRESERVE it faithfully.");
                sb.AppendLine("It does not circumvent, strip, or defeat any protection.");
                return sb.ToString();
            });

            _out.Text = report;
            StatusBus.Report("Protection scan complete.");
        }
        catch (Exception ex)
        {
            _out.Text = "Scan failed: " + ex.Message;
            AppLog.WriteException("protection scan", ex);
            StatusBus.Report("Protection scan failed.");
        }
        finally
        {
            _scan.Enabled = true;
        }
    }
}