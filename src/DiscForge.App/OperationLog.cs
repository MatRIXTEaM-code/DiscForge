// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Devices;
using DiscForge.Core.Media;

namespace DiscForge.App;

/// <summary>
/// Builds a self-contained record of one operation: what hardware, what media,
/// what settings, what happened.
///
/// The application log records everything across a session, which is right for
/// diagnosing DiscForge but wrong for describing a single job. Someone asking
/// "why did this disc rip badly" needs the drive, its firmware, the media's
/// manufacturer, the options chosen and the result — in one file, without the
/// twelve unrelated operations either side of it.
///
/// The hardware details matter more than they look. Optical drives differ
/// enormously in what they can read from damaged media, and firmware revisions
/// differ within a model. A report saying "it failed" is nearly useless; one
/// saying "MATSHITA UJ8E2 S firmware 1.00, CMC Magnetics CD-R, 4x, ASC 0x02 on
/// ten sectors" tells you the drive couldn't track the disc and suggests trying
/// another.
/// </summary>
internal sealed class OperationLog
{
    private readonly StringBuilder _body = new();
    private readonly DateTime _started = DateTime.Now;

    public string Operation { get; }

    public OperationLog(string operation)
    {
        Operation = operation;
    }

    /// <summary>Drive make, model, firmware and capabilities.</summary>
    public void Drive(DriveCapabilities drive, DriveCapabilityPage? caps = null)
    {
        _body.AppendLine("DRIVE");
        _body.AppendLine($"  {drive.Vendor} {drive.Model}");
        _body.AppendLine($"  Firmware      {drive.FirmwareRevision}");
        _body.AppendLine($"  Device        {drive.DevicePath}");
        _body.AppendLine($"  CD  read/write {drive.CdRead}/{drive.CdWrite}");
        _body.AppendLine($"  DVD read/write {drive.DvdRead}/{drive.DvdWrite}");
        _body.AppendLine($"  BD  read/write {drive.BdRead}/{drive.BdWrite}");
        _body.AppendLine($"  RAW DAO-96    {drive.RawDao96}");

        if (caps is not null)
        {
            _body.AppendLine($"  C2 pointers   {caps.C2Pointers}");
            _body.AppendLine($"  Accurate stream {caps.CddaAccurateStream}");
            _body.AppendLine($"  Buffer        {caps.BufferSizeKb:N0} KB");
            if (caps.MaxReadSpeedKbs > 0)
                _body.AppendLine($"  Max read      {caps.MaxReadSpeedKbs:N0} KB/s " +
                                 $"({DriveCapabilityPage.ToCdX(caps.MaxReadSpeedKbs):0.0}x CD)");
        }
        _body.AppendLine();
    }

    /// <summary>What disc was in it.</summary>
    public void Media(DriveCapabilities drive, MediaIdentity? identity = null)
    {
        _body.AppendLine("MEDIA");
        _body.AppendLine($"  Profile       {drive.MediaProfile}");
        if (drive.Disc is not null)
            _body.AppendLine($"  State         {drive.Disc.Describe()}");

        if (identity is not null)
        {
            if (identity.AtipCode is not null)
                _body.AppendLine($"  ATIP          {identity.AtipCode}");
            if (identity.MediaId is not null)
                _body.AppendLine($"  Media ID      {identity.MediaId}");
            _body.AppendLine($"  Manufacturer  {identity.Manufacturer ?? "not in the reference table"}");
            if (identity.CapacityMb is { } mb)
                _body.AppendLine($"  Capacity      {mb:N0} MB");
        }
        else
        {
            _body.AppendLine("  (no manufacturing identity — normal for a pressed disc)");
        }
        _body.AppendLine();
    }

    /// <summary>The options the operation ran with.</summary>
    public void Settings(params (string Name, object Value)[] settings)
    {
        _body.AppendLine("SETTINGS");
        foreach (var (name, value) in settings)
            _body.AppendLine($"  {name,-20}{value}");
        _body.AppendLine();
    }

    /// <summary>The report the operation produced, verbatim.</summary>
    public void Result(string text)
    {
        _body.AppendLine("RESULT");
        foreach (var line in text.Split('\n'))
            _body.AppendLine("  " + line.TrimEnd('\r'));
        _body.AppendLine();
    }

    public void Section(string title, string text)
    {
        _body.AppendLine(title.ToUpperInvariant());
        foreach (var line in text.Split('\n'))
            _body.AppendLine("  " + line.TrimEnd('\r'));
        _body.AppendLine();
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"  DiscForge — {Operation}");
        sb.AppendLine($"  {_started:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Version {typeof(OperationLog).Assembly.GetName().Version}");
        sb.AppendLine($"  {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.Append(_body);
        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"  Finished {DateTime.Now:HH:mm:ss} " +
                      $"({(DateTime.Now - _started).TotalSeconds:N0}s)");
        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        return sb.ToString();
    }

    /// <summary>
    /// Offer to save this log. Returns the path written, or null if the user
    /// declined.
    /// </summary>
    public string? SaveWithDialog()
    {
        using var dlg = new System.Windows.Forms.SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"discforge-{Sanitise(Operation)}-{_started:yyyyMMdd-HHmmss}.txt",
            InitialDirectory = AppSettings.LastLogDirectory ?? "",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;

        try
        {
            File.WriteAllText(dlg.FileName, ToString());
            AppSettings.LastLogDirectory = Path.GetDirectoryName(dlg.FileName);
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            AppLog.WriteException("save operation log", ex);
            return null;
        }
    }

    private static string Sanitise(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) || c == ' ' ? '-' : char.ToLowerInvariant(c))
                           .ToArray());
    }
}