// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Hard-disk CHD extraction to a raw image. A real chdman hard-disk CHD (zlib/LZMA
/// hunks plus one incompressible NONE hunk) decodes to its exact image (the CHD SHA-1
/// proves it), and a small parent/child pair exercises PARENT-hunk resolution against
/// the parent image.
/// </summary>
public class ChdHdExtractorTests
{
    private const string HdB64 =
        "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";

    private const string HdParentB64 =
        "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";

    private const string HdChildB64 =
        "TUNvbXBySEQAAAB8AAAABXpsaWJsem1hAAAAAAAAAAAAAAAAAACAAAAAAAAAAAFJAAAAAAAAAHwAABAAAAACAM3gpfXF2l4K7LF3LsP01MhB2Tl7pHjRL2" +
        "HVqq7ImzLVuiCFsAgIfLTiun7Bm/NLN1mvyh6QSGjJkdaLeEdEREQBAAAfAAAAAAAAAABDWUxTOjEsSEVBRFM6MixTRUNTOjMyLEJQUzo1MTIAAAmFc1r8" +
        "YHcbyzG9xplqAKxR3BCnfIN4ROndQ/2mi5ljQ3l0yNvF/HCPKHFwP0Vteww/h5Gac4Ryq/NQcBCuNcEMnoraGwMycjme04gnYwuWZHORDLuO2AzxZMjGsb" +
        "mYPxYdcHKhKEV2ysgk2C859X24lCFhNUCT1m3df9t5uqqbCADAaJHhAeArae0BkNkqaO/MrEJT4vnIzhMUJAAAAAAMAAAAAACrRVgIAAAAAhAiEAERAcpP" +
        "ImSA";

    private static byte[] Hd() => System.Convert.FromBase64String(HdB64);
    private static byte[] HdParent() => System.Convert.FromBase64String(HdParentB64);
    private static byte[] HdChild() => System.Convert.FromBase64String(HdChildB64);

    [Fact]
    public void A_hard_disk_chd_extracts_to_its_image_and_verifies()
    {
        var raw = ChdHdExtractor.Extract(Hd());
        Assert.Equal(8 * 4096, raw.Length);   // does not throw => SHA-1 verified
    }

    [Fact]
    public void A_child_hd_chd_without_its_parent_is_declined()
    {
        Assert.Throws<ChdFormatException>(() => ChdHdExtractor.Extract(HdChild()));
    }

    [Fact]
    public void A_child_hd_chd_with_its_parent_extracts_and_verifies()
    {
        var raw = ChdHdExtractor.Extract(HdChild(), HdParent());
        Assert.Equal(8 * 4096, raw.Length);
    }
}
