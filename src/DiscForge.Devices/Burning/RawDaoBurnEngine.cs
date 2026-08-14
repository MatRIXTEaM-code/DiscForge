// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;
using DiscForge.Core.Raw;

namespace DiscForge.Devices.Burning;

/// <summary>
/// RAW disc-at-once CD burning via IMAPI2's MsftDiscFormat2RawCD — the writer
/// that takes a complete raw image (main channel + sub-channels, lead-in
/// included) and puts it on disc exactly as given. This is what makes exact
/// gaps, index points, CD-TEXT, ISRC/MCN and mixed layouts writable: the
/// whole disc is composed by <see cref="RawImageGenerator"/> in Core, and this
/// engine's only jobs are sector-type negotiation and transport.
///
/// Per the documented IMAPI2 contract:
///   - the stream's first sector is the lead-in at MSF 95:00:00 (IMAPI seeks
///     to the media-appropriate start within it);
///   - RequestedSectorType is valid only AFTER PrepareMedia, and must be one
///     the drive lists in SupportedSectorTypes (PQ_ONLY=1 / IS_COOKED=2 /
///     IS_RAW=3);
///   - the write is single-session and results in a closed disc.
///
/// Negotiation: when the disc carries CD-TEXT the image needs R-W symbols, so
/// IS_COOKED (or IS_RAW) is required; otherwise PQ_ONLY is preferred as the
/// most widely supported raw mode. The generated image is staged to a temp
/// file — a full 80-minute DAO-96 image is ~880 MB, in line with the staging
/// the other engines already do.
///
/// Status: the COM sequence follows the documented flow and the image format
/// follows ECMA-130 / MMC; both MUST be validated on real hardware (see
/// docs/RAW_DAO.md for the checklist — audio CD first).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawDaoBurnEngine : IBurnEngine
{
    private const int SubcodePqOnly = 1;      // IMAPI_FORMAT2_RAW_CD_SUBCODE_PQ_ONLY
    private const int SubcodeIsCooked = 2;    // …_IS_COOKED (IMAPI2's default)
    private const int SubcodeIsRaw = 3;       // …_IS_RAW

    public bool Supports(BurnMethod method) => method == BurnMethod.RawDao96;

    /// <summary>Burn a CDI image RAW (single session).</summary>
    public void Burn(Stream cdi, CdiImage image, BurnPlan plan,
                     IProgress<BurnProgress>? progress = null)
    {
        if (plan.Method != BurnMethod.RawDao96)
            throw new NotSupportedException("RawDaoBurnEngine handles only RawDao96 plans.");

        using var layout = DiscLayout.FromCdi(image, cdi);
        BurnLayout(layout, plan, progress);
    }

    /// <summary>
    /// Burn a fully specified layout (from a CUE sheet or a CDI) RAW.
    /// This is the entry point that carries the complete CUE semantics —
    /// indexes, gaps, flags, ISRC/MCN, CD-TEXT.
    /// </summary>
    public void BurnLayout(DiscLayout layout, BurnPlan plan,
                           IProgress<BurnProgress>? progress = null,
                           int? forcedSectorType = null, bool probeOnly = false)
    {
        Type masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
            ?? throw new InvalidOperationException("IMAPI2 not available on this system.");
        dynamic master = Activator.CreateInstance(masterType)!;
        try
        {
            string uniqueId = Imapi2MediaTools.FindRecorderId(master, plan.DevicePath)
                ?? throw new InvalidOperationException($"No IMAPI2 recorder matches {plan.DevicePath}.");

            Type recType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
            dynamic recorder = Activator.CreateInstance(recType)!;
            try
            {
                recorder.InitializeDiscRecorder(uniqueId);

                Type rawType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2RawCD")
                    ?? throw new NotSupportedException(
                        "IMAPI2's raw CD writer is not available on this system.");
                dynamic format = Activator.CreateInstance(rawType)!;
                try
                {
                    format.Recorder = recorder;
                    format.ClientName = "DiscForge";

                    CheckMedia(format, recorder);

                    progress?.Report(new BurnProgress("prepare", 0.02, "Preparing media (DAO)"));
                    try { format.PrepareMedia(); }
                    catch (COMException ex)
                    { throw new IOException("PrepareMedia failed. " + Describe(ex), ex); }

                    bool completed = false;
                    try
                    {
                        // Report what the drive actually supports (only valid after
                        // PrepareMedia) — this is the diagnostic that tells us which raw
                        // subcode modes exist without having to burn a disc to find out.
                        HashSet<int> supported = ReadSupportedSectorTypes(format);
                        progress?.Report(new BurnProgress("prepare", 0.025,
                            "Drive supports raw sector types: " +
                            (supported.Count == 0 ? "(none reported)" : string.Join(", ", supported.Select(NameSectorType)))));

                        if (probeOnly)
                        {
                            progress?.Report(new BurnProgress("probe", 1.0,
                                "Probe only — media prepared and released, nothing written."));
                            completed = true;
                            return;
                        }

                        // Sector type: only valid after PrepareMedia.
                        var form = NegotiateSectorType(format, layout, progress, supported, forcedSectorType);

                        if (plan.WriteSpeedSectorsPerSecond is int sps)
                        {
                            try
                            {
                                format.SetWriteSpeed(sps, false);
                                progress?.Report(new BurnProgress("prepare", 0.04,
                                    $"Write speed requested: {sps} sectors/s"));
                            }
                            catch (COMException)
                            {
                                progress?.Report(new BurnProgress("prepare", 0.04,
                                    "The drive rejected the speed request — burning at its default"));
                            }
                        }

                        // Stage the generated image. Streaming generation into
                        // the burn would risk buffer underruns on the ECC path;
                        // a temp file keeps the write side simple and fast.
                        var tmp = Path.Combine(Path.GetTempPath(),
                            "ojug_raw_" + Guid.NewGuid().ToString("N") + ".img");
                        try
                        {
                            long total = RawImageGenerator.TotalSectors(layout);
                            progress?.Report(new BurnProgress("prepare", 0.05,
                                $"Composing raw image: {total:N0} sectors × " +
                                $"{RawImageGenerator.SectorSize(form)} bytes"));

                            using (var img = File.Create(tmp))
                            {
                                var gen = progress is null ? null : new Progress<double>(f =>
                                    progress.Report(new BurnProgress("prepare",
                                        0.05 + f * 0.25, "Composing raw image")));
                                RawImageGenerator.Generate(layout, form, img, gen);
                            }

                            progress?.Report(new BurnProgress("burn", 0.3,
                                "Writing raw image (DAO) — this cannot be cancelled safely"));

                            dynamic stream = CreateComStreamFromFile(tmp);
                            try { format.WriteMedia(stream); }
                            catch (COMException ex)
                            { throw new IOException("Raw write failed. " + Describe(ex), ex); }
                            finally
                            {
                                if (stream is not null && Marshal.IsComObject(stream))
                                    Marshal.ReleaseComObject(stream);
                            }
                            completed = true;
                        }
                        finally
                        {
                            try { File.Delete(tmp); } catch { /* best effort */ }
                        }
                    }
                    finally
                    {
                        // Always release the media, but don't let a release
                        // failure mask the real error of a failed write.
                        try { format.ReleaseMedia(); }
                        catch when (!completed) { /* already failing */ }
                    }

                    progress?.Report(new BurnProgress("finalize", 1.0, "RAW burn complete"));
                }
                finally { Marshal.FinalReleaseComObject(format); }
            }
            finally { Marshal.FinalReleaseComObject(recorder); }
        }
        finally { Marshal.FinalReleaseComObject(master); }
    }

    // ---- pieces ------------------------------------------------------------

    private static void CheckMedia(dynamic format, dynamic recorder)
    {
        bool supported;
        try { supported = format.IsCurrentMediaSupported(recorder); }
        catch (COMException ex)
        { throw new IOException("Could not read the disc in the drive. " + Describe(ex), ex); }

        if (!supported)
            throw new IOException(
                "The disc in the drive can't be written raw. RAW DAO needs blank CD-R or " +
                "CD-RW media; pressed discs, DVDs and BDs are not raw-CD writable.");

        bool blank = true;
        try { blank = format.MediaPhysicallyBlank; } catch { /* older stacks */ }
        if (!blank)
            throw new IOException(
                "RAW DAO writes the whole disc from the lead-in, so the disc must be " +
                "physically blank. Erase it first (if rewritable) or use a fresh one.");
    }

    /// <summary>
    /// Pick the sector type: what the disc needs (CD-TEXT ⇒ R-W symbols)
    /// intersected with what the drive supports.
    /// </summary>
    private RawSubcodeForm NegotiateSectorType(dynamic format, DiscLayout layout,
                                               IProgress<BurnProgress>? progress,
                                               HashSet<int> supported, int? forcedSectorType)
    {
        bool needsRw = !layout.CdText.IsEmpty || layout.HasProgramRw || layout.HasVerbatimSubchannel;

        // An explicit --subcode override wins over auto-negotiation: on real hardware the
        // auto-picked PQ-16 layout is not always what IMAPI2 accepts, so let the operator
        // force IS_COOKED (IMAPI2's default) or IS_RAW and see what the drive takes.
        if (forcedSectorType is int forced)
        {
            var forcedForm = forced switch
            {
                SubcodePqOnly => RawSubcodeForm.Pq16,
                SubcodeIsCooked => RawSubcodeForm.Packed96,
                SubcodeIsRaw => RawSubcodeForm.Interleaved96,
                _ => throw new IOException($"Unknown forced sector type {forced}."),
            };
            if (supported.Count > 0 && !supported.Contains(forced))
                progress?.Report(new BurnProgress("prepare", 0.028,
                    $"note: the drive did not advertise {NameSectorType(forced)} — trying it anyway on request."));
            try { format.RequestedSectorType = forced; }
            catch (COMException ex)
            { throw new IOException($"The drive rejected forced raw sector type {NameSectorType(forced)}. " + Describe(ex), ex); }
            progress?.Report(new BurnProgress("prepare", 0.03,
                $"Raw sector type (forced): {forcedForm} ({RawImageGenerator.SectorSize(forcedForm)} bytes/sector)"));
            return forcedForm;
        }

        (int type, RawSubcodeForm form)? pick = null;
        if (!needsRw && supported.Contains(SubcodePqOnly))
            pick = (SubcodePqOnly, RawSubcodeForm.Pq16);
        else if (supported.Contains(SubcodeIsCooked))
            pick = (SubcodeIsCooked, RawSubcodeForm.Packed96);
        else if (supported.Contains(SubcodeIsRaw))
            pick = (SubcodeIsRaw, RawSubcodeForm.Interleaved96);
        else if (supported.Contains(SubcodePqOnly))
            pick = (SubcodePqOnly, RawSubcodeForm.Pq16);   // last resort; drops CD-TEXT

        if (pick is null)
        {
            // No list available: IS_COOKED is IMAPI2's documented default.
            pick = (SubcodeIsCooked, RawSubcodeForm.Packed96);
        }

        if (needsRw && pick.Value.type == SubcodePqOnly)
        {
            if (layout.HasVerbatimSubchannel)
                throw new IOException(
                    "This copy preserves the source's sub-channel verbatim (protection " +
                    "data), but the drive only writes P-Q raw sectors. Writing would " +
                    "discard the protection and produce a non-working disc, so DiscForge " +
                    "won't. Use a drive that supports full 96-byte raw subcode.");
            if (layout.HasProgramRw)
                throw new IOException(
                    "This disc carries CD+G sub-channel graphics, but the drive only " +
                    "writes P-Q raw sectors (no R-W). Burning would silently produce a " +
                    "music-only disc, so DiscForge won't. Use a drive that supports " +
                    "full 96-byte raw subcode.");
            progress?.Report(new BurnProgress("prepare", 0.03,
                "This drive only writes P-Q raw sectors, which cannot carry CD-TEXT — " +
                "the disc will burn without it"));
        }

        try { format.RequestedSectorType = pick.Value.type; }
        catch (COMException ex)
        {
            throw new IOException(
                $"The drive rejected raw sector type {pick.Value.type}. " + Describe(ex), ex);
        }

        progress?.Report(new BurnProgress("prepare", 0.03,
            $"Raw sector type: {pick.Value.form} " +
            $"({RawImageGenerator.SectorSize(pick.Value.form)} bytes/sector)"));
        return pick.Value.form;
    }

    private static HashSet<int> ReadSupportedSectorTypes(dynamic format)
    {
        var supported = new HashSet<int>();
        try
        {
            foreach (object v in format.SupportedSectorTypes)
                supported.Add(System.Convert.ToInt32(v));
        }
        catch { /* property unavailable */ }
        return supported;
    }

    private static string NameSectorType(int t) => t switch
    {
        SubcodePqOnly => "PQ_ONLY (2368 B/sector, P-Q)",
        SubcodeIsCooked => "IS_COOKED (2448 B/sector, de-interleaved R-W)",
        SubcodeIsRaw => "IS_RAW (2448 B/sector, raw interleaved P-W)",
        _ => $"type {t}",
    };

    private static string Describe(COMException ex) => (uint)ex.HResult switch
    {
        0xC0AA0600 => "A write operation is already in progress.",
        0xC0AA0602 => "The operation is only valid after the media has been prepared.",
        0xC0AA060E => "The drive does not support this raw data block type.",
        0xC0AA060D => "IMAPI2 rejected the raw data stream for the chosen sector type " +
                      "(its byte/subcode layout isn't what this mode expects). Try a different " +
                      "--subcode (cooked / raw / pq).",
        0xC0AA0606 => "Only blank CD-R/RW media is supported — insert a blank disc.",
        0xC0AA0402 => "The disc in the drive isn't supported for this operation. " +
                      "RAW DAO needs blank CD media.",
        0xC0AA0404 => "The media is write-protected.",
        0xC0AA0202 => "The drive is in use by another program.",
        0xC0AA0301 => "No disc in the drive.",
        0xC0AA0302 => "The drive tray is open.",
        _ => $"IMAPI2 error 0x{(uint)ex.HResult:X8}: {ex.Message.Trim()}",
    };

    private static object CreateComStreamFromFile(string path)
    {
        const uint STGM_READ = 0x0;
        int hr = SHCreateStreamOnFileEx(path, STGM_READ, 0, false, IntPtr.Zero, out object stream);
        if (hr != 0) throw Marshal.GetExceptionForHR(hr)!;
        return stream;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(
        string pszFile, uint grfMode, uint dwAttributes, bool fCreate,
        IntPtr pstmTemplate, [MarshalAs(UnmanagedType.Interface)] out object ppstm);
}
