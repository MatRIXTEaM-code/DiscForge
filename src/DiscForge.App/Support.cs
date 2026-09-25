// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Diagnostics;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// DiscForge is free to use; donations are optional and unlock nothing. This is the one place the
/// donation link lives — set <see cref="DonateUrl"/> to your PayPal.Me (or PayPal donate) link and
/// every "Donate" button in the app starts working. While it's empty those buttons stay hidden, so a
/// build never ships a dead link.
/// </summary>
internal static class Support
{
    /// <summary>Your PayPal link, e.g. "https://paypal.me/YourName". Empty = no donate buttons.</summary>
    public const string DonateUrl = "https://paypal.me/DiscForgeUK";

    public static bool HasDonateLink => DonateUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public const string Blurb =
        "DiscForge is free. If it's useful to you, a donation helps keep it going — it's entirely " +
        "optional and doesn't unlock anything; every feature is already yours.";

    public static void OpenDonate(IWin32Window? owner)
    {
        if (!HasDonateLink) return;
        try { Process.Start(new ProcessStartInfo { FileName = DonateUrl, UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppLog.WriteException("open donate link", ex);
            RetroMessageBox.Show(owner, $"Couldn't open your browser. The donation page is:\r\n\r\n{DonateUrl}",
                "DiscForge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
