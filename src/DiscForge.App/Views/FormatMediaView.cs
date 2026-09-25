// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>
/// The escape hatch for preparing removable media (SD/SDHC/SDXC, CF, and similar cards) that a
/// flashcart-based dumper — GBxCart RW/FlashGBX, Cart Reader, or a floppy-imaging rig's own storage
/// — reads from or writes to. DiscForge has no code that talks to a card reader/writer at all, and
/// formatting one (the partition table plus filesystem layout a card's own controller and wear-
/// leveling expect) is a different problem from anything DiscForge itself does — reimplementing it
/// here would just be a worse copy of tools the SD Association and card vendors already maintain
/// correctly. Same launch-what-you-point-it-at contract as every other external-tool button in the
/// app: DiscForge never bundles, inspects, or knows anything else about the formatter.
/// </summary>
internal sealed class FormatMediaView : UserControl
{
    private readonly Label _info = new()
    {
        AutoSize = false, Location = new Point(12, 12), Size = new Size(712, 60),
        Font = Theme.Ui, ForeColor = Color.Black,
        Text = "DiscForge has no code that talks to a card reader/writer, and formatting removable " +
               "media correctly (partition table + filesystem the card's own controller expects) is " +
               "its own established problem — not something to reimplement here. This tile launches " +
               "a dedicated formatter for prepping an SD/SDHC/SDXC (or similar) card, e.g. before " +
               "using it with a flashcart-based dumper (Cartridges tile) or a floppy-imaging rig.",
    };
    // The SD Association's official SD Card Formatter (developed by Tuxera) — the standard,
    // vendor-correct tool for SD/SDHC/SDXC. DiscForge never bundles it; this launches whatever the
    // user points it at, same as every other external-tool button in the app.
    private readonly Button _cardFormatter = new()
    {
        Text = "SD Card Formatter…", Location = new Point(12, 80), Width = 150, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 118), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    public FormatMediaView()
    {
        Size = new Size(736, 160);
        BackColor = Color.White;
        Padding = new Padding(12);

        _cardFormatter.Click += (_, _) => LaunchExternalCardFormatter();

        Controls.Add(_info); Controls.Add(_cardFormatter); Controls.Add(_status);

        _status.Text = "Format the card in its own window — DOUBLE-CHECK the drive letter there before " +
                       "formatting; this erases everything on that card. (Picked the wrong file? " +
                       "Shift+Click the button to choose again.)";
    }

    /// <summary>
    /// Launch a user-supplied card formatter (asked for once, then remembered) — DiscForge has no
    /// card reader/writer support of its own. Delegates to <see cref="ExternalToolLauncher"/>, the
    /// same shared logic every other external-tool button in the app uses; this view has no event
    /// log, so it reports through <see cref="_status"/> instead.
    /// </summary>
    private void LaunchExternalCardFormatter() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathCardFormatter,
        p => Settings.ExternalDumperPathCardFormatter = p,
        "Locate SD Card Formatter (or your preferred card formatter)",
        "Double-check the drive letter before formatting — this erases the whole card.",
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
