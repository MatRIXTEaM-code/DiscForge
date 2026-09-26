// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.OldArchives;

namespace DiscForge.Core.Protect;

/// <summary>How much protection to add.</summary>
public enum ParityLevel
{
    /// <summary>8 parity sectors per 247 (≈3%): survives about 3% of the image lost.</summary>
    Low = 8,
    /// <summary>16 per 239 (≈7%).</summary>
    Normal = 16,
    /// <summary>32 per 223 (≈14%): the level dvdisaster uses by default.</summary>
    High = 32,
}

/// <summary>Header of a DiscForge parity file (<c>.dfpar</c>).</summary>
public sealed record ParityHeader
{
    public const string Magic = "DFPARITY";
    public const int Version = 1;
    public const int HeaderBytes = 128;

    public required int SectorSize { get; init; }
    public required long ImageLength { get; init; }
    public required long DataSectors { get; init; }
    /// <summary>Data sectors per stripe.</summary>
    public required int K { get; init; }
    /// <summary>Parity sectors per stripe.</summary>
    public required int P { get; init; }
    public required long Stripes { get; init; }
    public required DateTime CreatedUtc { get; init; }

    public long ParitySectors => Stripes * P;
    public long DataCrcOffset => HeaderBytes;
    public long ParityCrcOffset => DataCrcOffset + DataSectors * 4;
    public long ParityDataOffset => Align(ParityCrcOffset + ParitySectors * 4, 4096);
    public long FileLength => ParityDataOffset + ParitySectors * SectorSize;
    public double Overhead => (double)P / K;

    /// <summary>Stripe that data sector <paramref name="i"/> belongs to, and its row in the stripe.</summary>
    public (long Stripe, int Row) Place(long i) => (i % Stripes, (int)(i / Stripes));
    /// <summary>Data sector at <paramref name="row"/> of <paramref name="stripe"/> (may be past the image: virtual zero).</summary>
    public long DataIndex(long stripe, int row) => row * Stripes + stripe;
    public long ParityOffset(long stripe, int j) => ParityDataOffset + ((long)j * Stripes + stripe) * SectorSize;

    private static long Align(long v, long a) => (v + a - 1) / a * a;

    public byte[] ToBytes()
    {
        var b = new byte[HeaderBytes];
        System.Text.Encoding.ASCII.GetBytes(Magic).CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), SectorSize);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), ImageLength);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), DataSectors);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), K);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(36), P);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), Stripes);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), new DateTimeOffset(CreatedUtc).ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(124), Crc32Std.Compute(b.AsSpan(0, 124)));
        return b;
    }

    public static ParityHeader Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderBytes || System.Text.Encoding.ASCII.GetString(b[..8]) != Magic)
            throw new InvalidDataException("Not a DiscForge parity file.");
        if (Crc32Std.Compute(b[..124]) != BinaryPrimitives.ReadUInt32LittleEndian(b[124..]))
            throw new InvalidDataException("The parity file's header is damaged.");
        int ver = BinaryPrimitives.ReadInt32LittleEndian(b[8..]);
        if (ver != Version) throw new InvalidDataException($"Parity file version {ver} isn't supported.");
        var h = new ParityHeader
        {
            SectorSize = BinaryPrimitives.ReadInt32LittleEndian(b[12..]),
            ImageLength = BinaryPrimitives.ReadInt64LittleEndian(b[16..]),
            DataSectors = BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
            K = BinaryPrimitives.ReadInt32LittleEndian(b[32..]),
            P = BinaryPrimitives.ReadInt32LittleEndian(b[36..]),
            Stripes = BinaryPrimitives.ReadInt64LittleEndian(b[40..]),
            CreatedUtc = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(b[48..])).UtcDateTime,
        };
        if (h.SectorSize is < 512 or > 65536 || h.K < 1 || h.P < 1 || h.K + h.P > 255 || h.Stripes < 1 ||
            h.DataSectors > h.Stripes * h.K || h.DataSectors * h.SectorSize < h.ImageLength)
            throw new InvalidDataException("The parity file's header has impossible values.");
        return h;
    }
}

public sealed record ProtectProgress(string Stage, long Done, long Total)
{
    public double Fraction => Total == 0 ? 1 : (double)Done / Total;
}

