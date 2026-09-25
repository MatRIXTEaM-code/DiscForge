// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Raw;

/// <summary>
/// CD-TEXT: album and track titles/performers carried as 18-byte "packs" in
/// the R–W sub-channels of the LEAD-IN. Because DiscForge generates the
/// lead-in itself for RAW burns, CD-TEXT costs nothing extra at write time —
/// the packs simply become the lead-in's R–W symbols.
///
/// Pack format (public CD-TEXT / MMC documentation):
///   [0] pack type      (0x80 title, 0x81 performer, …, 0x8F size info)
///   [1] track number   (0 = whole album; bit 7 = extension flag, unused here)
///   [2] sequence       (running pack counter within the block)
///   [3] block/char     (bit 7 DBCS, bits 6..4 block number, bits 3..0
///                       character position of this pack's first character
///                       within its string, capped at 15)
///   [4..15] text       (strings NUL-terminated, running across packs)
///   [16..17] CRC-16    (over bytes 0..15, inverted — same CRC as Q)
///
/// One block (language 0, ISO 8859-1) is generated. Each sector's 96 R–W
/// symbols hold four packs (18 bytes = 24 six-bit symbols each); the pack set
/// repeats through the whole lead-in so a player can pick it up at any point.
/// </summary>
public static class CdTextBuilder
{
    public const int PackSize = 18;
    public const int PacksPerSector = 4;

    public sealed record TrackText(string? Title, string? Performer);

    public sealed record DiscText
    {
        public string? AlbumTitle { get; init; }
        public string? AlbumPerformer { get; init; }
        /// <summary>Track texts in track order (index 0 = first track).</summary>
        public IReadOnlyList<TrackText> Tracks { get; init; } = Array.Empty<TrackText>();

        public bool IsEmpty =>
            string.IsNullOrEmpty(AlbumTitle) && string.IsNullOrEmpty(AlbumPerformer) &&
            Tracks.All(t => string.IsNullOrEmpty(t.Title) && string.IsNullOrEmpty(t.Performer));
    }

    /// <summary>
    /// Build the full pack set for one language block. Returns an empty array
    /// when there is no text at all (no CD-TEXT is then written).
    /// </summary>
    public static byte[][] BuildPacks(DiscText text, int firstTrack, int lastTrack)
    {
        if (text.IsEmpty) return Array.Empty<byte[]>();

        var packs = new List<byte[]>();
        byte seq = 0;

        // Type 0x80 (titles) and 0x81 (performers): for each type, the album
        // string (track 0) followed by every track's string, all NUL-joined,
        // flowed across as many packs as needed.
        AppendTextPacks(packs, ref seq, 0x80,
            Flow(text.AlbumTitle, text.Tracks.Select(t => t.Title)));
        AppendTextPacks(packs, ref seq, 0x81,
            Flow(text.AlbumPerformer, text.Tracks.Select(t => t.Performer)));

        // Type 0x8F: size information — three packs describing the block.
        AppendSizeInfo(packs, ref seq, firstTrack, lastTrack);

        return packs.ToArray();
    }

    /// <summary>The NUL-joined string list for one pack type, with the track
    /// number each character belongs to (0 = album).</summary>
    private static List<(byte trackNo, byte ch)> Flow(string? album, IEnumerable<string?> tracks)
    {
        var chars = new List<(byte, byte)>();
        void Add(byte trackNo, string? s)
        {
            foreach (byte b in Encoding.Latin1.GetBytes(s ?? "")) chars.Add((trackNo, b));
            chars.Add((trackNo, 0));           // NUL terminator, owned by its string
        }
        Add(0, album);
        byte n = 1;
        foreach (var s in tracks) Add(n++, s);
        return chars;
    }

