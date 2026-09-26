// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Security.Cryptography;
using DiscForge.Core.Protect;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Reed-Solomon parity files: damage an image in different ways and repair it.</summary>
public class ParityFileTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "df-par-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static byte[] RandomImage(int bytes, int seed)
    {
        var b = new byte[bytes];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static string Sha(string path) => System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Gf256_basics()
    {
        for (int a = 1; a < 256; a++)
        {
            Assert.Equal(1, Gf256.Mul((byte)a, Gf256.Inv((byte)a)));
            Assert.Equal((byte)a, Gf256.Div(Gf256.Mul((byte)a, 29), 29));
        }
        // The vector path must agree with the table path.
        var src = RandomImage(1000, 1);
        foreach (byte c in new byte[] { 2, 3, 29, 142, 255 })
        {
            var fast = new byte[1000];
            Gf256.MulAdd(fast, src, c);
            for (int i = 0; i < 1000; i++) Assert.Equal(Gf256.Mul(c, src[i]), fast[i]);
        }
    }

    [Theory]
    [InlineData(ParityLevel.Low)]
    [InlineData(ParityLevel.Normal)]
    [InlineData(ParityLevel.High)]
    public void Repairs_scattered_and_burst_damage(ParityLevel level)
    {
        var dir = TempDir();
        try
        {
            string img = Path.Combine(dir, "disc.iso"), par = img + ".dfpar";
            File.WriteAllBytes(img, RandomImage(2048 * 3000 + 777, 7));   // not a whole number of sectors
            string good = Sha(img);
            var h = ParityFile.Create(img, par, level);
            Assert.True(ParityFile.Verify(img, par).Intact);

            // A scratch: a run of consecutive sectors, spread over the stripes by the interleave.
            int burst = (int)(h.Stripes * (h.P - 2));
            var bytes = File.ReadAllBytes(img);
            Array.Clear(bytes, 2048 * 1000, Math.Min(burst, 1990) * 2048);
            // A few scattered bit flips, including the partial last sector.
            bytes[5] ^= 0x10;
            bytes[^3] ^= 0x01;
            File.WriteAllBytes(img, bytes);

            var check = ParityFile.Verify(img, par);
            Assert.False(check.Intact);
            Assert.True(check.Repairable, check.Summary());
            var rep = ParityFile.Repair(img, par);
            Assert.True(rep.Complete);
            Assert.Equal(good, Sha(img));
            Assert.True(ParityFile.Verify(img, par).Intact);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Repairs_damage_in_the_parity_file_too()
    {
        var dir = TempDir();
        try
        {
            string img = Path.Combine(dir, "disc.iso"), par = img + ".dfpar";
            File.WriteAllBytes(img, RandomImage(2048 * 500, 3));
            var h = ParityFile.Create(img, par, ParityLevel.Normal);
            string goodPar = Sha(par);
            var pb = File.ReadAllBytes(par);
            pb[(int)h.ParityDataOffset + 100] ^= 0xFF;   // one parity sector
            File.WriteAllBytes(par, pb);
            var ib = File.ReadAllBytes(img);
            ib[2048 * 10] ^= 0xFF;
            File.WriteAllBytes(img, ib);

            var check = ParityFile.Verify(img, par);
            Assert.Equal(1, check.DamagedData);
            Assert.Equal(1, check.DamagedParity);
            var rep = ParityFile.Repair(img, par);
            Assert.True(rep.Complete);
            Assert.Equal(1, rep.ParityRepaired);
            Assert.True(ParityFile.Verify(img, par).Intact);
            Assert.Equal(goodPar, Sha(par));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Too_much_damage_is_reported_not_guessed()
    {
        var dir = TempDir();
        try
        {
            string img = Path.Combine(dir, "disc.iso"), par = img + ".dfpar";
            File.WriteAllBytes(img, RandomImage(2048 * 2000, 5));
            var h = ParityFile.Create(img, par, ParityLevel.Low);
            var b = File.ReadAllBytes(img);
            // Destroy P+1 sectors of stripe 0 (rows 0..P).
            for (int r = 0; r <= h.P; r++) b[(int)(h.DataIndex(0, r) * 2048)] ^= 1;
            File.WriteAllBytes(img, b);
            var check = ParityFile.Verify(img, par);
            Assert.False(check.Repairable);
            var rep = ParityFile.Repair(img, par);
            Assert.False(rep.Complete);
            Assert.Equal(h.P + 1, rep.StillDamaged);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Tiny_and_truncated_images()
    {
        var dir = TempDir();
        try
        {
            string img = Path.Combine(dir, "tiny.bin"), par = img + ".dfpar";
            File.WriteAllBytes(img, RandomImage(5000, 9));
            string good = Sha(img);
            ParityFile.Create(img, par, ParityLevel.High);
            // Cut the end off: missing sectors are damage like any other.
            File.WriteAllBytes(img, File.ReadAllBytes(img)[..3000]);
            var check = ParityFile.Verify(img, par);
            Assert.True(check.LengthMismatch);
            Assert.True(ParityFile.Repair(img, par).Complete);
            Assert.Equal(good, Sha(img));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Solver_recovers_any_P_erasures()
    {
        // Direct check of the erasure decoder on random codewords of one byte per symbol.
        var rnd = new Random(11);
        foreach (int p in new[] { 2, 8, 16, 32 })
        {
            int k = 255 - p, n = k + p;
            for (int trial = 0; trial < 5; trial++)
            {
                string d = TempDir();
                try
                {
                    string img = Path.Combine(d, "x.bin"), par = img + ".dfpar";
                    File.WriteAllBytes(img, RandomImage(2048 * k, trial + p));
                    var h = ParityFile.Create(img, par, p);
                    var b = File.ReadAllBytes(img);
                    foreach (var row in Enumerable.Range(0, k).OrderBy(_ => rnd.Next()).Take(p))
                        b[(int)(h.DataIndex(0, row) * 2048) + rnd.Next(2048)] ^= (byte)(1 + rnd.Next(255));
                    File.WriteAllBytes(img, b);
                    Assert.True(ParityFile.Repair(img, par).Complete);
                    Assert.True(ParityFile.Verify(img, par).Intact);
                }
                finally { Directory.Delete(d, true); }
            }
        }
    }
}
