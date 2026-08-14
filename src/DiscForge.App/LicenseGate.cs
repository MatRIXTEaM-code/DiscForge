// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Licensing;
using Microsoft.Win32;

namespace DiscForge.App;

/// <summary>
/// The application's licence state: reads the stored key, derives this machine's id, and
/// validates against the embedded vendor public key. Enforcement is deliberately soft —
/// an unlicensed copy still runs, but the shell shows an "evaluation" watermark and the
/// About dialog carries an Activate button. (A client-side check is a deterrent, not DRM;
/// the licensing raises the bar and enables legitimate sales, nothing more.)
/// </summary>
internal static class LicenseGate
{
    private static LicenseResult? _cached;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscForge");

    public static string LicensePath => Path.Combine(Dir, "license.key");

    /// <summary>This machine's opaque id, derived from the Windows MachineGuid (name fallback).</summary>
    public static string MachineId => DiscForge.Core.Licensing.MachineId.FromRaw(RawMachine());

    public static LicenseResult Status => _cached ??= Evaluate();

    public static bool IsLicensed => Status.IsValid;

    /// <summary>Try to activate with a pasted key; on success it is stored and becomes the state.</summary>
    public static LicenseResult Activate(string key)
    {
        var r = License.Validate(key?.Trim(), LicenseConfig.PublicSpki, MachineId, DateTime.UtcNow);
        if (r.IsValid)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(LicensePath, key!.Trim());
                _cached = r;
            }
            catch (Exception ex) { AppLog.WriteException("license-save", ex); }
        }
        return r;
    }

    private static LicenseResult Evaluate()
    {
        string? key = null;
        try { if (File.Exists(LicensePath)) key = File.ReadAllText(LicensePath); }
        catch (Exception ex) { AppLog.WriteException("license-read", ex); }
        return License.Validate(key, LicenseConfig.PublicSpki, MachineId, DateTime.UtcNow);
    }

    private static string RawMachine()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (k?.GetValue("MachineGuid") is string g && g.Length > 0) return g;
        }
        catch { /* fall back */ }
        return Environment.MachineName;
    }
}
