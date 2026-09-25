// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Licensing;

namespace DiscForge.App;

/// <summary>
/// The application's licence state: the stored key (validated against the embedded vendor public
/// key and this machine's id) and, without a valid key, the 30-day trial (<see cref="TrialStore"/>).
/// While the trial runs everything works; once it ends the app asks for a key at start-up and
/// closes without one. The same check guards the dforge CLI.
/// </summary>
internal static class LicenseGate
{
    private static LicenseResult? _cached;
    private static TrialStatus? _trial;

    public static string LicensePath => Entitlement.LicensePath;

    /// <summary>This machine's opaque id, derived from the Windows MachineGuid (name fallback).</summary>
    public static string MachineId => Entitlement.CurrentMachineId();

    public static LicenseResult Status => _cached ??= Entitlement.CurrentLicense(MachineId, DateTime.UtcNow);

    public static bool IsLicensed => Status.IsValid;

    /// <summary>The trial state (checked once per run; also records the trial's start on first run).</summary>
    public static TrialStatus Trial => _trial ??= TrialStore.Check(MachineId, DateTime.UtcNow);

    /// <summary>Licensed, or still within the trial.</summary>
    public static bool CanRun => IsLicensed || Trial.CanRun;

    /// <summary>Short text for title bars and the About box.</summary>
    public static string StatusText => IsLicensed
        ? $"Licensed to {Status.Info?.Name}"
        : Trial.Describe();

    /// <summary>Try to activate with a pasted key; on success it is stored and becomes the state.</summary>
    public static LicenseResult Activate(string key)
    {
        var r = License.Validate(key?.Trim(), LicenseConfig.PublicSpki, MachineId, DateTime.UtcNow);
        if (r.IsValid)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LicensePath)!);
                File.WriteAllText(LicensePath, key!.Trim());
                _cached = r;
            }
            catch (Exception ex) { AppLog.WriteException("license-save", ex); }
        }
        return r;
    }
}
