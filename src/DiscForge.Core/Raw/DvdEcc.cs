// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Raw;

/// <summary>The outcome of correcting a DVD ECC block.</summary>
public sealed record DvdEccResult
{
    public required bool Corrected { get; init; }
    public required int PiRowsCorrected { get; init; }
    public required int PoColumnsCorrected { get; init; }
    public required int UncorrectableRows { get; init; }
    public required int Passes { get; init; }

    public string Summary() => Corrected
        ? $"ECC block corrected in {Passes} pass(es): {PiRowsCorrected} PI row(s), {PoColumnsCorrected} PO column(s) repaired."
        : $"ECC block NOT fully correctable after {Passes} pass(es): {UncorrectableRows} row(s) still fail PI.";
}

/// <summary>
/// The DVD sector-layer error correction (RS-PC), the DVD analogue of the CD RSPC that <c>EccCorrector</c>
/// already does. A DVD ECC block is a logical 208×182 byte array: 192 rows × 172 bytes of data, an inner code
/// PI = RS(182,172) protecting each row (10 parity bytes), and an outer code PO = RS(208,192) protecting each
/// column (16 parity rows). Because it is a PRODUCT code, a row the inner code cannot fix is passed to the outer
/// code as a whole-row erasure — and 16 parity rows correct up to 16 erased rows — so damage that no single code
/// survives is recovered by alternating PI and PO passes. This reuses DiscForge's validated GF(2^8) Reed-Solomon
/// engine (the same field, 0x11D, as the CD codes). Pure integrity repair from the disc's own parity; it defeats nothing.
///
/// Validation note: the RS-PC math here is validated software-first by round-trip against DiscForge's own RS
/// encoder (encode a block, injure it beyond the inner code, correct it, get the original back). It operates on
/// the LOGICAL ECC block. Assembling that logical block from a specific dumper's raw byte stream (the physical
/// recording interleave in ECMA-267) is the one step not verifiable in this environment against a real DVD dump;
/// that mapping should be confirmed against a real raw ECC block before relying on it for real-disc repair.
/// </summary>
public static class DvdEcc
{
    public const int DataRows = 192;
    public const int DataCols = 172;
    public const int PiParity = 10;                 // inner code: RS(182,172)
    public const int PoParity = 16;                 // outer code: RS(208,192)
    public const int Rows = DataRows + PoParity;    // 208
    public const int Cols = DataCols + PiParity;    // 182
    public const int BlockBytes = Rows * Cols;      // 37,856

    private static readonly ReedSolomonGf256 Pi = new(Cols, DataCols);   // RS(182,172)
    private static readonly ReedSolomonGf256 Po = new(Rows, DataRows);   // RS(208,192)

    /// <summary>Build a full 208×182 ECC block from a 192×172 (33,024-byte) data array — for round-trip validation.</summary>
    public static byte[] EncodeBlock(ReadOnlySpan<byte> data)
    {
        if (data.Length != DataRows * DataCols)
            throw new ArgumentException($"DVD ECC data is {DataRows * DataCols} bytes (192×172).", nameof(data));

        var block = new byte[BlockBytes];
        for (int r = 0; r < DataRows; r++)
            data.Slice(r * DataCols, DataCols).CopyTo(block.AsSpan(r * Cols, DataCols));

        var row = new byte[DataCols];
        void PiRow(int r)
        {
            for (int c = 0; c < DataCols; c++) row[c] = block[r * Cols + c];
            var cw = Pi.Encode(row);
            for (int c = DataCols; c < Cols; c++) block[r * Cols + c] = cw[c];
        }

        // 1) PI on the 192 data rows → their 10 parity bytes (cols 172..181).
        for (int r = 0; r < DataRows; r++) PiRow(r);

        // 2) PO on ALL 182 columns (data AND PI columns) over the 192 data rows → 16 parity rows (192..207).
        var col = new byte[DataRows];
        for (int c = 0; c < Cols; c++)
        {
            for (int r = 0; r < DataRows; r++) col[r] = block[r * Cols + c];
            var cw = Po.Encode(col);
            for (int r = DataRows; r < Rows; r++) block[r * Cols + c] = cw[r];
        }

        // 3) PI on the 16 PO rows → their own 10 parity bytes, so every row is a valid inner codeword.
        for (int r = DataRows; r < Rows; r++) PiRow(r);
        return block;
    }

    /// <summary>Extract the 192×172 user-data array from a (corrected) ECC block.</summary>
    public static byte[] ExtractData(ReadOnlySpan<byte> block)
    {
        if (block.Length != BlockBytes) throw new ArgumentException($"A DVD ECC block is {BlockBytes} bytes.", nameof(block));
        var data = new byte[DataRows * DataCols];
        for (int r = 0; r < DataRows; r++)
            block.Slice(r * Cols, DataCols).CopyTo(data.AsSpan(r * DataCols, DataCols));
        return data;
    }

    /// <summary>
    /// Correct a 208×182 ECC block in place, alternating the inner (PI, per row) and outer (PO, per column) codes,
    /// with rows the inner code cannot fix handed to the outer code as erasures.
    /// </summary>
    public static DvdEccResult Correct(byte[] block, int maxPasses = 8)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Length != BlockBytes) throw new ArgumentException($"A DVD ECC block is {BlockBytes} bytes.", nameof(block));

        int piFixed = 0, poFixed = 0, passes = 0;
        var badRows = new List<int>();
        var rowBuf = new byte[Cols];
        var colBuf = new byte[Rows];

        for (int pass = 0; pass < maxPasses; pass++)
        {
            passes++;
            bool changed = false;
            badRows.Clear();

            // PI pass — correct each row as RS(182,172).
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++) rowBuf[c] = block[r * Cols + c];
                if (Pi.TryDecode(rowBuf, out var corr))
                {
                    if (WasChanged(corr, rowBuf))
                    {
                        for (int c = 0; c < Cols; c++) block[r * Cols + c] = corr[c];
                        changed = true;
                        piFixed++;
                    }
                }
                else badRows.Add(r);
            }

            // PO pass — correct all 182 columns as RS(208,192), erasing rows PI could not fix.
            IReadOnlyList<int>? erasures = badRows.Count is > 0 and <= PoParity ? badRows.ToArray() : null;
            for (int c = 0; c < Cols; c++)
            {
                for (int r = 0; r < Rows; r++) colBuf[r] = block[r * Cols + c];
                if (Po.TryDecode(colBuf, out var corr, erasures))
                {
                    bool colChanged = false;
                    for (int r = 0; r < Rows; r++)
                        if (block[r * Cols + c] != corr[r]) { block[r * Cols + c] = corr[r]; changed = true; colChanged = true; }
                    if (colChanged) poFixed++;
                }
            }

            if (!changed)
                return new DvdEccResult
                {
                    Corrected = badRows.Count == 0, PiRowsCorrected = piFixed, PoColumnsCorrected = poFixed,
                    UncorrectableRows = badRows.Count, Passes = passes,
                };
        }

        // Final PI recheck to report residual bad rows.
        badRows.Clear();
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++) rowBuf[c] = block[r * Cols + c];
            if (!Pi.TryDecode(rowBuf, out _)) badRows.Add(r);
        }
        return new DvdEccResult
        {
            Corrected = badRows.Count == 0, PiRowsCorrected = piFixed, PoColumnsCorrected = poFixed,
            UncorrectableRows = badRows.Count, Passes = passes,
        };
    }

    private static bool WasChanged(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return true;
        return false;
    }
}
