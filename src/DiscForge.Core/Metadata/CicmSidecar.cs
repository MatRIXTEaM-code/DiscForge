// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using System.Xml;

namespace DiscForge.Core.Metadata;

/// <summary>
/// Emits a CICM metadata sidecar — the preservation-metadata XML Aaru writes (schema: claunia's
/// CICMMetadata, root element <c>CICMMetadata</c>, no target namespace). It records an image's
/// identity, size and checksums in an interchange format archives and Aaru both understand, so a
/// DiscForge dump carries the same machine-readable provenance a preservation workflow expects.
///
/// This produces the CORE, structurally-conformant subset — Image, Size, Sequence, Checksums, and
/// (for optical media) DiscType / Tracks / Sessions, in schema element order. It is not the full
/// 40-element optical schema; the fields it emits use the schema's own names and ordering so the
/// document slots into the CICMMetadata root. Pure and unit-tested.
/// </summary>
public static class CicmSidecar
{
    public sealed record Checksum(string Type, string Hex);

    public sealed record Input
    {
        public required string ImageName { get; init; }
        public string ImageFormat { get; init; } = "Raw disc image (sector by sector copy)";
        public required long SizeBytes { get; init; }
        public IReadOnlyList<Checksum> Checksums { get; init; } = Array.Empty<Checksum>();
        /// <summary>Optical media → &lt;OpticalDisc&gt;; otherwise &lt;BlockMedia&gt;.</summary>
        public bool Optical { get; init; } = true;
        public string? MediaTitle { get; init; }
        public string? DiscType { get; init; }      // optical only, e.g. "CD-ROM"
        public int? Tracks { get; init; }            // optical only
        public int? Sessions { get; init; }          // optical only
    }

    public static string Build(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // Write through a UTF-8 stream so the XML DECLARATION matches the bytes File.WriteAllText emits
        // (an XmlWriter over a StringBuilder always declares utf-16, which would mislabel a saved file).
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Indent = true, Encoding = utf8 };
        using (var w = XmlWriter.Create(ms, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("CICMMetadata");
            w.WriteStartElement(input.Optical ? "OpticalDisc" : "BlockMedia");

            // Image (with a format attribute) then Size — the schema's leading sequence.
            w.WriteStartElement("Image");
            w.WriteAttributeString("format", input.ImageFormat);
            w.WriteString(input.ImageName);
            w.WriteEndElement();

            w.WriteElementString("Size", input.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(input.MediaTitle))
            {
                w.WriteStartElement("Sequence");
                w.WriteElementString("MediaTitle", input.MediaTitle);
                w.WriteEndElement();
            }

            if (input.Checksums.Count > 0)
            {
                w.WriteStartElement("Checksums");
                foreach (var c in input.Checksums)
                {
                    w.WriteStartElement("Checksum");
                    w.WriteAttributeString("type", c.Type);
                    w.WriteString(c.Hex);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }

            if (input.Optical)
            {
                if (!string.IsNullOrEmpty(input.DiscType)) w.WriteElementString("DiscType", input.DiscType);
                if (input.Tracks is int t) w.WriteElementString("Tracks", t.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (input.Sessions is int s) w.WriteElementString("Sessions", s.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            w.WriteEndElement();   // OpticalDisc / BlockMedia
            w.WriteEndElement();   // CICMMetadata
            w.WriteEndDocument();
        }
        return utf8.GetString(ms.ToArray());
    }
}
