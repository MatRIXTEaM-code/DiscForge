// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// Shared logic behind every "launch an external tool" button across DiscForge — Read's rippers
/// (RawDump2, CloneCD, Xreveal, CloneBD, IsoBuster, DVDFab, and a generic "Other tool…" slot for
/// whatever isn't already covered by a named button), Burn's burners (ImgBurn,
/// Alcohol 120%, DAEMON Tools), and the format-specific screens (Xbox's Xbox Backup Creator/
/// abgx360, the memory card screen's MemcardRex), plus redumper/MPF on Read, EAC/CUERipper on Rip
/// Audio, QPxTool/Opti Drive Control on the quality scan, and ScummVM on the ScummVM screen. Originally lived only in ReadView as a private
/// method; pulled out here once BurnView needed the identical behaviour, rather than
/// copy-pasting it a second time. Ask for the tool's path once (via the <paramref
/// name="getPath"/>/<paramref name="setPath"/> accessors the caller supplies — each button
/// remembers its own path independently), launch it with its own folder as its working
/// directory, and clear a bad remembered path on failure so the next click re-prompts instead of
/// repeating the same failure forever.
///
/// The <c>WorkingDirectory</c> line matters more than it looks: a real crash report (see the
/// v1.90.2 CHANGELOG entry) showed a launched tool's own startup code opening a file by its bare
/// filename, assuming it runs from its own folder — without an explicit WorkingDirectory it
/// instead inherited DiscForge's, and crashed before its window finished loading.
///
/// DiscForge never bundles, inspects, or knows anything else about what these tools do — this
/// starts the process the user points it at and nothing more.
///
/// Hold Shift while clicking to force the picker to reappear even when a path is already
/// remembered. Added after a real report: a user picked a Windows-Installer icon-cache stub
/// (a file named like <c>NewShortcutNN_&lt;guid&gt;.exe</c> under <c>C:\Windows\Installer\...</c>
/// — MSI's temporary copy of an app's icon resource, not the app itself) instead of the real
/// tool. <see cref="System.Diagnostics.Process.Start"/> launches that stub without error — it's a
/// real, runnable exe — so nothing in this method's own error handling ever fires, and the
/// remembered (wrong) path then silently "succeeds" on every click forever with no window ever
/// appearing. There is no way to tell a merely-quiet tool from a launched-nothing stub from here,
/// so the fix is letting the user force a fresh prompt rather than trying to detect the bad file.
///
/// Reporting is a plain <c>(message, isError)</c> delegate rather than a concrete
/// <see cref="EventLogView"/> — Read and Burn both have a numbered event log to report into, but
/// the Xbox and memory-card screens don't carry one and report through a single status label
/// instead. Making the caller decide how to show the message let this same method serve both
/// shapes of screen without forcing an event log onto views that were never designed to have one.
/// </summary>
internal static class ExternalToolLauncher
{
    public static void Launch(
        Func<string?> getPath, Action<string?> setPath, string pickerTitle,
        string followUpMessage, Action<string, bool> report) =>
        Launch(getPath, setPath, pickerTitle, followUpMessage, report, startInfo: null);

    /// <summary>
    /// Same as the plain overload, but lets the caller shape how the picked tool is started — for
    /// the few tools where "just run the exe" is the wrong thing: a console dumper like redumper
    /// (started bare, its window would vanish the moment it finished, taking its output with it —
    /// so it's opened inside a console that stays open) or ScummVM (started pointed at the game
    /// folder the screen already identified). <paramref name="startInfo"/> receives the tool's
    /// full path and returns the <see cref="System.Diagnostics.ProcessStartInfo"/> to run; when null
    /// the tool is started directly, exactly as before. The working directory is still forced to
    /// the tool's own folder unless the caller set one, for the reason given on this class.
    /// </summary>
    public static void Launch(
        Func<string?> getPath, Action<string?> setPath, string pickerTitle,
        string followUpMessage, Action<string, bool> report,
        Func<string, System.Diagnostics.ProcessStartInfo>? startInfo)
    {
        string? path = getPath();
        bool forceRePrompt = Control.ModifierKeys.HasFlag(Keys.Shift);
        if (forceRePrompt || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            using var pick = new OpenFileDialog
            {
                Title = forceRePrompt ? $"{pickerTitle} (Shift+Click: pick a different file)" : pickerTitle,
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            };
            if (pick.ShowDialog() != DialogResult.OK) return;
            path = Path.GetFullPath(pick.FileName);
            setPath(path);
        }

        try
        {
            var psi = startInfo?.Invoke(path)
                      ?? new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            if (string.IsNullOrEmpty(psi.WorkingDirectory))
                psi.WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            System.Diagnostics.Process.Start(psi);
            report($"Launched {Path.GetFileName(path)}. {followUpMessage}", false);
        }
        catch (Exception ex)
        {
            report($"Could not launch '{path}': {ex.Message}", true);
            // Whatever went wrong, the remembered path clearly isn't usable — clear it so the
            // next click re-prompts instead of repeating the same failure forever.
            setPath(null);
            AppLog.WriteException("launch external tool", ex);
        }
    }

    /// <summary>Adapter for callers that already have an <see cref="EventLogView"/> (Read, Burn) —
    /// keeps their call sites reading exactly as before this method's reporting was generalised.</summary>
    public static void Launch(
        Func<string?> getPath, Action<string?> setPath, string pickerTitle,
        string followUpMessage, EventLogView log,
        Func<string, System.Diagnostics.ProcessStartInfo>? startInfo = null) =>
        Launch(getPath, setPath, pickerTitle, followUpMessage,
            (msg, isError) => log.Add(msg, isError ? EventLogView.Level.Error : EventLogView.Level.Info),
            startInfo);

    /// <summary>
    /// Start a console tool inside a <c>cmd.exe /k</c> window opened in the tool's own folder, running
    /// the tool once with <paramref name="firstArgs"/> (typically <c>--help</c>) so its usage is on
    /// screen and the prompt stays open for the real command. Used for redumper, which is driven
    /// entirely from the command line.
    /// </summary>
    public static System.Diagnostics.ProcessStartInfo ConsoleWindow(string toolPath, string firstArgs)
    {
        string dir = Path.GetDirectoryName(toolPath) ?? string.Empty;
        return new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            // Outer quotes are cmd /k's own: it strips exactly one pair, leaving the quoted path intact.
            Arguments = $"/k \"\"{toolPath}\" {firstArgs}\"",
            WorkingDirectory = dir,
            UseShellExecute = true,
        };
    }
}
