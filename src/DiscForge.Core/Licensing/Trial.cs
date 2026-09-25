// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DiscForge.Core.Licensing;

/// <summary>Where a copy stands: licensed, in its trial, or needing a key.</summary>
public enum TrialState
{
    /// <summary>Within the trial period.</summary>
    Active,
    /// <summary>The trial period has ended.</summary>
    Expired,
    /// <summary>The stored trial record was edited, or the clock was set back before it — treated as ended.</summary>
    Tampered,
}

/// <summary>The outcome of <see cref="Trial.Evaluate"/>.</summary>
public sealed record TrialStatus
{
    public required TrialState State { get; init; }
    public required DateTime StartUtc { get; init; }
    public required int DaysLeft { get; init; }
    public bool CanRun => State == TrialState.Active;

    public string Describe() => State switch
    {
        TrialState.Active => DaysLeft == 1 ? "Trial: 1 day left" : $"Trial: {DaysLeft} days left",
        TrialState.Expired => "The trial has ended",
        _ => "The trial record is invalid (edited, or the PC clock was set back)",
    };
}

/// <summary>
/// The 30-day evaluation period. The first run records a start time; every run after that checks it.
/// The record is kept in two places (a file under %APPDATA%\DiscForge and, on Windows, the current
/// user's registry) and carries an HMAC tied to this machine's id, so a copied or hand-edited record is
/// rejected and deleting one copy doesn't reset the trial. Deleting every copy does start a new trial —
/// this is a deterrent for honest users, not copy protection; the licence key is the real gate.
///
/// The evaluation itself (<see cref="Evaluate"/>) is a pure function of the stored records, the machine
/// id and the clock, so it is fully unit-testable; <see cref="TrialStore"/> does the I/O.
/// </summary>
public static class Trial
{
    public const int DefaultDays = 30;
    private const string Version = "v1";
    // Clock set back by more than this (compared with the last run) counts as tampering.
    private static readonly TimeSpan ClockSlack = TimeSpan.FromDays(2);

    /// <summary>A stored trial record: start, the latest time seen, and the integrity tag.</summary>
    public sealed record Record(DateTime StartUtc, DateTime LastSeenUtc);

    /// <summary>
    /// Decide the trial state from whatever records were found (any may be null or garbage), and return
    /// the record to write back to every store.
    /// </summary>
    public static (TrialStatus Status, string ToStore) Evaluate(
        IEnumerable<string?> storedRecords, string machineId, DateTime nowUtc, int days = DefaultDays)
    {
        bool anyPresent = false, anyTampered = false;
        Record? best = null;
        foreach (var raw in storedRecords)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            anyPresent = true;
            var rec = Parse(raw, machineId);
            if (rec is null) { anyTampered = true; continue; }
            // The earliest start and the latest "last seen" win: combining copies can only shorten a trial.
            best = best is null
                ? rec
                : new Record(
                    rec.StartUtc < best.StartUtc ? rec.StartUtc : best.StartUtc,
                    rec.LastSeenUtc > best.LastSeenUtc ? rec.LastSeenUtc : best.LastSeenUtc);
        }

        if (best is null && anyPresent && anyTampered)
        {
            // Only unreadable records: keep them unreadable (don't hand out a fresh trial).
            var bad = new TrialStatus { State = TrialState.Tampered, StartUtc = nowUtc, DaysLeft = 0 };
            return (bad, Format(new Record(DateTime.MinValue.AddYears(1), nowUtc), machineId));
        }

        best ??= new Record(nowUtc, nowUtc);
        TrialState state;
        if (nowUtc + ClockSlack < best.LastSeenUtc || nowUtc + ClockSlack < best.StartUtc) state = TrialState.Tampered;
        else if (nowUtc >= best.StartUtc.AddDays(days)) state = TrialState.Expired;
        else state = TrialState.Active;

        int left = state == TrialState.Active
            ? Math.Max(1, (int)Math.Ceiling((best.StartUtc.AddDays(days) - nowUtc).TotalDays))
            : 0;
        var lastSeen = nowUtc > best.LastSeenUtc ? nowUtc : best.LastSeenUtc;
        return (new TrialStatus { State = state, StartUtc = best.StartUtc, DaysLeft = left },
                Format(new Record(best.StartUtc, lastSeen), machineId));
    }

    public static string Format(Record r, string machineId)
    {
        string payload = string.Join('|', Version,
            r.StartUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            r.LastSeenUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        return payload + "|" + Tag(payload, machineId);
    }

    public static Record? Parse(string raw, string machineId)
    {
        var parts = raw.Trim().Split('|');
        if (parts.Length != 4 || parts[0] != Version) return null;
        string payload = string.Join('|', parts[0], parts[1], parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Tag(payload, machineId)), Encoding.ASCII.GetBytes(parts[3])))
            return null;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long s) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long l))
            return null;
        if (s < DateTime.MinValue.Ticks || s > DateTime.MaxValue.Ticks || l < DateTime.MinValue.Ticks || l > DateTime.MaxValue.Ticks)
            return null;
        return new Record(new DateTime(s, DateTimeKind.Utc), new DateTime(l, DateTimeKind.Utc));
    }

    private static string Tag(string payload, string machineId)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("DiscForge trial record|" + machineId));
        return System.Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>
/// Reads and writes the trial record in its two homes. Every failure is swallowed: a store that can't
/// be read or written simply doesn't contribute (with both gone, a new trial starts — see <see cref="Trial"/>).
/// </summary>
public static class TrialStore
{
    private const string RegistryKey = @"Software\DiscForge";
    private const string RegistryValue = "TrialRecord";

    public static string DataDirectory
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "DiscForge");
        }
    }

    private static string FilePath => Path.Combine(DataDirectory, "trial.dat");

    /// <summary>Load, evaluate and write back — the one call the app and CLI make.</summary>
    public static TrialStatus Check(string machineId, DateTime nowUtc, int days = Trial.DefaultDays)
    {
        var (status, toStore) = Trial.Evaluate(new[] { ReadFile(), ReadRegistry() }, machineId, nowUtc, days);
        WriteFile(toStore);
        WriteRegistry(toStore);
        return status;
    }

    private static string? ReadFile()
    {
        try { return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null; } catch { return null; }
    }

    private static void WriteFile(string value)
    {
        try { Directory.CreateDirectory(DataDirectory); File.WriteAllText(FilePath, value); } catch { }
    }

    private static string? ReadRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKey);
            return k?.GetValue(RegistryValue) as string;
        }
        catch { return null; }
    }

    private static void WriteRegistry(string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKey);
            k.SetValue(RegistryValue, value);
        }
        catch { }
    }
}

/// <summary>Licence + trial together: what the app and the CLI both ask before doing any work.</summary>
public static class Entitlement
{
    public static string LicensePath => Path.Combine(TrialStore.DataDirectory, "license.key");

    /// <summary>The installed licence key's validation result (Missing when there is none).</summary>
    public static LicenseResult CurrentLicense(string machineId, DateTime nowUtc)
    {
        string? key = null;
        try { if (File.Exists(LicensePath)) key = File.ReadAllText(LicensePath); } catch { }
        return License.Validate(key, LicenseConfig.PublicSpki, machineId, nowUtc);
    }

    /// <summary>This machine's id: the Windows MachineGuid (falls back to the computer name).</summary>
    public static string CurrentMachineId()
    {
        string raw = Environment.MachineName;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (k?.GetValue("MachineGuid") is string g && g.Length > 0) raw = g;
            }
            catch { }
        }
        return MachineId.FromRaw(raw);
    }
}
