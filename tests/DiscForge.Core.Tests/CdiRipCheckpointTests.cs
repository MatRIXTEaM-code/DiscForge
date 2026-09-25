// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Preservation;
using DiscForge.Core.Reading;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The resume checkpoint that lets `read-cdi --resume` skip re-reading a track that already
/// captured cleanly on a previous attempt, instead of re-ripping a whole multi-track CD because one
/// stubborn track stalled it. Track-granularity, not sector-granularity — see the type's own doc
/// comment for why a CDI's layout makes that the natural unit here.
/// </summary>
public class CdiRipCheckpointTests
{
    private static ReadTrackPlan Track(int number, uint startLba, uint length,
        CdiTrackMode mode = CdiTrackMode.Mode1, CdiSectorSize size = CdiSectorSize.S2048) => new()
    {
        Number = number, StartLba = startLba, LengthSectors = length, Mode = mode, SectorSize = size, IsAudio = false,
    };

    private static ReadPlan Plan(params ReadTrackPlan[] tracks) => new()
    {
        Tracks = tracks, RawMode = false, Warnings = Array.Empty<string>(),
    };

    private static CdiRipCheckpoint Sample() => new()
    {
        PlanSignature = "1:0:100:Mode1:0",
        CompletedTracks = new[] { 1, 2 },
        UtcTimestamp = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void JSON_round_trips_every_field()
    {
        var cp = Sample();
        var back = CdiRipCheckpointRecorder.FromJson(CdiRipCheckpointRecorder.ToJson(cp));
        Assert.Equal(cp.PlanSignature, back.PlanSignature);
        Assert.Equal(cp.CompletedTracks, back.CompletedTracks);
    }

    [Fact]
    public void WriteSidecar_then_ReadSidecar_recovers_the_record()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-{Guid.NewGuid():N}.cdi");
        try
        {
            var cp = Sample();
            CdiRipCheckpointRecorder.WriteSidecar(cp, tmp);
            Assert.True(File.Exists(CdiRipCheckpointRecorder.SidecarPath(tmp)));

            var read = CdiRipCheckpointRecorder.ReadSidecar(tmp);
            Assert.NotNull(read);
            Assert.Equal(cp.PlanSignature, read!.PlanSignature);
            Assert.Equal(cp.CompletedTracks, read.CompletedTracks);
        }
        finally { File.Delete(CdiRipCheckpointRecorder.SidecarPath(tmp)); }
    }

    [Fact]
    public void ReadSidecar_returns_null_when_no_checkpoint_exists()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-missing-{Guid.NewGuid():N}.cdi");
        Assert.Null(CdiRipCheckpointRecorder.ReadSidecar(tmp));
    }

    [Fact]
    public void DeleteSidecar_removes_an_existing_checkpoint_and_is_a_no_op_otherwise()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-{Guid.NewGuid():N}.cdi");
        CdiRipCheckpointRecorder.WriteSidecar(Sample(), tmp);
        Assert.True(File.Exists(CdiRipCheckpointRecorder.SidecarPath(tmp)));

        CdiRipCheckpointRecorder.DeleteSidecar(tmp);
        Assert.False(File.Exists(CdiRipCheckpointRecorder.SidecarPath(tmp)));
        CdiRipCheckpointRecorder.DeleteSidecar(tmp);   // no-op, must not throw
    }

    [Fact]
    public void SidecarPath_appends_the_fixed_suffix()
    {
        Assert.Equal("game.cdi.ripstate.json", CdiRipCheckpointRecorder.SidecarPath("game.cdi"));
    }

    [Fact]
    public void PlanSignature_is_identical_for_two_equivalent_plans()
    {
        var a = Plan(Track(1, 0, 100), Track(2, 100, 50));
        var b = Plan(Track(1, 0, 100), Track(2, 100, 50));
        Assert.Equal(CdiRipCheckpointRecorder.ComputePlanSignature(a), CdiRipCheckpointRecorder.ComputePlanSignature(b));
    }

    [Theory]
    [InlineData("startLba")]
    [InlineData("length")]
    [InlineData("mode")]
    [InlineData("sectorSize")]
    [InlineData("trackCount")]
    public void PlanSignature_changes_when_the_plan_differs(string what)
    {
        var baseline = Plan(Track(1, 0, 100), Track(2, 100, 50));
        var changed = what switch
        {
            "startLba" => Plan(Track(1, 0, 100), Track(2, 101, 50)),
            "length" => Plan(Track(1, 0, 100), Track(2, 100, 51)),
            "mode" => Plan(Track(1, 0, 100), Track(2, 100, 50, CdiTrackMode.Mode2)),
            "sectorSize" => Plan(Track(1, 0, 100), Track(2, 100, 50, size: CdiSectorSize.S2352)),
            "trackCount" => Plan(Track(1, 0, 100)),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        Assert.NotEqual(
            CdiRipCheckpointRecorder.ComputePlanSignature(baseline),
            CdiRipCheckpointRecorder.ComputePlanSignature(changed));
    }
}