/// <summary>Result of checking an image against its parity file.</summary>
public sealed record ParityCheck(
    long DataSectors,
    long DamagedData,
    long DamagedParity,
    long StripesDamaged,
    long StripesUnrepairable,
    IReadOnlyList<long> DamagedSectors,
    bool LengthMismatch)
{
    public bool Intact => DamagedData == 0 && DamagedParity == 0 && !LengthMismatch;
    public bool Repairable => StripesUnrepairable == 0;

    public string Summary()
    {
        if (Intact) return $"All {DataSectors:N0} sectors are intact.";
        var s = new List<string>();
        if (DamagedData > 0) s.Add($"{DamagedData:N0} damaged sector(s) in the image");
        if (DamagedParity > 0) s.Add($"{DamagedParity:N0} in the parity file");
        if (LengthMismatch) s.Add("the image is the wrong size");
        return string.Join(", ", s) + (Repairable ? " — all repairable." : $" — {StripesUnrepairable:N0} group(s) have too much damage to repair.");
    }
}

public sealed record ParityRepair(ParityCheck Before, long SectorsRepaired, long ParityRepaired, long StillDamaged)
{
    public bool Complete => StillDamaged == 0;
}

/// <summary>
/// Reed-Solomon parity files for disc images, in the spirit of dvdisaster's error-correction files:
/// made while an image is good, they rebuild sectors that are later lost to bit-rot, a failing drive,
/// or a disc that could only be rescued partly. The image's sectors are split into interleaved groups
/// ("stripes": sector i goes to stripe i mod S), so a long run of damage — a scratch — is spread over
/// many groups; each group has P parity sectors and survives any P of its sectors being lost. Damaged
/// sectors are found by the CRC-32 stored for every sector, so they are repaired as erasures, which
/// is what lets P parity sectors fix P losses. The image itself is never changed except to write back
/// repaired sectors.
/// </summary>
public static class ParityFile
{
    public const string Extension = ".dfpar";
    public static string DefaultPath(string image) => image + Extension;

    /// <summary>Memory for parity accumulation per batch (bounds RAM whatever the image size).</summary>
    private const long BatchBytes = 64L << 20;

    private static byte[] Generator(int p)
    {
        // g(x) = Π_{i=0}^{p-1} (x - α^i); coefficients g[0..p], g[p] = 1 (highest power).
        var g = new byte[p + 1];
        g[0] = 1;
        for (int i = 0; i < p; i++)
        {
            byte root = Gf256.Pow(i);
            for (int j = i + 1; j >= 1; j--) g[j] = (byte)(g[j - 1] ^ Gf256.Mul(g[j], root));
            g[0] = Gf256.Mul(g[0], root);
        }
        return g;
    }

    // ------------------------------------------------------------------------------ create

    public static ParityHeader Create(string imagePath, string parityPath, ParityLevel level = ParityLevel.Normal,
        int sectorSize = 2048, IProgress<ProtectProgress>? progress = null, CancellationToken ct = default)
        => Create(imagePath, parityPath, (int)level, sectorSize, progress, ct);

