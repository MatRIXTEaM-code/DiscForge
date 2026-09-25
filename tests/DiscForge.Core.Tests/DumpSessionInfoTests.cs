// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Tests for the dump-session sidecar: the "how" (drive, firmware, settings) that travels
/// alongside a dump next to a hash manifest's "what".</summary>
public class DumpSessionInfoTests
{
    private static DumpSessionInfo Sample() => new()
    {
        Operation = "read-disc",
        ToolVersion = "1.70.0",
        UtcTimestamp = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc),
        DriveVendor = "PLEXTOR",
        DriveModel = "PX-W5224TA",
        DriveFirmware = "1.04",
        DevicePath = @"\\.\D:",
        MediaProfile = "DvdRom",
        Engine = "spti",
        RetryCount = 3,
        ContinueOnError = false,
        Outcome = "complete",
    };

    [Fact]
    public void JSON_round_trips_every_field()
    {
        var info = Sample();
        var json = DumpSessionRecorder.ToJson(info);
        var back = DumpSessionRecorder.FromJson(json);

        Assert.Equal(info.Operation, back.Operation);
        Assert.Equal(info.ToolVersion, back.ToolVersion);
        Assert.Equal(info.UtcTimestamp, back.UtcTimestamp);
        Assert.Equal(info.DriveVendor, back.DriveVendor);
        Assert.Equal(info.DriveModel, back.DriveModel);
        Assert.Equal(info.DriveFirmware, back.DriveFirmware);
        Assert.Equal(info.Engine, back.Engine);
        Assert.Equal(info.RetryCount, back.RetryCount);
        Assert.Equal(info.Outcome, back.Outcome);
    }

    [Fact]
    public void WriteSidecar_then_ReadSidecar_recovers_the_record()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-{Guid.NewGuid():N}.iso");
        try
        {
            var info = Sample();
            DumpSessionRecorder.WriteSidecar(info, tmp);
            Assert.True(File.Exists(DumpSessionRecorder.SidecarPath(tmp)));

            var read = DumpSessionRecorder.ReadSidecar(tmp);
            Assert.NotNull(read);
            Assert.Equal(info.DriveModel, read!.DriveModel);
        }
        finally
        {
            File.Delete(DumpSessionRecorder.SidecarPath(tmp));
        }
    }

    [Fact]
    public void ReadSidecar_returns_null_when_no_record_exists()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"dforge-test-missing-{Guid.NewGuid():N}.iso");
        Assert.Null(DumpSessionRecorder.ReadSidecar(tmp));
    }

    [Fact]
    public void SidecarPath_appends_the_fixed_suffix()
    {
        Assert.Equal("game.iso.dumpsession.json", DumpSessionRecorder.SidecarPath("game.iso"));
    }

    [Fact]
    public void ToLineageData_flattens_every_populated_field_to_strings()
    {
        var data = Sample().ToLineageData();
        Assert.Equal("read-disc", data["operation"]);
        Assert.Equal("PLEXTOR", data["driveVendor"]);
        Assert.Equal("PX-W5224TA", data["driveModel"]);
        Assert.Equal("3", data["retryCount"]);
        Assert.Equal("False", data["continueOnError"]);
        Assert.Equal("complete", data["outcome"]);
    }

    [Fact]
    public void ToLineageData_omits_null_fields_rather_than_writing_empty_strings()
    {
        var minimal = new DumpSessionInfo
        {
            Operation = "burn-raw",
            ToolVersion = "1.70.0",
            UtcTimestamp = DateTime.UtcNow,
        };
        var data = minimal.ToLineageData();
        Assert.False(data.ContainsKey("driveVendor"));
        Assert.False(data.ContainsKey("retryCount"));
        Assert.Equal(3, data.Count);   // operation, toolVersion, utc only
    }

    [Fact]
    public void ToLineageData_prefixes_extras_so_they_cannot_collide_with_the_fixed_keys()
    {
        var info = Sample() with { Extra = new Dictionary<string, string> { ["subcode"] = "raw" } };
        var data = info.ToLineageData();
        Assert.Equal("raw", data["extra.subcode"]);
    }
}
