// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.App;

/// <summary>
/// Tiny decoupling bus so views can report status to the shell's status bar
/// without holding a reference to MainForm. MainForm subscribes; views report.
/// </summary>
internal static class StatusBus
{
    public static event Action<string>? Changed;

    public static void Report(string message) => Changed?.Invoke(message);
}