    public static ParityHeader Create(string imagePath, string parityPath, int paritySectors,
        int sectorSize = 2048, IProgress<ProtectProgress>? progress = null, CancellationToken ct = default)
    {
        int p = Math.Clamp(paritySectors, 2, 128);
        int k = 255 - p;
        using var img = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        long len = img.Length;
        if (len == 0) throw new InvalidDataException("The image is empty.");
        long n = (len + sectorSize - 1) / sectorSize;
        long stripes = (n + k - 1) / k;
        // Small images: use fewer data rows so every stripe is full-length where possible.
        int kUsed = (int)Math.Min(k, (n + stripes - 1) / stripes);
        var h = new ParityHeader
        {
            SectorSize = sectorSize, ImageLength = len, DataSectors = n, K = kUsed, P = p, Stripes = stripes,
            CreatedUtc = DateTime.UtcNow,
        };
        var g = Generator(p);
        string tmp = parityPath + ".tmp";
        try
        {
            using (var par = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                par.SetLength(h.FileLength);
                par.Position = 0;
                par.Write(h.ToBytes());
                var dataCrc = new uint[n];
                int batch = (int)Math.Clamp(BatchBytes / ((long)p * sectorSize), 1, stripes);
                var block = new byte[(long)batch * p * sectorSize];
                var row = new byte[(long)batch * sectorSize];
                var fb = new byte[sectorSize];
                long done = 0;
                for (long s0 = 0; s0 < stripes; s0 += batch)
                {
                    int bs = (int)Math.Min(batch, stripes - s0);
                    Array.Clear(block, 0, bs * p * sectorSize);
                    var baseIdx = new int[bs];   // circular register base per stripe
                    for (int r = 0; r < kUsed; r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Row r of stripes [s0, s0+bs) is data sectors [r*S + s0, r*S + s0 + bs): contiguous.
                        long first = h.DataIndex(s0, r);
                        int avail = (int)Math.Clamp(n - first, 0, bs);
                        var rowSpan = row.AsSpan(0, bs * sectorSize);
                        rowSpan.Clear();
                        if (avail > 0)
                        {
                            img.Position = first * sectorSize;
                            ReadFull(img, rowSpan[..(int)Math.Min((long)avail * sectorSize, len - first * sectorSize)]);
                            for (int t = 0; t < avail; t++) dataCrc[first + t] = Crc32Std.Compute(rowSpan.Slice(t * sectorSize, sectorSize));
                        }
                        for (int t = 0; t < bs; t++)
                        {
                            var m = rowSpan.Slice(t * sectorSize, sectorSize);
                            var regBase = block.AsSpan(t * p * sectorSize, p * sectorSize);
                            int b = baseIdx[t];
                            // fb = m ^ r[P-1]; shift; r[j] ^= g_j·fb (j ≥ 1); r[0] = g_0·fb.
                            var top = regBase.Slice(((b + p - 1) % p) * sectorSize, sectorSize);
                            m.CopyTo(fb);
                            Gf256.MulAdd(fb, top, 1);
                            b = (b + p - 1) % p;   // old r[P-1] slot becomes new r[0]
                            baseIdx[t] = b;
                            Gf256.Mul(regBase.Slice(b * sectorSize, sectorSize), fb, g[0]);
                            for (int j = 1; j < p; j++)
                                Gf256.MulAdd(regBase.Slice(((b + j) % p) * sectorSize, sectorSize), fb, g[j]);
                        }
                    }
                    // Parity symbol order: p_0 = r[P-1] … p_{P-1} = r[0] (highest power first).
                    for (int j = 0; j < p; j++)
                    {
                        var outRow = row.AsSpan(0, bs * sectorSize);
                        for (int t = 0; t < bs; t++)
                        {
                            int b = baseIdx[t];
                            block.AsSpan(t * p * sectorSize + ((b + p - 1 - j) % p) * sectorSize, sectorSize)
                                .CopyTo(outRow.Slice(t * sectorSize, sectorSize));
                        }
                        par.Position = h.ParityOffset(s0, j);
                        par.Write(outRow);
                        var crcBuf = new byte[bs * 4];
                        for (int t = 0; t < bs; t++)
                            BinaryPrimitives.WriteUInt32LittleEndian(crcBuf.AsSpan(t * 4), Crc32Std.Compute(outRow.Slice(t * sectorSize, sectorSize)));
                        par.Position = h.ParityCrcOffset + ((long)j * stripes + s0) * 4;
                        par.Write(crcBuf);
                    }
                    done += bs;
                    progress?.Report(new ProtectProgress("Creating parity", done, stripes));
                }
                var crcBytes = new byte[n * 4];
                for (long i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(crcBytes.AsSpan((int)(i * 4)), dataCrc[i]);
                par.Position = h.DataCrcOffset;
                par.Write(crcBytes);
            }
            File.Move(tmp, parityPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return h;
    }

    // ------------------------------------------------------------------------------ verify / repair

    public static ParityHeader ReadHeader(string parityPath)
    {
        using var fs = File.OpenRead(parityPath);
        var b = new byte[ParityHeader.HeaderBytes];
        ReadFull(fs, b);
        var h = ParityHeader.Parse(b);
        if (fs.Length < h.FileLength) throw new InvalidDataException("The parity file is truncated.");
        return h;
    }

    public static ParityCheck Verify(string imagePath, string parityPath, IProgress<ProtectProgress>? progress = null, CancellationToken ct = default)
    {
        var (check, _, _) = Scan(imagePath, parityPath, progress, ct);
        return check;
    }

    /// <summary>Find and rebuild damaged sectors, writing them back into the image (and parity file).</summary>
    public static ParityRepair Repair(string imagePath, string parityPath, IProgress<ProtectProgress>? progress = null, CancellationToken ct = default)
    {
        var (before, badData, badParity) = Scan(imagePath, parityPath, progress, ct);
        if (before.Intact) return new ParityRepair(before, 0, 0, 0);
        var h = ReadHeader(parityPath);
        int ss = h.SectorSize, k = h.K, p = h.P, n = k + p;
        using var img = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.RandomAccess);
        if (img.Length < h.ImageLength) img.SetLength(h.ImageLength);
        using var par = new FileStream(parityPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.RandomAccess);

        // Group the damage by stripe.
        var byStripe = new SortedDictionary<long, List<int>>();   // stripe -> codeword positions (0..n-1)
        foreach (var i in badData) { var (s, r) = h.Place(i); Add(byStripe, s, r); }
        foreach (var q in badParity) { long s = q % h.Stripes; int j = (int)(q / h.Stripes); Add(byStripe, s, k + j); }

        long fixedData = 0, fixedParity = 0, still = 0, done = 0;
        var word = new byte[n][];
        for (int i = 0; i < n; i++) word[i] = new byte[ss];
        foreach (var (stripe, erasures) in byStripe)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new ProtectProgress("Repairing", done, byStripe.Count));
            if (erasures.Count > p)
            {
                still += erasures.Count(e => e < k);
                continue;
            }
            // Load the codeword (erasures as zero; rows past the image are virtual zeros).
            for (int pos = 0; pos < n; pos++)
            {
                Array.Clear(word[pos]);
                if (erasures.Contains(pos)) continue;
                if (pos < k) ReadDataSector(img, h, h.DataIndex(stripe, pos), word[pos]);
                else { par.Position = h.ParityOffset(stripe, pos - k); ReadFull(par, word[pos]); }
            }
            var values = SolveErasures(word, erasures, p, ss);
            for (int e = 0; e < erasures.Count; e++)
            {
                int pos = erasures[e];
                if (pos < k)
                {
                    long di = h.DataIndex(stripe, pos);
                    long off = di * ss;
                    int len = (int)Math.Min(ss, h.ImageLength - off);
                    img.Position = off;
                    img.Write(values[e], 0, len);
                    fixedData++;
                }
                else
                {
                    par.Position = h.ParityOffset(stripe, pos - k);
                    par.Write(values[e]);
                    fixedParity++;
                }
            }
        }
        if (img.Length > h.ImageLength) img.SetLength(h.ImageLength);
        img.Flush();
        par.Flush();
        return new ParityRepair(before, fixedData, fixedParity, still);

        static void Add(SortedDictionary<long, List<int>> d, long s, int pos)
        {
            if (!d.TryGetValue(s, out var l)) d[s] = l = new List<int>();
            l.Add(pos);
        }
    }

