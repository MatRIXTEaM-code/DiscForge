// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cue;
using DiscForge.Core.Util;

namespace DiscForge.Core.Raw;

/// <summary>
/// Offline analyser for raw CD images — the instrument for hardware
/// validation. Given a file of raw sectors (2368 PQ, 2448 packed or
/// interleaved, or bare 2352 main channel), it works out what it's looking at
/// and reports: the TOC decoded from the lead-in Q, sub-channel CRC health,
/// MCN/ISRC frames found, CD-TEXT decoded back out of the lead-in R–W,
/// per-track scramble state, and EDC/ECC validity of data sectors.
///
/// Nothing here consults the generator's intent: format detection is by
/// Q-CRC voting, CD-TEXT comes from the pack bytes, ECC by syndrome
/// evaluation. That independence is the point — run it on a DiscForge image
/// and on a rip of the burned disc, and differences are findings. Run it on
/// a rip of a PRESSED disc and a clean ECC verdict settles the parity-order
/// convention without burning anything.
/// </summary>
public static class RawImageInspector
{
    public sealed record TrackInfo
    {
        public required int Number { get; init; }
        public bool IsData { get; init; }
        /// <summary>Start of index 1 in absolute sectors (MSF 00:00:00 base),
        /// from the TOC when present, else from the program scan.</summary>
        public long StartSector { get; init; }
        public string? Isrc { get; init; }
        /// <summary>Data tracks: whether the main channel is scrambled on disc.</summary>
        public bool? Scrambled { get; init; }
        public int? Mode { get; init; }
        public int DataSectorsChecked { get; init; }
        public int EdcErrors { get; init; }
        public int EccErrors { get; init; }
        /// <summary>What the data checks covered: "Mode 1 EDC+ECC",
        /// "XA Form 1/2 EDC", or an honest "none" for formless Mode 2.</summary>
        public string? CheckKind { get; init; }
        /// <summary>Sampled sectors inside this DATA track that carry no
        /// 12-byte sync pattern — structurally impossible in healthy data, so
        /// each one is a void or foreign content, and none of them received
        /// any EDC/ECC check. Silently skipping them once let a 47%-empty
        /// image print "clean"; they are counted now.</summary>
        public int SynclessSectors { get; init; }
    }

    public sealed record Report
    {
        public required int SectorSize { get; init; }
        public required RawSubcodeForm? Form { get; init; }     // null = main-only 2352
        public required long TotalSectors { get; init; }
        public bool HasLeadIn { get; init; }
        public long LeadInSectors { get; init; }
        /// <summary>Absolute sector of the image's first sector (a program-only
        /// rip usually starts at 150 = MSF 00:02:00).</summary>
        public long BaseSector { get; init; }
        public int QFramesChecked { get; init; }
        public int QCrcErrors { get; init; }
        public long LeadOutStartSector { get; init; }
        public string? Mcn { get; init; }
        public string? AlbumTitle { get; init; }
        public string? AlbumPerformer { get; init; }
        public IReadOnlyList<string?> TrackTitles { get; init; } = Array.Empty<string?>();
        public int CdTextPacksValid { get; init; }
        public int CdTextPacksBad { get; init; }
        public required IReadOnlyList<TrackInfo> Tracks { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        /// <summary>Main-only (2352) images: how many sectors the scan sampled.</summary>
        public int MainSectorsSampled { get; init; }
        /// <summary>Main-only images: sampled sectors with no sync pattern —
        /// audio content, or voids in a nominally-data image. Never silently
        /// skipped again.</summary>
        public int MainSynclessSectors { get; init; }
        /// <summary>Main-only images: sampled sectors that are entirely zero
        /// (a subset of the sync-less count). A large value against a data
        /// image is the muted-drive signature.</summary>
        public int MainZeroSectors { get; init; }
    }

