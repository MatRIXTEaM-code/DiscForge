using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class CircRecoveryTests
{
    [Fact]
    public void A_short_burst_is_fully_correctable_by_c2()
    {
        var v = CircRecovery.AnalyzeBurst(12);
        Assert.True(v.FullyCorrectable);
        Assert.True(v.MaxErasuresPerC2 <= v.C2ErasureCapacity);
    }

    [Fact]
    public void A_long_burst_exceeds_the_c2_erasure_budget()
    {
        var v = CircRecovery.AnalyzeBurst(40);
        Assert.False(v.FullyCorrectable);
        Assert.True(v.MaxErasuresPerC2 > v.C2ErasureCapacity);
    }

    [Fact]
    public void The_oracle_reports_the_interleaves_correctable_burst_length()
    {
        // With a 4-frame delay over 28 symbols, C2's 4-erasure budget covers a 16-frame burst.
        var v = CircRecovery.AnalyzeBurst(0);
        Assert.Equal(16, v.MaxCorrectableBurstFrames);
    }

    [Fact]
    public void Interleaving_turns_an_uncorrectable_burst_into_a_recoverable_one()
    {
        // A 12-frame burst would wipe 12 symbols from a single un-interleaved codeword (uncorrectable),
        // but cross-interleaving spreads it so every C2 codeword stays within its 4-erasure budget.
        bool recovered = CircRecovery.SimulateBurst(frames: 300, burstStart: 100, burstLen: 12, out int maxErasures);
        Assert.True(recovered);
        Assert.True(maxErasures <= CircRecovery.C2ErasureCapacity);
    }

    [Fact]
    public void A_burst_beyond_the_interleave_capacity_is_not_fully_recovered()
    {
        bool recovered = CircRecovery.SimulateBurst(frames: 300, burstStart: 100, burstLen: 40, out int maxErasures);
        Assert.False(recovered);
        Assert.True(maxErasures > CircRecovery.C2ErasureCapacity);
    }

    [Fact]
    public void The_simulation_and_the_oracle_agree_on_the_erasure_load()
    {
        // The worst-case erasure count the simulation actually sees matches the oracle's prediction.
        CircRecovery.SimulateBurst(frames: 400, burstStart: 150, burstLen: 20, out int simMax);
        var oracle = CircRecovery.AnalyzeBurst(20);
        Assert.Equal(oracle.MaxErasuresPerC2, simMax);
    }
}
