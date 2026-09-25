// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Tests for the resume checkpoint that lets `read-disc --resume` pick up an
/// interrupted dump where it left off, instead of re-reading a whole disc from LBA 0.</summary>
public class DumpCheckpointTests
{
    private static DumpCheckpoint Sample() => new()
    {
        Operation = "read-disc",
        NextLba = 123456,
        TotalSectors = 2000000,
        BlockLengthBytes = 2048,
        DriveVendor = "TSSTcorp",
        DriveModel = "SH-224DB",
        RetryCount = 3,
        ContinueOnError = false,
        UtcTimestamp = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void JSON_round_trips_every_field()
    {
        var cp = Sample();
        var back = DumpCheckpointRecorder.FromJson(DumpCheckpointRecorder.ToJson(cp));

        Assert.Equal(cp.Operation, back.Operation);
        Assert.Equal(cp.NextLba, back.NextLba);
        Assert.Equal(cp.TotalSectors, back.TotalSectors);
        Assert.Equal(cp.BlockLengthBytes, back.BlockLengthBytes);
        Assert.Equal(cp.DriveVendor, back.DriveVendor);
        Assert.Equal(cp.DriveModel, back.DriveModel);
        Assert.Equal(cp.RetryCount, back.RetryCount);
        Assert.Equal(cp.ContinueOnError, back.ContinueOnError);
    }

    [Fact]
    public void WriteSidecar_then_ReadSidecar_recovers_the_record()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-{Guid.NewGuid():N}.iso");
        try
        {
            var cp = Sample();
            DumpCheckpointRecorder.WriteSidecar(cp, tmp);
            Assert.True(File.Exists(DumpCheckpointRecorder.SidecarPath(tmp)));

            var read = DumpCheckpointRecorder.ReadSidecar(tmp);
            Assert.NotNull(read);
            Assert.Equal(cp.NextLba, read!.NextLba);
        }
        finally
        {
            File.Delete(DumpCheckpointRecorder.SidecarPath(tmp));
        }
    }

    [Fact]
    public void ReadSidecar_returns_null_when_no_checkpoint_exists()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-missing-{Guid.NewGuid():N}.iso");
        Assert.Null(DumpCheckpointRecorder.ReadSidecar(tmp));
    }

    [Fact]
    public void DeleteSidecar_removes_an_existing_checkpoint_and_is_a_no_op_otherwise()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-{Guid.NewGuid():N}.iso");
        DumpCheckpointRecorder.WriteSidecar(Sample(), tmp);
        Assert.True(File.Exists(DumpCheckpointRecorder.SidecarPath(tmp)));

        DumpCheckpointRecorder.DeleteSidecar(tmp);
        Assert.False(File.Exists(DumpCheckpointRecorder.SidecarPath(tmp)));

        // Deleting again (nothing there) must not throw.
        DumpCheckpointRecorder.DeleteSidecar(tmp);
    }

    [Fact]
    public void SidecarPath_appends_the_fixed_suffix()
    {
        Assert.Equal("game.iso.resume.json", DumpCheckpointRecorder.SidecarPath("game.iso"));
    }

    [Fact]
    public void MatchesCapacity_true_only_when_both_sectors_and_block_length_agree()
    {
        var cp = Sample();
        Assert.True(cp.MatchesCapacity(2000000, 2048));
        Assert.False(cp.MatchesCapacity(1999999, 2048));   // different disc size
        Assert.False(cp.MatchesCapacity(2000000, 2352));   // different sector form
    }
}