    private static void AppendTextPacks(List<byte[]> packs, ref byte seq, byte type,
                                        List<(byte trackNo, byte ch)> chars)
    {
        // Character position within the current string, per pack header byte 3.
        int posInString = 0;
        for (int i = 0; i < chars.Count; i += 12)
        {
            var pack = new byte[PackSize];
            pack[0] = type;
            pack[1] = chars[i].trackNo;
            pack[2] = seq++;
            pack[3] = (byte)Math.Min(posInString, 15);

            for (int j = 0; j < 12; j++)
            {
                if (i + j < chars.Count)
                {
                    pack[4 + j] = chars[i + j].ch;
                    posInString = chars[i + j].ch == 0 ? 0 : posInString + 1;
                }
                else pack[4 + j] = 0;
            }

            ushort crc = Crc16.ComputeInverted(pack.AsSpan(0, 16));
            pack[16] = (byte)(crc >> 8);
            pack[17] = (byte)crc;
            packs.Add(pack);
        }
    }

    private static void AppendSizeInfo(List<byte[]> packs, ref byte seq, int firstTrack, int lastTrack)
    {
        // 36 bytes of size info across three 0x8F packs:
        //   [0] character code (0 = ISO 8859-1)
        //   [1] first track  [2] last track  [3] copyright flags
        //   [4..19]  pack count per type 0x80..0x8F
        //   [20..27] last sequence number per block 0..7
        //   [28..35] language code per block 0..7 (0x09 = English)
        var info = new byte[36];
        info[0] = 0x00;
        info[1] = (byte)firstTrack;
        info[2] = (byte)lastTrack;
        info[3] = 0x00;

        // Pack counts, including the three 0x8F packs themselves.
        var counts = new int[16];
        foreach (var p in packs) counts[p[0] - 0x80]++;
        counts[0x0F] = 3;
        for (int t = 0; t < 16; t++) info[4 + t] = (byte)counts[t];

        info[20] = (byte)(packs.Count + 3 - 1);   // last sequence, block 0
        info[28] = 0x09;                          // English

        for (int p = 0; p < 3; p++)
        {
            var pack = new byte[PackSize];
            pack[0] = 0x8F;
            pack[1] = (byte)p;                    // 0x8F uses byte 1 as its part number
            pack[2] = seq++;
            pack[3] = 0x00;
            Array.Copy(info, p * 12, pack, 4, 12);
            ushort crc = Crc16.ComputeInverted(pack.AsSpan(0, 16));
            pack[16] = (byte)(crc >> 8);
            pack[17] = (byte)crc;
            packs.Add(pack);
        }
    }

    /// <summary>
    /// Fill one sector's 96 R–W symbols with four consecutive packs from the
    /// repeating cycle. <paramref name="sectorIndex"/> is the lead-in sector
    /// number; symbols are the packs' bytes split into 6-bit values, MSB first.
    /// </summary>
    public static void FillSectorRw(byte[][] packs, long sectorIndex, Span<byte> rw96)
    {
        rw96.Clear();
        if (packs.Length == 0) return;

        Span<byte> bytes = stackalloc byte[PacksPerSector * PackSize];   // 72
        for (int p = 0; p < PacksPerSector; p++)
        {
            var pack = packs[(int)((sectorIndex * PacksPerSector + p) % packs.Length)];
            pack.CopyTo(bytes.Slice(p * PackSize, PackSize));
        }

        // 72 bytes → 96 six-bit symbols: every 3 bytes become 4 symbols.
        for (int g = 0; g < 24; g++)
        {
            int b0 = bytes[g * 3], b1 = bytes[g * 3 + 1], b2 = bytes[g * 3 + 2];
            rw96[g * 4 + 0] = (byte)(b0 >> 2);
            rw96[g * 4 + 1] = (byte)(((b0 & 0x03) << 4) | (b1 >> 4));
            rw96[g * 4 + 2] = (byte)(((b1 & 0x0F) << 2) | (b2 >> 6));
            rw96[g * 4 + 3] = (byte)(b2 & 0x3F);
        }
    }
}
