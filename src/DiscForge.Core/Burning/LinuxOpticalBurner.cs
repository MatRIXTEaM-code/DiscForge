// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Diagnostics;

namespace DiscForge.Core.Burning;

/// <summary>
/// The Linux optical-burn backend. Linux has no single burn API; the established command-line writers are
/// <c>growisofs</c> (dvd+rw-tools — DVD and Blu-ray) and <c>wodim</c>/<c>cdrecord</c> (CD). This drives them
/// so <c>dforge burn</c> works on Linux too, completing the burn story alongside Windows (IMAPI2) and macOS
/// (hdiutil). Which writer fits depends on the media: CD-sized images go to wodim, larger to growisofs; the
/// run method picks the one that is installed and appropriate. The argument building is pure so it can be
/// verified without a drive; the run method shells out. The device defaults to <c>/dev/sr0</c>.
/// </summary>
public static class LinuxOpticalBurner
{
    public const string DefaultDevice = "/dev/sr0";
    private const long CdCapacityBytes = 900L * 1024 * 1024;   // ~900 MB: above this, treat as DVD/BD

    /// <summary>Build the <c>growisofs</c> argument list (DVD/Blu-ray): <c>-Z dev=image</c>, dvd-compat close.</summary>
    public static IReadOnlyList<string> BuildGrowisofsArgs(string device, string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(device);
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        return new[] { "-dvd-compat", "-Z", $"{device}={imagePath}" };
    }

    /// <summary>Build the <c>wodim</c> (cdrecord-compatible) argument list for a CD data burn.</summary>
    public static IReadOnlyList<string> BuildWodimArgs(string device, string imagePath, bool eject = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(device);
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        var args = new List<string> { "-v", $"dev={device}" };
        if (eject) args.Add("-eject");
        args.Add("-data");
        args.Add(imagePath);
        return args;
    }

    /// <summary>Which writer suits an image of this size: growisofs for DVD/BD-sized, wodim for CD-sized.</summary>
    public static string PreferredTool(long imageBytes) => imageBytes > CdCapacityBytes ? "growisofs" : "wodim";

    /// <summary>
    /// Burn an image to the drive. Chooses growisofs or wodim by image size and availability, and returns the
    /// process exit code. Throws if neither tool is installed (with a clear install hint) or off Linux.
    /// </summary>
    public static int RunBurn(string device, string imagePath, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux burn backend is Linux-only.");
        if (!File.Exists(imagePath)) throw new FileNotFoundException($"'{imagePath}' not found.");

        long size = new FileInfo(imagePath).Length;
        string first = PreferredTool(size);
        string? firstPath = Which(first);
        string other = first == "growisofs" ? "wodim" : "growisofs";
        string? otherPath = Which(other);

        var (tool, path) = firstPath is not null ? (first, firstPath)
                         : otherPath is not null ? (other, otherPath)
                         : (null, null)!;
        if (tool is null)
            throw new InvalidOperationException(
                "No Linux optical writer found. Install dvd+rw-tools (growisofs) for DVD/Blu-ray or wodim/cdrkit for CD.");

        var args = tool == "growisofs" ? BuildGrowisofsArgs(device, imagePath) : BuildWodimArgs(device, imagePath);
        log($"Using {tool} on {device}…");
        return Run(path!, args, log);
    }

    /// <summary>List optical devices by probing /dev/sr* and /dev/cdrom.</summary>
    public static IReadOnlyList<string> ListDevices()
    {
        var found = new List<string>();
        for (int i = 0; i < 8; i++) { var d = $"/dev/sr{i}"; if (File.Exists(d)) found.Add(d); }
        if (File.Exists("/dev/cdrom")) found.Add("/dev/cdrom");
        return found;
    }

    private static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static int Run(string exe, IReadOnlyList<string> args, Action<string> log)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }
}