    /// <summary>
    /// Analyse a raw image. <paramref name="deep"/> checks every sector's Q
    /// CRC and every data sector's EDC/ECC instead of sampling.
    /// </summary>
    public static Report Inspect(Stream image, bool deep = false)
    {
        var (size, form) = DetectFormat(image);
        long total = image.Length / size;
        var notes = new List<string>();

        if (form is null)
            return InspectMainOnly(image, total, deep, notes);

        // ---- lead-in / program boundary and TOC ----------------------------
        var q = new byte[12];
        long leadIn = 0;
        bool hasLeadIn = false;
        var tocStarts = new Dictionary<int, long>();
        long leadOut = 0;
        int firstTrack = 0, lastTrack = 0;
        var tocControl = new Dictionary<int, int>();

        // Scan from the top while Q says TNO=00 (lead-in).
        for (long s = 0; s < total; s++)
        {
            ReadQ(image, s, size, form.Value, q);
            if (!QCrcOk(q)) { if (hasLeadIn) { leadIn = s + 1; continue; } else break; }
            if ((q[0] & 0x0F) != 1) { leadIn = s + 1; continue; }   // non-position: keep going
            if (q[1] != 0x00) { leadIn = s; break; }                // program reached
            hasLeadIn = true;
            leadIn = s + 1;
            int point = q[2];
            long target = ((long)Bcd.To(q[7]) * 60 + Bcd.To(q[8])) * 75 + Bcd.To(q[9]);
            switch (point)
            {
                case 0xA0: firstTrack = Bcd.To(q[7]); break;
                case 0xA1: lastTrack = Bcd.To(q[7]); break;
                case 0xA2: leadOut = target; break;
                default:
                    int trk = Bcd.To((byte)point);
                    if (trk is >= 1 and <= 99)
                    {
                        tocStarts[trk] = target;
                        tocControl[trk] = q[0] >> 4;
                    }
                    break;
            }
        }
        if (!hasLeadIn) leadIn = 0;

        // Base offset of the program area: read the first valid program Q.
        long baseSector = 0;
        for (long s = leadIn; s < Math.Min(leadIn + 300, total); s++)
        {
            ReadQ(image, s, size, form.Value, q);
            if (QCrcOk(q) && (q[0] & 0x0F) == 1 && q[1] != 0x00)
            {
                long abs = ((long)Bcd.To(q[7]) * 60 + Bcd.To(q[8])) * 75 + Bcd.To(q[9]);
                baseSector = abs - (s - leadIn);
                break;
            }
        }

        // ---- program scan: Q health, tracks, MCN/ISRC ----------------------
        long programSectors = total - leadIn;
        long step = deep ? 1 : Math.Max(1, programSectors / 4000);
        int qChecked = 0, qBad = 0;
        string? mcn = null;
        var isrcs = new Dictionary<int, string>();
        var trackFirstSeen = new SortedDictionary<int, long>();
        var trackControl = new Dictionary<int, int>();

        for (long s = leadIn; s < total; s += step)
        {
            ReadQ(image, s, size, form.Value, q);
            qChecked++;
            if (!QCrcOk(q)) { qBad++; continue; }
            switch (q[0] & 0x0F)
            {
                case 1:
                    int trk = Bcd.To(q[1]);
                    if (trk is >= 1 and <= 99)
                    {
                        int index = Bcd.To(q[2]);
                        if (index >= 1 && !trackFirstSeen.ContainsKey(trk))
                            trackFirstSeen[trk] = baseSector + (s - leadIn);
                        trackControl[trk] = q[0] >> 4;
                    }
                    break;
                case 2:
                    mcn ??= DecodeMcn(q);
                    break;
                case 3:
                    // ISRC frames don't carry the track number; attribute to
                    // the nearest preceding position frame's track.
                    int owner = NearestTrack(trackFirstSeen, baseSector + (s - leadIn));
                    if (owner > 0 && !isrcs.ContainsKey(owner))
                        isrcs[owner] = DecodeIsrc(q);
                    break;
            }
        }

        // In lead-in-less rips the TOC is empty; fall back to the scan.
        var trackNumbers = tocStarts.Count > 0
            ? tocStarts.Keys.OrderBy(k => k).ToList()
            : trackFirstSeen.Keys.ToList();

        // ---- lead-in Q health + CD-TEXT ------------------------------------
        string? albumTitle = null, albumPerformer = null;
        var trackTitles = new List<string?>();
        int packsGood = 0, packsBad = 0;
        if (hasLeadIn)
        {
            long liStep = deep ? 1 : Math.Max(1, leadIn / 1000);
            for (long s = 0; s < leadIn; s += liStep)
            {
                ReadQ(image, s, size, form.Value, q);
                qChecked++;
                if (!QCrcOk(q)) qBad++;
            }

            if (form is RawSubcodeForm.Packed96 or RawSubcodeForm.Interleaved96)
                (albumTitle, albumPerformer, trackTitles, packsGood, packsBad) =
                    DecodeCdText(image, size, form.Value, Math.Min(leadIn, 200));
        }

        // ---- per-track data analysis ---------------------------------------
        var tracks = new List<TrackInfo>();
        var starts = trackNumbers.ToDictionary(n => n,
            n => tocStarts.TryGetValue(n, out var v) ? v : trackFirstSeen[n]);
        foreach (var n in trackNumbers)
        {
            long start = starts[n];
            long end = trackNumbers.Where(x => x > n).Select(x => starts[x])
                .DefaultIfEmpty(leadOut > 0 ? leadOut : baseSector + programSectors).Min();
            int control = tocControl.TryGetValue(n, out var c) ? c
                : trackControl.TryGetValue(n, out c) ? c : 0;
            bool isData = (control & 0x4) != 0;

            bool? scrambled = null;
            int? mode = null;
            int checkedCount = 0, edcErr = 0, eccErr = 0, syncless = 0;
            string? checkKind = null;
            if (isData)
            {
                var main = new byte[2352];
                long fileStart = leadIn + (start - baseSector);
                long fileEnd = leadIn + (end - baseSector);
                long dStep = deep ? 1 : Math.Max(1, (fileEnd - fileStart) / 64);
                for (long s = fileStart; s < fileEnd && s < total; s += dStep)
                {
                    image.Position = s * size;
                    image.ReadExactly(main, 0, 2352);
                    if (!HasSync(main)) { syncless++; continue; }
                    if (scrambled is null)
                    {
                        scrambled = DetectScrambled(main, out mode);
                        if (scrambled is null) continue;     // undecidable sector
                    }
                    if (scrambled == true) CdScrambler.ScrambleInPlace(main);
                    mode ??= main[15];
                    var check = CheckDataSector(main);
                    if (check is { } chk)
                    {
                        checkedCount++;
                        checkKind = chk.kind;
                        if (!chk.edcOk) edcErr++;
                        if (!chk.eccOk) eccErr++;
                    }
                    else checkKind ??= main[15] == 2
                        ? "none (formless Mode 2 — no EDC to verify)" : null;
                }
            }

            tracks.Add(new TrackInfo
            {
                Number = n,
                IsData = isData,
                StartSector = start,
                Isrc = isrcs.TryGetValue(n, out var i) ? i : null,
                Scrambled = scrambled,
                Mode = mode,
                DataSectorsChecked = checkedCount,
                EdcErrors = edcErr,
                EccErrors = eccErr,
                CheckKind = checkKind,
                SynclessSectors = syncless,
            });
            if (syncless > 0)
                notes.Add($"track {n}: {syncless} sampled data sector(s) have NO sync pattern — " +
                          "voids or foreign content the EDC/ECC checks could not even reach.");
        }

        if (firstTrack > 0 && trackNumbers.Count > 0 &&
            (firstTrack != trackNumbers.Min() || lastTrack != trackNumbers.Max()))
            notes.Add($"TOC declares tracks {firstTrack}–{lastTrack} but the scan found " +
                      $"{trackNumbers.Min()}–{trackNumbers.Max()}.");

        return new Report
        {
            SectorSize = size,
            Form = form,
            TotalSectors = total,
            HasLeadIn = hasLeadIn,
            LeadInSectors = leadIn,
            BaseSector = baseSector,
            QFramesChecked = qChecked,
            QCrcErrors = qBad,
            LeadOutStartSector = leadOut,
            Mcn = mcn,
            AlbumTitle = albumTitle,
            AlbumPerformer = albumPerformer,
            TrackTitles = trackTitles,
            CdTextPacksValid = packsGood,
            CdTextPacksBad = packsBad,
            Tracks = tracks,
            Notes = notes,
        };
    }