    private static (ParityCheck Check, List<long> BadData, List<long> BadParity) Scan(
        string imagePath, string parityPath, IProgress<ProtectProgress>? progress, CancellationToken ct)
    {
        var h = ReadHeader(parityPath);
        int ss = h.SectorSize;
        using var par = new FileStream(parityPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var dataCrc = new byte[h.DataSectors * 4];
        par.Position = h.DataCrcOffset;
        ReadFull(par, dataCrc);
        var parCrc = new byte[h.ParitySectors * 4];
        par.Position = h.ParityCrcOffset;
        ReadFull(par, parCrc);

        var badData = new List<long>();
        var badParity = new List<long>();
        bool lengthMismatch;
        long total = h.DataSectors + h.ParitySectors;
        using (var img = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        {
            lengthMismatch = img.Length != h.ImageLength;
            var buf = new byte[ss];
            for (long i = 0; i < h.DataSectors; i++)
            {
                if ((i & 1023) == 0) { ct.ThrowIfCancellationRequested(); progress?.Report(new ProtectProgress("Checking the image", i, total)); }
                bool ok;
                try
                {
                    Array.Clear(buf);
                    img.Position = i * ss;
                    int want = (int)Math.Min(ss, h.ImageLength - i * ss);
                    int got = img.Read(buf, 0, want);
                    while (got < want) { int m = img.Read(buf, got, want - got); if (m <= 0) break; got += m; }
                    ok = got == want && Crc32Std.Compute(buf) == BinaryPrimitives.ReadUInt32LittleEndian(dataCrc.AsSpan((int)(i * 4)));
                }
                catch (IOException) { ok = false; }   // unreadable (a failing disk) counts as damaged
                if (!ok) badData.Add(i);
            }
        }
        {
            var buf = new byte[ss];
            par.Position = h.ParityDataOffset;
            for (long q = 0; q < h.ParitySectors; q++)
            {
                if ((q & 1023) == 0) { ct.ThrowIfCancellationRequested(); progress?.Report(new ProtectProgress("Checking the parity file", h.DataSectors + q, total)); }
                ReadFull(par, buf);
                if (Crc32Std.Compute(buf) != BinaryPrimitives.ReadUInt32LittleEndian(parCrc.AsSpan((int)(q * 4)))) badParity.Add(q);
            }
        }
        var perStripe = new Dictionary<long, int>();
        foreach (var i in badData) { long s = i % h.Stripes; perStripe[s] = perStripe.GetValueOrDefault(s) + 1; }
        foreach (var q in badParity) { long s = q % h.Stripes; perStripe[s] = perStripe.GetValueOrDefault(s) + 1; }
        long unrepairable = perStripe.Values.Count(c => c > h.P);
        progress?.Report(new ProtectProgress("Checked", total, total));
        return (new ParityCheck(h.DataSectors, badData.Count, badParity.Count, perStripe.Count, unrepairable, badData, lengthMismatch), badData, badParity);
    }

    private static void ReadDataSector(FileStream img, ParityHeader h, long index, byte[] dest)
    {
        if (index >= h.DataSectors) return;   // virtual zero row
        long off = index * h.SectorSize;
        int want = (int)Math.Min(h.SectorSize, h.ImageLength - off);
        if (off >= img.Length) return;
        img.Position = off;
        int got = 0;
        while (got < want) { int m = img.Read(dest, got, want - got); if (m <= 0) break; got += m; }
    }

    /// <summary>
    /// Erasure decoding of one codeword whose positions are symbol vectors (one byte per sector
    /// offset). Codeword position i has degree n-1-i; the code's roots are α^0..α^(P-1).
    /// Returns the values of the erased positions, in the order given.
    /// </summary>
    internal static byte[][] SolveErasures(byte[][] word, IReadOnlyList<int> erasures, int p, int ss)
    {
        int n = word.Length, e = erasures.Count;
        // Syndromes S_j = Σ_i r_i · α^{j(n-1-i)}.
        var synd = new byte[p][];
        for (int j = 0; j < p; j++)
        {
            synd[j] = new byte[ss];
            for (int i = 0; i < n; i++) Gf256.MulAdd(synd[j], word[i], Gf256.Pow(j * (n - 1 - i)));
        }
        // Erasure locators X_k = α^{n-1-i_k}; Λ(x) = Π (1 + X_k x).
        var X = erasures.Select(i => Gf256.Pow(n - 1 - i)).ToArray();
        var lambda = new byte[e + 1];
        lambda[0] = 1;
        for (int kx = 0; kx < e; kx++)
            for (int d = kx + 1; d >= 1; d--) lambda[d] ^= Gf256.Mul(lambda[d - 1], X[kx]);
        // Forney (first root α^0): value_k = X_k · Ω(X_k⁻¹) / Λ'(X_k⁻¹), with Ω = S·Λ mod x^P.
        // Ω(X⁻¹) is linear in the syndromes: Σ_j S_j · Σ_l Λ_l X^{-(j+l)} over j+l < P.
        var result = new byte[e][];
        for (int kx = 0; kx < e; kx++)
        {
            byte xi = X[kx], xinv = Gf256.Inv(xi);
            // Λ'(x) in characteristic 2: only odd terms survive.
            byte deriv = 0;
            for (int d = 1; d <= e; d += 2) deriv ^= Gf256.Mul(lambda[d], PowOf(xinv, d - 1));
            byte scale = Gf256.Div(xi, deriv);
            var v = new byte[ss];
            for (int j = 0; j < p; j++)
            {
                byte w = 0;
                for (int l = 0; l <= e && j + l < p; l++) w ^= Gf256.Mul(lambda[l], PowOf(xinv, j + l));
                Gf256.MulAdd(v, synd[j], Gf256.Mul(w, scale));
            }
            result[kx] = v;
        }
        return result;
    }

    private static byte PowOf(byte x, int e)
    {
        if (e == 0) return 1;
        if (x == 0) return 0;
        return Gf256.Exp[(Gf256.Log[x] * e) % 255];
    }

    private static void ReadFull(Stream s, Span<byte> buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf[got..]);
            if (n <= 0) throw new EndOfStreamException("File ended early.");
            got += n;
        }
    }
}
