// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>
/// The escape hatch for PSP ISO editing. DiscForge already reads a PSP UMD's filesystem and
/// PARAM.SFO without decrypting anything (<c>DiscForge.Core.Psp</c>; <c>psp-info</c>,
/// <c>pbp-info</c>, <c>pbp-extract</c> on the CLI), but has no ISO editor/rebuilder of its own.
/// UMDGen fills that role.
///
/// This screen is honestly a different shape from every other external-tool escape hatch in the
/// app: it is not a ripper. A physical UMD is dumped by homebrew running on the PSP console
/// itself, not by a PC talking to drive hardware over USB the way an optical drive or a
/// flashcart is — so there is no "capture" step for DiscForge to hand off here, only ISO editing
/// once a dump already exists. The button is offered anyway because it's still a real gap in
/// what DiscForge itself can do to a PSP image; it just isn't the acquisition step every other
/// button on every other screen represents.
/// </summary>
internal sealed class PspView : UserControl
{
    private readonly Label _info = new()
    {
        AutoSize = false, Location = new Point(12, 12), Size = new Size(712, 60),
        Font = Theme.Ui, ForeColor = Color.Black,
        Text = "DiscForge already reads a PSP UMD's filesystem and PARAM.SFO (see the CLI's " +
               "psp-info/pbp-info/pbp-extract) without decrypting anything, but has no ISO editor " +
               "of its own. A physical UMD is dumped by homebrew running on the PSP itself, not by " +
               "anything a PC talks to — so unlike every other button in DiscForge, this one is for " +
               "editing an image you already have, not acquiring one.",
    };
    // UMDGen: browse, edit, and rebuild a PSP ISO (including fixing its LBA table after an
    // edit). DiscForge never bundles, inspects, or knows anything else about it — same
    // launch-what-you-point-it-at contract as every external-tool button in the app.
    private readonly Button _umdGen = new()
    {
        Text = "UMDGen…", Location = new Point(12, 80), Width = 110, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 118), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    public PspView()
    {
        Size = new Size(736, 160);
        BackColor = Color.White;
        Padding = new Padding(12);

        _umdGen.Click += (_, _) => LaunchExternalUmdGen();

        Controls.Add(_info); Controls.Add(_umdGen); Controls.Add(_status);

        _status.Text = "Once you have a PSP ISO, use Examine or Extract to inspect it, or launch " +
                       "UMDGen to edit it directly.";
    }

    /// <summary>
    /// Launch a user-supplied UMDGen (asked for once, then remembered) — DiscForge has no PSP ISO
    /// editor of its own. Delegates to <see cref="ExternalToolLauncher"/>, the same shared logic
    /// every other external-tool button in the app uses; this view has no event log, so it
    /// reports through <see cref="_status"/> instead.
    /// </summary>
    private void LaunchExternalUmdGen() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathUmdGen,
        p => Settings.ExternalDumperPathUmdGen = p,
        "Locate UMDGen",
        "Edit or rebuild the ISO there — DiscForge did not modify it.",
        ReportToStatus);

    /// <summary>Shared report sink for this view's external-tool button: this screen has no
    /// event log the way Read/Burn do, so it reports through the status label instead.</summary>
    private void ReportToStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.ForeColor = isError ? Color.FromArgb(0xA0, 0x20, 0x20) : Color.FromArgb(0x20, 0x70, 0x20);
        if (!isError) StatusBus.Report(message);
    }
}