    // ---- main-channel-only (2352) images -----------------------------------

    private static Report InspectMainOnly(Stream image, long total, bool deep, List<string> notes)
    {
        // No subcode: report scramble state and EDC/ECC health of Mode 1
        // sectors — exactly what's needed to gold-test the ECC conventions
        // against a rip of a real pressed disc.
        var main = new byte[2352];
        bool? scrambled = null;
        int? mode = null;
        int checkedCount = 0, edcErr = 0, eccErr = 0, audioLike = 0, zeroed = 0, sampled = 0;
        long step = deep ? 1 : Math.Max(1, total / 512);

        for (long s = 0; s < total; s += step)
        {
            image.Position = s * 2352;
            image.ReadExactly(main, 0, 2352);
            sampled++;
            if (!HasSync(main))
            {
                audioLike++;
                if (IsAllZero(main)) zeroed++;
                continue;
            }
            scrambled ??= DetectScrambled(main, out mode);
            if (scrambled is null) continue;
            if (scrambled == true) CdScrambler.ScrambleInPlace(main);
            mode ??= main[15];
            if (CheckDataSector(main) is { } c)
            {
                checkedCount++;
                if (!c.edcOk) edcErr++;
                if (!c.eccOk) eccErr++;
            }
        }

        if (audioLike > 0 && checkedCount == 0)
            notes.Add("No sync patterns found — this looks like audio (or not a raw image).");
        else if (audioLike > 0)
        {
            // The half-void lesson: a data image that is partly sync-less used
            // to sail through as "clean" because these sectors were silently
            // skipped. They are either audio tracks of a mixed-mode disc
            // (legitimate) or voids (the muted-drive signature) — without
            // subcode this scan cannot tell which, so it says so out loud.
            int pct = (int)Math.Round(100.0 * audioLike / sampled);
            notes.Add($"{audioLike} of {sampled} sampled sectors ({pct}%) have NO sync pattern " +
                      $"({zeroed} entirely zero). Mixed-mode audio, or voids in a data image — " +
                      "the EDC/ECC verdict above covers ONLY the structured sectors.");
        }

        return new Report
        {
            SectorSize = 2352,
            Form = null,
            TotalSectors = total,
            MainSectorsSampled = sampled,
            MainSynclessSectors = audioLike,
            MainZeroSectors = zeroed,
            Tracks = new[]
            {
                new TrackInfo
                {
                    Number = 1,
                    IsData = checkedCount > 0,
                    StartSector = 150,
                    Scrambled = scrambled,
                    Mode = mode,
                    DataSectorsChecked = checkedCount,
                    EdcErrors = edcErr,
                    EccErrors = eccErr,
                    CheckKind = checkedCount > 0
                        ? (mode == 1 ? "Mode 1 EDC+ECC" : "XA EDC") : null,
                },
            },
            Notes = notes,
        };
    }

