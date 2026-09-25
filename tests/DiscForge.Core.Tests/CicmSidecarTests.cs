// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Xml.Linq;
using DiscForge.Core.Metadata;
using Xunit;

namespace DiscForge.Core.Tests;

public class CicmSidecarTests
{
    [Fact]
    public void Builds_conformant_optical_metadata()
    {
        string xml = CicmSidecar.Build(new CicmSidecar.Input
        {
            ImageName = "game.bin",
            SizeBytes = 734_003_200,
            Checksums = new[] { new CicmSidecar.Checksum("md5", "abc"), new CicmSidecar.Checksum("sha1", "def") },
            DiscType = "CD-ROM",
            Tracks = 12,
            Sessions = 1,
            MediaTitle = "Test Disc",
        });

        var doc = XDocument.Parse(xml);
        Assert.Equal("CICMMetadata", doc.Root!.Name.LocalName);

        var od = doc.Root.Element("OpticalDisc");
        Assert.NotNull(od);
        Assert.Equal("game.bin", od!.Element("Image")!.Value);
        Assert.Equal("Raw disc image (sector by sector copy)", od.Element("Image")!.Attribute("format")!.Value);
        Assert.Equal("734003200", od.Element("Size")!.Value);
        Assert.Equal("CD-ROM", od.Element("DiscType")!.Value);
        Assert.Equal("12", od.Element("Tracks")!.Value);
        Assert.Equal("Test Disc", od.Element("Sequence")!.Element("MediaTitle")!.Value);

        var md5 = od.Element("Checksums")!.Elements("Checksum").First(c => c.Attribute("type")!.Value == "md5");
        Assert.Equal("abc", md5.Value);
    }

    [Fact]
    public void Block_media_uses_the_block_element_and_omits_optical_fields()
    {
        string xml = CicmSidecar.Build(new CicmSidecar.Input { ImageName = "disk.img", SizeBytes = 100, Optical = false });
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Root!.Element("BlockMedia"));
        Assert.Null(doc.Root.Element("OpticalDisc"));
        Assert.Null(doc.Root.Element("BlockMedia")!.Element("DiscType"));
    }
}
