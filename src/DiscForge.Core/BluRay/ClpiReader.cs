// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.BluRay;

/// <summary>One elementary stream described by a clip's ProgramInfo table.</summary>
public sealed record ClipStream
{
    /// <summary>The transport-stream PID carrying this elementary stream.</summary>
    public required ushort Pid { get; init; }
    /// <summary>The stream_coding_type byte.</summary>
    public required byte CodingType { get; init; }
    /// <summary>Best-effort class inferred from the coding type.</summary>
    public required StreamKind Kind { get; init; }
    /// <summary>ISO-639 language for audio/PG/text streams; empty for video.</summary>
    public string Language { get; init; } = "";

    // Video attributes (empty for non-video).
    public string VideoFormat { get; init; } = "";
    public string FrameRate { get; init; } = "";
    public string AspectRatio { get; init; } = "";

    // Audio attributes (empty for non-audio).
    public string AudioFormat { get; init; } = "";
    public string SampleRate { get; init; } = "";

    public string CodingName => BdmvCoding.Name(CodingType);
}

/// <summary>A parsed .clpi clip-info file: its version and the streams it declares.</summary>
public sealed record BluRayClip
{
    /// <summary>The 4-char version ("0100", "0200", "0300").</summary>
    public required string Version { get; init; }
    public required IReadOnlyList<ClipStream> Streams { get; init; }
}

/// <summary>
/// Parses a Blu-ray clip-information file (.clpi), the sidecar that describes one
/// .m2ts stream file: its program map and, for each elementary stream, the coding
/// type and codec attributes (video format/frame rate/aspect; audio format/sample
/// rate/language; PG language).
///
/// Layout (all multi-byte integers big-endian):
///   0x00  4   type_indicator             "HDMV"
///   0x04  4   version_number             "0100" / "0200" / "0300"
///   0x08  u32 SequenceInfo_start_address
///   0x0C  u32 ProgramInfo_start_address
///   0x10  u32 CPI_start_address
///   0x14  u32 ClipMark_start_address
///   0x18  u32 ExtensionData_start_address
///   0x1C  12  reserved
///   0x28      ClipInfo()                 (skipped — we jump by the addresses above)
///
/// ProgramInfo() @ ProgramInfo_start_address: length, reserved, number_of_programs,
/// then for each program an SPN + program_map_PID + a stream count, then per stream
/// a 16-bit PID followed by a length-prefixed StreamCodingInfo() carrying the
/// coding type and attributes.
///
/// Structure only — the .m2ts payload is never read, and BDMV metadata is
/// unencrypted even on an AACS disc. Clean-room from the public BDAV/BDMV
/// format description.
/// </summary>
public static class ClpiReader
{
    public const string Magic = "HDMV";

    /// <summary>Parse a clip-info file from a whole-file byte buffer.</summary>
    public static BluRayClip Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new BdmvReaderCursor(data);

        r.RequireMagic(Magic);
        string version = r.ReadAscii(4);

        _ = r.ReadU32();                     // SequenceInfo_start_address
        uint programInfoStart = r.ReadU32(); // ProgramInfo_start_address
        // CPI / ClipMark / ExtensionData addresses follow but aren't decoded here.

        var streams = ParseProgramInfo(r, programInfoStart);

        return new BluRayClip { Version = version, Streams = streams };
    }

    /// <summary>Read a .clpi file and parse it.</summary>
    public static BluRayClip ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Clip-info not found.", path);
        return Parse(File.ReadAllBytes(path));
    }

    private static List<ClipStream> ParseProgramInfo(BdmvReaderCursor r, uint programInfoStart)
    {
        r.Seek(programInfoStart);
        _ = r.ReadU32();                     // length
        _ = r.ReadU8();                      // reserved
        int programCount = r.ReadU8();       // number_of_programs

        var streams = new List<ClipStream>();
        for (int p = 0; p < programCount; p++)
        {
            _ = r.ReadU32();                 // SPN_program_sequence_start
            _ = r.ReadU16();                 // program_map_PID
            int streamCount = r.ReadU8();    // number_of_streams_in_ps
            _ = r.ReadU8();                  // reserved

            for (int s = 0; s < streamCount; s++)
            {
                ushort pid = r.ReadU16();    // stream_PID
                streams.Add(ReadCodingInfo(r, pid));
            }
        }
        return streams;
    }

    /// <summary>Read one length-prefixed StreamCodingInfo() for a given PID.</summary>
    private static ClipStream ReadCodingInfo(BdmvReaderCursor r, ushort pid)
    {
        int start = r.Position;
        int len = r.ReadU8();                // length of the coding-info body
        int end = start + 1 + len;

        byte coding = r.ReadU8();
        StreamKind kind = BdmvCoding.KindOf(coding);

        string language = "";
        string videoFormat = "", frameRate = "", aspect = "";
        string audioFormat = "", sampleRate = "";

        if (BdmvCoding.IsVideo(coding))
        {
            byte fmtRate = r.ReadU8();       // video_format(4) + frame_rate(4)
            byte aspFlags = r.ReadU8();      // aspect_ratio(4) + reserved/cc
            videoFormat = BdmvCoding.VideoFormat(fmtRate >> 4);
            frameRate = BdmvCoding.FrameRate(fmtRate & 0x0F);
            aspect = BdmvCoding.AspectRatio(aspFlags >> 4);
        }
        else if (BdmvCoding.IsAudio(coding))
        {
            byte fmtRate = r.ReadU8();       // audio_format(4) + sample_rate(4)
            language = r.ReadLanguage();
            audioFormat = BdmvCoding.AudioFormat(fmtRate >> 4);
            sampleRate = BdmvCoding.SampleRate(fmtRate & 0x0F);
        }
        else if (coding is BdmvCoding.PresentationGraphics or BdmvCoding.InteractiveGraphics)
        {
            language = r.ReadLanguage();
        }
        else if (coding == BdmvCoding.TextSubtitle)
        {
            _ = r.ReadU8();                  // character_code
            language = r.ReadLanguage();
        }

        r.Seek(end);                         // skip any trailing reserved bytes

        return new ClipStream
        {
            Pid = pid,
            CodingType = coding,
            Kind = kind,
            Language = language,
            VideoFormat = videoFormat,
            FrameRate = frameRate,
            AspectRatio = aspect,
            AudioFormat = audioFormat,
            SampleRate = sampleRate,
        };
    }
}