    // ---- detection ---------------------------------------------------------

    /// <summary>
    /// Public face of the format detector, for tools that need to open a raw
    /// image the same way the inspector does (sector viewer, extraction).
    /// </summary>
    public static (int size, RawSubcodeForm? form) DetectLayout(Stream image)
        => DetectFormat(image);

    /// <summary>
    /// Length of the lead-in in sectors (0 when the image has none) — the
    /// same boundary rule the inspector uses: Q frames with TNO 00 belong to
    /// the lead-in; the first valid program frame ends it.
    /// </summary>
    public static long FindLeadInLength(Stream image, int size, RawSubcodeForm form)
    {
        var q = new byte[12];
        long total = image.Length / size;
        long leadIn = 0;
        bool sawLeadIn = false;
        for (long s = 0; s < total; s++)
        {
            ReadQ(image, s, size, form, q);
            if (!QCrcOk(q)) { if (sawLeadIn) { leadIn = s + 1; continue; } else break; }
            if ((q[0] & 0x0F) != 1) { leadIn = s + 1; continue; }
            if (q[1] != 0x00) return sawLeadIn ? s : 0;
            sawLeadIn = true;
            leadIn = s + 1;
        }
        return sawLeadIn ? leadIn : 0;
    }

    /// <summary>
    /// Work out sector size and subcode layout by Q-CRC voting: the correct
    /// (size, layout) pair makes sub-channel CRCs come out valid; wrong ones
    /// produce noise. 2352 (main only) is the fallback when nothing votes.
    /// </summary>
    private static (int size, RawSubcodeForm? form) DetectFormat(Stream image)
    {
        long len = image.Length;
        var candidates = new List<(int size, RawSubcodeForm form)>();
        if (len % 2368 == 0) candidates.Add((2368, RawSubcodeForm.Pq16));
        if (len % 2448 == 0)
        {
            candidates.Add((2448, RawSubcodeForm.Packed96));
            candidates.Add((2448, RawSubcodeForm.Interleaved96));
        }

        (int size, RawSubcodeForm form) best = default;
        int bestScore = 0;
        var q = new byte[12];
        foreach (var c in candidates)
        {
            long total = len / c.size;
            long step = Math.Max(1, total / 200);
            int score = 0, tried = 0;
            for (long s = 0; s < total && tried < 200; s += step, tried++)
            {
                ReadQ(image, s, c.size, c.form, q);
                if (QCrcOk(q)) score++;
            }
            if (score > bestScore) { bestScore = score; best = c; }
        }

        // Demand a clear signal: at least a quarter of sampled frames valid.
        if (bestScore >= 50) return (best.size, best.form);
        if (len % 2352 == 0) return (2352, null);
        if (best.size != 0) return (best.size, best.form);   // divisible but noisy
        throw new InvalidDataException(
            $"The file is {len:N0} bytes — not a whole number of 2368, 2448 or 2352-byte " +
            "sectors, so it doesn't look like a raw CD image.");
    }

