// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.Patch;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for PAL/NTSC video-mode conversion. The GP1(08h) display-mode command is
/// pinned (recognition, and that only bit 3 of the parameter is touched), the
/// scanner is checked for finding the right sites and ignoring look-alikes, and the
/// PPF path is validated by applying the generated patch back through the PPF
/// engine and confirming it produces the converted image.
/// </summary>
public class Ps1VideoModeTests
{
    // A GP1(08h) command word 0x080000PP as four little-endian bytes.
    private static byte[] DisplayModeCmd(byte param) =>
        new byte[] { param, 0x00, 0x00, 0x08 };

    [Fact]
    public void A_display_mode_command_is_recognised_and_look_alikes_are_not()
    {
        Assert.True(Ps1VideoMode.IsDisplayModeCommand(0x08000009));
        Assert.True(Ps1VideoMode.IsDisplayModeCommand(0x08000000));
        Assert.False(Ps1VideoMode.IsDisplayModeCommand(0x08123409));   // param not confined to low byte
        Assert.False(Ps1VideoMode.IsDisplayModeCommand(0x09000009));   // wrong opcode
    }

    [Fact]
    public void The_mode_bit_is_bit3_of_the_parameter()
    {
        Assert.Equal(PsxVideoMode.Ntsc, Ps1VideoMode.ModeOfParam(0x01));   // bit3 clear
        Assert.Equal(PsxVideoMode.Pal, Ps1VideoMode.ModeOfParam(0x09));    // bit3 set
        Assert.Equal(0x09, Ps1VideoMode.SetParamMode(0x01, PsxVideoMode.Pal));
        Assert.Equal(0x01, Ps1VideoMode.SetParamMode(0x09, PsxVideoMode.Ntsc));
        // Only bit 3 changes — the resolution/interlace bits are preserved.
        Assert.Equal(0x27, Ps1VideoMode.SetParamMode(0x2F, PsxVideoMode.Ntsc));
    }

    [Fact]
    public void The_scanner_finds_only_sites_that_need_converting()
    {
        // A buffer with a PAL command, an NTSC command, and unrelated data.
        var data = new byte[32];
        DisplayModeCmd(0x09).CopyTo(data, 4);    // PAL  → needs change for NTSC target
        DisplayModeCmd(0x01).CopyTo(data, 16);   // NTSC → already correct
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0x12345678);  // noise

        var sites = Ps1VideoModePatcher.FindSites(data, PsxVideoMode.Ntsc);

        var site = Assert.Single(sites);
        Assert.Equal(4, site.Offset);
        Assert.Equal(0x09, site.OldParam);
        Assert.Equal(0x01, site.NewParam);
        Assert.Equal(PsxVideoMode.Pal, site.CurrentMode);
    }

    [Fact]
    public void Patching_in_place_flips_the_bit_and_counts_the_sites()
    {
        var data = new byte[12];
        DisplayModeCmd(0x09).CopyTo(data, 0);
        DisplayModeCmd(0x08).CopyTo(data, 8);

        int n = Ps1VideoModePatcher.PatchInPlace(data, PsxVideoMode.Ntsc);

        Assert.Equal(2, n);
        Assert.Equal(0x01, data[0]);   // 0x09 → 0x01
        Assert.Equal(0x00, data[8]);   // 0x08 → 0x00
    }

    [Fact]
    public void The_generated_ppf_applies_to_reproduce_the_converted_image()
    {
        var original = new byte[64];
        DisplayModeCmd(0x09).CopyTo(original, 8);    // a PAL command
        var expected = (byte[])original.Clone();
        Ps1VideoModePatcher.PatchInPlace(expected, PsxVideoMode.Ntsc);

        var ppf = Ps1VideoModePatcher.CreatePpf(original, PsxVideoMode.Ntsc);
        Assert.NotNull(ppf);

        // Apply the patch back through the PPF engine and confirm it matches.
        var patched = (byte[])original.Clone();
        using var ms = new MemoryStream(patched);
        PpfPatch.Apply(PpfPatch.Parse(ppf!), ms);
        Assert.Equal(expected, patched);
    }

    [Fact]
    public void Nothing_to_change_yields_no_patch()
    {
        var data = new byte[8];
        DisplayModeCmd(0x01).CopyTo(data, 0);   // already NTSC
        Assert.Null(Ps1VideoModePatcher.CreatePpf(data, PsxVideoMode.Ntsc));
    }
}
