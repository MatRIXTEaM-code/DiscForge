// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DiscForge.App;

/// <summary>
/// A diagnostic log written to disk, so a problem can be reported with facts
/// rather than remembered fragments. Everything that reaches the on-screen event
/// log lands here too, alongside an environment header and full exception detail.
///
/// Location: %APPDATA%\DiscForge\logs\openjuggler.log (plus a .1 backup once it
/// grows past a megabyte). Nothing here leaves the machine unless the user sends
/// it deliberately.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiscForge", "logs");

    public static string FilePath { get; } = Path.Combine(Dir, "openjuggler.log");

    /// <summary>Everything logged this session, for the diagnostics report.</summary>
    private static readonly List<string> Session = new();

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Roll();
            Write("=== session started ===");
            foreach (var line in EnvironmentLines()) Write("  " + line);
        }
        catch { /* logging must never break the app */ }
    }

    /// <summary>Environment facts worth having in every bug report.</summary>
    public static IEnumerable<string> EnvironmentLines()
    {
        yield return $"DiscForge {Assembly.GetExecutingAssembly().GetName().Version}";
        // A version number can lag if someone forgets to bump it (it has). Report
        // what the build can actually DO, so a diagnostic is never ambiguous.
        yield return $"Build has     : {string.Join(", ", Capabilities())}";
        yield return $"OS           : {RuntimeInformation.OSDescription}";
        yield return $"Architecture : {RuntimeInformation.OSArchitecture} / process {RuntimeInformation.ProcessArchitecture}";
        yield return $".NET         : {RuntimeInformation.FrameworkDescription}";
        yield return $"Elevated     : {IsElevated()}";
        yield return $"Culture      : {System.Globalization.CultureInfo.CurrentCulture.Name}";
        yield return $"Time (UTC)   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
    }

    /// <summary>Feature markers, detected from the types actually compiled in.</summary>
    private static IEnumerable<string> Capabilities()
    {
        var core = typeof(DiscForge.Core.Cdi.CdiParser).Assembly;
        var devices = typeof(DiscForge.Devices.DriveDetector).Assembly;

        if (core.GetType("DiscForge.Core.Reading.ReadPlanner") is not null) yield return "read";
        if (core.GetType("DiscForge.Core.Copying.CopyPlanner") is not null) yield return "copy";
        if (core.GetType("DiscForge.Core.Mds.MdsParser") is not null) yield return "mds";
        if (core.GetType("DiscForge.Core.Iso.IsoReader") is not null) yield return "browse";
        if (core.GetType("DiscForge.Core.Create.AudioCdCreator") is not null) yield return "audio-create";
        if (core.GetType("DiscForge.Core.Audio.JitterCorrection") is not null) yield return "jitter";
        if (core.GetType("DiscForge.Core.Udf.UdfReader") is not null) yield return "udf";
        if (core.GetType("DiscForge.Core.Convert.IsoConverter") is not null) yield return "iso-convert";
        if (devices.GetType("DiscForge.Devices.Burning.Imapi2TrackAtOnceBurnEngine") is not null)
            yield return "audio-burn(TAO)";
    }

    private static string IsElevated()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return "n/a";
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator) ? "yes" : "no (Drives/Burn/Read need admin)";
        }
        catch { return "unknown"; }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            Session.Add(line);
            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch { /* disk full / permissions: keep the in-memory copy */ }
        }
    }

    public static void WriteException(string context, Exception ex)
    {
        Write($"EXCEPTION in {context}: {ex.GetType().Name}: {ex.Message}");
        Write(ex.ToString());
        if (ex.InnerException is not null)
            Write("  inner: " + ex.InnerException);
    }

    /// <summary>A complete report: environment + this session's log.</summary>
    public static string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("DiscForge diagnostic report");
        sb.AppendLine("=============================");
        sb.AppendLine();
        foreach (var line in EnvironmentLines()) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("Session log");
        sb.AppendLine("-----------");
        lock (Gate)
            foreach (var line in Session) sb.AppendLine(line);
        return sb.ToString();
    }

    private static void Roll()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var backup = FilePath + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(FilePath, backup);
        }
        catch { /* best effort */ }
    }
}
