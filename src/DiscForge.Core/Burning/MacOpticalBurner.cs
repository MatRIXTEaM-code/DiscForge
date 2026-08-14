// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Diagnostics;

namespace DiscForge.Core.Burning;

/// <summary>
/// The macOS optical-burn backend. Unlike Windows (IMAPI2 COM), macOS exposes disc writing through Apple's
/// own supported command-line front-ends over the DiscRecording framework — <c>hdiutil burn</c> to write an
/// image to the inserted disc, and <c>system_profiler</c>/<c>drutil</c> to enumerate the burner and its
/// media. This drives those tools so <c>dforge burn</c> / <c>dforge drives</c> work on a Mac with an
/// (internal or external) optical writer, from the ordinary cross-platform build — no Windows required.
/// The argument-building is a pure function so it can be verified without a drive; the run methods shell out.
/// </summary>
public static class MacOpticalBurner
{
    /// <summary>Build the <c>hdiutil</c> argument list for burning <paramref name="imagePath"/>.</summary>
    /// <remarks>hdiutil verifies the written disc by default; passing <c>-noverifyburn</c> skips that.</remarks>
    public static IReadOnlyList<string> BuildBurnArgs(string imagePath, bool verify, int? speedMultiplier = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        var args = new List<string> { "burn", imagePath };
        if (!verify) args.Add("-noverifyburn");
        if (speedMultiplier is > 0) { args.Add("-speed"); args.Add(speedMultiplier.Value.ToString()); }
        return args;
    }

    /// <summary>The <c>system_profiler</c> argument list that reports the optical burner and any loaded media.</summary>
    public static IReadOnlyList<string> BuildDrivesArgs() => new[] { "SPDiscBurningDataType", "-detailLevel", "mini" };

    /// <summary>Burn an image to the inserted disc via hdiutil. Returns the process exit code (0 = success).</summary>
    public static int RunBurn(string imagePath, bool verify, int? speedMultiplier, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The hdiutil burn backend is macOS-only.");
        return Run("hdiutil", BuildBurnArgs(imagePath, verify, speedMultiplier), log);
    }

    /// <summary>List optical burners and their media via system_profiler. Returns the process exit code.</summary>
    public static int RunDrives(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The macOS drive enumeration backend is macOS-only.");
        return Run("system_profiler", BuildDrivesArgs(), log);
    }

    private static int Run(string exe, IReadOnlyList<string> args, Action<string> log)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
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
