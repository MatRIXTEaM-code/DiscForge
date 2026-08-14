using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class PreservationVaultTests
{
    private static byte[] Image(int len, int seed = 1)
    {
        var b = new byte[len];
        new System.Random(seed).NextBytes(b);
        return b;
    }

    // Corrupt a block by flipping bytes (keeps the length, so only the hash catches it).
    private static void Corrupt(PreservationVault v, int index)
    {
        var bytes = System.Convert.FromBase64String(v.Blocks[index]!);
        for (int i = 0; i < bytes.Length; i++) bytes[i] ^= 0xFF;
        v.Blocks[index] = System.Convert.ToBase64String(bytes);
    }

    // Lose a block entirely.
    private static void Erase(PreservationVault v, int index) => v.Blocks[index] = "";

    [Fact]
    public void An_undamaged_vault_heals_to_the_original()
    {
        var img = Image(8123);
        var vault = PreservationVaultOps.Create(img, parityBlocks: 4, dataBlocks: 8);
        Assert.True(PreservationVaultOps.Check(vault).Pristine);

        var healed = PreservationVaultOps.Heal(vault, out var report);
        Assert.True(report.ImageValid);
        Assert.Equal(img, healed);
    }

    [Fact]
    public void It_heals_up_to_parity_count_damaged_blocks()
    {
        var img = Image(20000, seed: 7);
        var vault = PreservationVaultOps.Create(img, parityBlocks: 4, dataBlocks: 8);

        Corrupt(vault, 0);   // data block, altered bytes
        Erase(vault, 3);     // data block, missing
        Corrupt(vault, 8);   // parity block
        Erase(vault, 11);    // parity block  → 4 damaged = the parity budget

        var health = PreservationVaultOps.Check(vault);
        Assert.Equal(4, health.DamagedBlocks.Count);
        Assert.True(health.Recoverable);

        var healed = PreservationVaultOps.Heal(vault, out var report);
        Assert.True(report.Recovered);
        Assert.True(report.ImageValid);
        Assert.Equal(4, report.RepairedBlocks.Count);
        Assert.Equal(img, healed);

        // After healing, the vault is pristine again.
        Assert.True(PreservationVaultOps.Check(vault).Pristine);
    }

    [Fact]
    public void One_more_than_parity_is_unrecoverable()
    {
        var img = Image(20000, seed: 9);
        var vault = PreservationVaultOps.Create(img, parityBlocks: 4, dataBlocks: 8);

        Corrupt(vault, 0); Corrupt(vault, 1); Erase(vault, 2); Erase(vault, 3); Corrupt(vault, 9);   // 5 > 4

        var health = PreservationVaultOps.Check(vault);
        Assert.False(health.Recoverable);

        var healed = PreservationVaultOps.Heal(vault, out var report);
        Assert.False(report.Recovered);
        Assert.Empty(healed);
    }

    [Fact]
    public void A_single_bit_of_rot_is_detected_and_repaired()
    {
        var img = Image(5000, seed: 3);
        var vault = PreservationVaultOps.Create(img, parityBlocks: 2, dataBlocks: 8);

        var one = System.Convert.FromBase64String(vault.Blocks[5]!);
        one[10] ^= 0x01;                       // flip one bit
        vault.Blocks[5] = System.Convert.ToBase64String(one);

        Assert.Single(PreservationVaultOps.Check(vault).DamagedBlocks);
        var healed = PreservationVaultOps.Heal(vault, out var report);
        Assert.True(report.ImageValid);
        Assert.Equal(img, healed);
    }

    [Fact]
    public void The_vault_survives_a_json_round_trip_and_still_heals()
    {
        var img = Image(12345, seed: 5);
        var vault = PreservationVaultOps.Create(img, parityBlocks: 3, dataBlocks: 8,
            genomeId: "abc123", lineageDigest: "deadbeef");

        var back = PreservationVaultOps.FromJson(PreservationVaultOps.ToJson(vault));
        Assert.Equal("abc123", back.GenomeId);
        Assert.Equal("deadbeef", back.LineageDigest);

        Corrupt(back, 2); Erase(back, 6); Corrupt(back, 9);   // 3 damaged = budget
        var healed = PreservationVaultOps.Heal(back, out var report);
        Assert.True(report.ImageValid);
        Assert.Equal(img, healed);
    }

    [Fact]
    public void Losing_all_the_parity_still_leaves_the_data_intact()
    {
        var img = Image(9000, seed: 11);
        var vault = PreservationVaultOps.Create(img, parityBlocks: 4, dataBlocks: 8);
        for (int i = 8; i < 12; i++) Erase(vault, i);   // wipe all parity; all 8 data survive

        var healed = PreservationVaultOps.Heal(vault, out var report);
        Assert.True(report.ImageValid);
        Assert.Equal(img, healed);
    }

    [Fact]
    public void Create_rejects_too_many_shards()
    {
        bool threw = false;
        try { PreservationVaultOps.Create(Image(100), parityBlocks: 200, dataBlocks: 200); }
        catch (System.ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