    private static void ReadQ(Stream image, long sector, int size, RawSubcodeForm form, byte[] q)
    {
        Span<byte> sub = stackalloc byte[96];
        image.Position = sector * size + 2352;
        image.ReadExactly(sub[..(size - 2352)]);
        SubcodeFrame.ExtractQ(sub[..(size - 2352)], form, q);
    }

    private static bool QCrcOk(ReadOnlySpan<byte> q)
        => Crc16.ComputeInverted(q[..10]) == (ushort)((q[10] << 8) | q[11]);

    private static bool HasSync(ReadOnlySpan<byte> main)
    {
        if (main[0] != 0 || main[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (main[i] != 0xFF) return false;
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < s.Length; i++) if (s[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// Scrambled or not? The sync bytes are outside the scrambler, so both
    /// forms have sync; the EDC decides. Returns null when neither form
    /// yields a valid Mode 1 EDC (e.g. Mode 2, or a damaged sector).
    /// </summary>
    private static bool? DetectScrambled(ReadOnlySpan<byte> main, out int? mode)
    {
        Span<byte> copy = new byte[2352];
        mode = null;

        // As stored.
        main.CopyTo(copy);
        if (copy[15] == 1 && EdcEcc.VerifyMode1(copy).EdcOk) { mode = 1; return false; }
        if (copy[15] == 2 && PlausibleBcdHeader(copy)) { mode = 2; return false; }

        // Descrambled.
        main.CopyTo(copy);
        CdScrambler.ScrambleInPlace(copy);
        if (copy[15] == 1 && EdcEcc.VerifyMode1(copy).EdcOk) { mode = 1; return true; }
        if (copy[15] == 2 && PlausibleBcdHeader(copy)) { mode = 2; return true; }

        return null;
    }

    /// <summary>
    /// Integrity-check one UNSCRAMBLED data sector, whatever its mode:
    /// Mode 1 → EDC + full ECC syndromes. Mode 2 with a duplicated XA
    /// subheader → Form 1 EDC (bytes 16..2071, stored at 2072) or Form 2 EDC
    /// (bytes 16..2347, stored at 2348; optional — zero means unused).
    /// Returns null when the sector carries nothing checkable.
    /// </summary>
    private static (bool edcOk, bool eccOk, string kind)? CheckDataSector(ReadOnlySpan<byte> main)
    {
        if (main[15] == 1)
        {
            var (edcOk, eccOk) = EdcEcc.VerifyMode1(main);
            return (edcOk, eccOk, "Mode 1 EDC+ECC");
        }
        if (main[15] == 2)
        {
            // XA subheader is stored twice (bytes 16..19 == 20..23); random or
            // formless data won't duplicate, so this is the XA detector.
            if (main[16] != main[20] || main[17] != main[21] ||
                main[18] != main[22] || main[19] != main[23]) return null;

            bool form2 = (main[18] & 0x20) != 0;
            if (!form2)
            {
                uint edc = EdcEcc.ComputeEdc(main[16..2072]);
                uint stored = (uint)main[2072] | ((uint)main[2073] << 8)
                            | ((uint)main[2074] << 16) | ((uint)main[2075] << 24);
                return (edc == stored, true, "XA Form 1 EDC");
            }
            uint edc2 = EdcEcc.ComputeEdc(main[16..2348]);
            uint stored2 = (uint)main[2348] | ((uint)main[2349] << 8)
                         | ((uint)main[2350] << 16) | ((uint)main[2351] << 24);
            if (stored2 == 0) return null;              // Form 2 EDC unused
            return (edc2 == stored2, true, "XA Form 2 EDC");
        }
        return null;
    }

    private static bool PlausibleBcdHeader(ReadOnlySpan<byte> s) =>
        (s[12] & 0x0F) <= 9 && (s[12] >> 4) <= 9 &&
        (s[13] & 0x0F) <= 9 && (s[13] >> 4) <= 9 && Bcd.To(s[13]) < 60 &&
        (s[14] & 0x0F) <= 9 && (s[14] >> 4) <= 9 && Bcd.To(s[14]) < 75;

    // ---- Q payload decoders ------------------------------------------------

    private static string DecodeMcn(ReadOnlySpan<byte> q)
    {
        var sb = new StringBuilder(13);
        for (int d = 0; d < 13; d++)
        {
            int b = q[1 + d / 2];
            sb.Append((char)('0' + ((d & 1) == 0 ? b >> 4 : b & 0x0F)));
        }
        return sb.ToString();
    }

    private static string DecodeIsrc(ReadOnlySpan<byte> q)
    {
        int c1 = q[1] >> 2;
        int c2 = ((q[1] & 3) << 4) | (q[2] >> 4);
        int c3 = ((q[2] & 0xF) << 2) | (q[3] >> 6);
        int c4 = q[3] & 0x3F;
        int c5 = q[4] >> 2;
        var sb = new StringBuilder(12);
        foreach (int c in new[] { c1, c2, c3, c4, c5 }) sb.Append((char)(c + 0x30));
        sb.Append((char)('0' + (q[5] >> 4))).Append((char)('0' + (q[5] & 15)));
        sb.Append((char)('0' + (q[6] >> 4))).Append((char)('0' + (q[6] & 15)));
        sb.Append((char)('0' + (q[7] >> 4))).Append((char)('0' + (q[7] & 15)));
        sb.Append((char)('0' + (q[8] >> 4)));
        return sb.ToString();
    }

    private static int NearestTrack(SortedDictionary<int, long> starts, long sector)
    {
        int best = 0;
        foreach (var (trk, start) in starts)
            if (start <= sector) best = trk;
        return best;
    }

    // ---- CD-TEXT -----------------------------------------------------------

    private static (string?, string?, List<string?>, int, int) DecodeCdText(
        Stream image, int size, RawSubcodeForm form, long sectors)
    {
        Span<byte> sub = stackalloc byte[96];
        Span<byte> rw = stackalloc byte[96];
        Span<byte> bytes = stackalloc byte[72];
        var packsByKey = new Dictionary<(byte type, byte seq), byte[]>();
        int good = 0, bad = 0;

        for (long s = 0; s < sectors; s++)
        {
            image.Position = s * size + 2352;
            image.ReadExactly(sub[..(size - 2352)]);
            SubcodeFrame.ExtractRw(sub[..(size - 2352)], form, rw);

            // 96 symbols → 72 bytes → four 18-byte packs.
            for (int g = 0; g < 24; g++)
            {
                int a = rw[g * 4], b = rw[g * 4 + 1], c = rw[g * 4 + 2], d = rw[g * 4 + 3];
                bytes[g * 3] = (byte)((a << 2) | (b >> 4));
                bytes[g * 3 + 1] = (byte)(((b & 0xF) << 4) | (c >> 2));
                bytes[g * 3 + 2] = (byte)(((c & 0x3) << 6) | d);
            }
            for (int p = 0; p < 4; p++)
            {
                var pack = bytes.Slice(p * 18, 18);
                if (pack[0] is < 0x80 or > 0x8F) continue;    // not CD-TEXT
                if (Crc16.ComputeInverted(pack[..16]) != (ushort)((pack[16] << 8) | pack[17]))
                { bad++; continue; }
                good++;
                packsByKey[((byte)pack[0], pack[2])] = pack.ToArray();
            }
        }

        List<string> TextOf(byte type)
        {
            var data = packsByKey.Where(kv => kv.Key.type == type)
                .OrderBy(kv => kv.Key.seq)
                .SelectMany(kv => kv.Value.Skip(4).Take(12))
                .ToArray();
            return Encoding.Latin1.GetString(data).Split('\0')
                .Select(x => x.TrimEnd('\0')).ToList();
        }

        string? album = null, performer = null;
        var titles = new List<string?>();
        if (packsByKey.Keys.Any(k => k.type == 0x80))
        {
            var t = TextOf(0x80);
            album = t.ElementAtOrDefault(0);
            titles = t.Skip(1).Where(x => x.Length > 0).Select(x => (string?)x).ToList();
        }
        if (packsByKey.Keys.Any(k => k.type == 0x81))
            performer = TextOf(0x81).ElementAtOrDefault(0);

        return (Emptied(album), Emptied(performer), titles, good, bad);
    }

    private static string? Emptied(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
