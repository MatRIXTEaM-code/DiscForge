// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Burning;
using DiscForge.Core.Devices;
using DiscForge.Devices;

namespace DiscForge.App.Views;

/// <summary>
/// "Just burn the thing" — a one-screen answer to the friction the full
/// <see cref="BurnView"/> deliberately trades for power: queue management,
/// several destinations at once, method radios, test/copies controls. AnyBurn
/// and ImgBurn's simple mode both offer this shape (pick a file, pick a
/// drive, go), and DiscForge had no equivalent for a one-off burn.
///
/// Every decision below is fixed rather than exposed: Write and Verify are
/// both on (verify-after-burn is the one safety default a beginner flow must
/// not skip), Method is Auto (the planner picks DAO/SAO vs RAW the same way
/// BurnView's own "Auto" does), Copies is 1, Test is off. There is exactly one
/// destination — the drive picked in the dropdown — because "burn several
/// drives at once" and "build a queue of several images" are exactly the
/// power-user features this view exists to hide.
///
/// This is a thin front end, not a second burn engine: planning goes through
/// <see cref="BurnJobPlanner"/> via <see cref="BurnExecutor.PlanImage"/>, and
/// the actual write/verify runs through the same <see cref="BurnExecutor"/>
/// BurnView uses — a burn that works from one screen must also still work
/// from the other, because it's the same code underneath.
/// </summary>
internal sealed class QuickBurnView : UserControl
{
    private readonly TextBox _path = new()
    {
        Width = 380, ReadOnly = true, Font = Theme.Ui, Location = new Point(96, 16),
    };
    private readonly Button _choose = new()
    {
        Text = "Choose image…", Location = new Point(484, 14), Width = 116, Height = 26,
        FlatStyle = FlatStyle.System,
    };

    private readonly ComboBox _drive = new()
    {
        Location = new Point(96, 56), Width = 504, Font = Theme.Ui,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly Button _rescan = new()
    {
        Text = "Rescan", Location = new Point(96, 88), Width = 90, Height = 24,
        FlatStyle = FlatStyle.System,
    };

    private readonly Button _burn = new()
    {
        Text = "Burn", Location = new Point(12, 128), Width = 588, Height = 40,
        FlatStyle = FlatStyle.System, Enabled = false, Font = Theme.UiBold,
    };

    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 178), Size = new Size(588, 22), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 210), Size = new Size(588, 220),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private readonly BurnExecutor _executor;

    /// <summary>A combo entry with a readable label — DriveCapabilities itself has
    /// no ToString suited to a dropdown.</summary>
    private sealed record DriveItem(DriveCapabilities Caps)
    {
        public override string ToString() => $"{Caps.Vendor} {Caps.Model} ({Caps.DevicePath})";
    }

    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private string? _imagePath;
    private bool _sourceIsIso;
    private bool _sourceIsCue;

    public QuickBurnView()
    {
        Size = new Size(612, 440);
        BackColor = Color.White;
        Padding = new Padding(12);
        _executor = new BurnExecutor(_log, _progress);   // no speed picker: always the drive's default

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 20), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 60), Font = Theme.Ui });

        _choose.Click += (_, _) => ChooseImage();
        _rescan.Click += async (_, _) => await DetectAsync();
        _drive.SelectedIndexChanged += (_, _) => UpdateBurnEnabled();
        _burn.Click += async (_, _) => await BurnAsync();

        Controls.Add(_path); Controls.Add(_choose);
        Controls.Add(_drive); Controls.Add(_rescan);
        Controls.Add(_burn);
        Controls.Add(_progress);
        Controls.Add(_log);

        _log.Add("Choose an image, pick a drive, and press Burn. " +
                 "Write + Verify run automatically — this is the simple flow; " +
                 "use Record Disc for queues, multiple drives, or manual method/speed control.");

        _ = DetectAsync();
    }

    private void ChooseImage()
    {
        using var dlg = new OpenFileDialog { Filter = BurnExecutor.ImageFileDialogFilter };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var ext = Path.GetExtension(dlg.FileName);
        if (ext.Equals(".ccd", StringComparison.OrdinalIgnoreCase))
        {
            // CloneCD needs the CCD->CUE staging conversion BurnView's OpenCdi does before a
            // burn engine can touch it — a step this quick flow deliberately doesn't carry, so
            // it sends the user to the view that does rather than silently failing later.
            _log.Add("CloneCD (.ccd) images need converting to BIN/CUE first — use Record Disc, " +
                     "which does that step automatically.", EventLogView.Level.Warn);
            return;
        }

        _imagePath = dlg.FileName;
        _sourceIsIso = ext.Equals(".iso", StringComparison.OrdinalIgnoreCase);
        _sourceIsCue = ext.Equals(".cue", StringComparison.OrdinalIgnoreCase);
        _path.Text = dlg.FileName;

        var size = new FileInfo(dlg.FileName).Length;
        _log.Add($"Image: {Path.GetFileName(dlg.FileName)} " +
                 $"({size / (1024.0 * 1024.0):N1} MB, {(_sourceIsIso ? "ISO" : _sourceIsCue ? "CUE" : "CDI")})");
        UpdateBurnEnabled();
    }

    private async Task DetectAsync()
    {
        _rescan.Enabled = false;
        _log.Add("Detecting drives…");
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());

            var keep = (_drive.SelectedItem as DriveItem)?.Caps.DevicePath;
            _drive.Items.Clear();
            foreach (var d in _detected) _drive.Items.Add(new DriveItem(d));

            if (_detected.Count == 0)
            {
                _log.Add("No optical drive detected. Connect a recorder and press Rescan " +
                         "(raw access usually needs administrator).", EventLogView.Level.Warn);
            }
            else
            {
                _log.Add($"{_detected.Count} drive(s) detected.", EventLogView.Level.Good);
                int restore = keep is null ? -1
                    : _detected.ToList().FindIndex(d => d.DevicePath == keep);
                _drive.SelectedIndex = restore >= 0 ? restore : 0;   // pre-select the first one found
            }
        }
        catch (Exception ex)
        {
            _log.Add("Drive detection failed: " + ex.Message, EventLogView.Level.Error);
        }
        finally
        {
            _rescan.Enabled = true;
            UpdateBurnEnabled();
        }
    }

    private void UpdateBurnEnabled() =>
        _burn.Enabled = _imagePath is not null && _drive.SelectedItem is DriveItem;

    private async Task BurnAsync()
    {
        if (_imagePath is null || _drive.SelectedItem is not DriveItem { Caps: var drive }) return;

        _log.Clear();
        _progress.Value = 0;

        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[] { new BurnDestination.Drive(drive) },
            Test = false,
            Write = true,
            Verify = true,
            Copies = 1,
            Method = BurnMethodChoice.Auto,
        };

        var plan = BurnExecutor.PlanImage(_imagePath, _sourceIsIso, _sourceIsCue, job, _log);
        if (plan is null) return;
        if (!BurnExecutor.LogPlanAndConfirm(plan, _log)) return;

        _burn.Enabled = false;
        try { await _executor.RunAllAsync(plan, _imagePath, _sourceIsIso, _sourceIsCue); }
        finally { UpdateBurnEnabled(); }
    }
}
