// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>
/// The escape hatch for sector-level physical-drive/image cloning (e.g. HDD Raw Copy Tool) — a
/// different domain from every other tile in the app. DiscForge's own sector-level code is built
/// around ECMA-130 CD/DVD/BD structure (2352/2048/2448-byte sectors, EDC/ECC, TOC/sub-channel), not
/// generic block-device I/O; a whole-drive or whole-image byte-for-byte clone (HDD/SSD/USB, or an
/// existing raw image) is a different, already-solved problem this app has no reason to duplicate.
/// Same launch-what-you-point-it-at contract as every other external-tool button in the app —
/// DiscForge never bundles, inspects, or knows anything else about the tool.
/// </summary>
internal sealed class RawCopyView : UserControl
{
    private readonly Label _info = new()
    {
        AutoSize = false, Location = new Point(12, 12), Size = new Size(712, 60),
        Font = Theme.Ui, ForeColor = Color.Black,
        Text = "DiscForge's own sector-level code is built around CD/DVD/BD structure, not generic " +
               "block-device I/O — whole-drive or whole-image byte-for-byte cloning is a different, " +
               "already-solved problem. This tile launches a dedicated raw-copy tool (e.g. HDD Raw " +
               "Copy Tool) for cloning a physical drive to/from an image, or drive-to-drive.",
    };
    // HDD Raw Copy Tool (hddguru.com) — sector-by-sector physical-drive <-> image cloning. DiscForge
    // never bundles, inspects, or knows anything else about it; this launches whatever the user
    // points it at, same as every other external-tool button in the app.
    private readonly Button _hddRawCopy = new()
    {
        Text = "HDD Raw Copy Tool…", Location = new Point(12, 80), Width = 160, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 118), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    public RawCopyView()
    {
        Size = new Size(736, 160);
        BackColor = Color.White;
        Padding = new Padding(12);

        _hddRawCopy.Click += (_, _) => LaunchExternalHddRawCopy();

        Controls.Add(_info); Controls.Add(_hddRawCopy); Controls.Add(_status);

        _status.Text = "Clone in its own window — TRIPLE-CHECK source and target there before starting; " +
                       "a raw copy overwrites the target completely and cannot be undone. (Picked the " +
                       "wrong file? Shift+Click the button to choose again.)";
    }

    /// <summary>
    /// Launch a user-supplied raw-copy tool (asked for once, then remembered) — DiscForge has no
    /// generic block-device cloning of its own. Delegates to <see cref="ExternalToolLauncher"/>,
    /// the same shared logic every other external-tool button in the app uses; this view has no
    /// event log, so it reports through <see cref="_status"/> instead.
    /// </summary>
    private void LaunchExternalHddRawCopy() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathHddRawCopy,
        p => Settings.ExternalDumperPathHddRawCopy = p,
        "Locate HDD Raw Copy Tool (or your preferred raw-copy tool)",
        "Triple-check source and target before starting — a raw copy overwrites the target completely.",
        ReportToStatus);

    /// <summary>Shared report sink for this view's external-tool button: this screen has no event
    /// log the way Read/Burn do, so it reports through the status label instead.</summary>
    private void ReportToStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.ForeColor = isError ? Color.FromArgb(0xA0, 0x20, 0x20) : Color.FromArgb(0x20, 0x70, 0x20);
        if (!isError) StatusBus.Report(message);
    }
}
