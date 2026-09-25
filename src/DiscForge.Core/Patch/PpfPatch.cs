// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Patch;

/// <summary>Which PPF revision a file is.</summary>
public enum PpfVersion { V1, V2, V3 }

/// <summary>Thrown when a file is not a PPF, or is a malformed one.</summary>
public sealed class PpfFormatException(string message) : Exception(message);

/// <summary>One contiguous run of bytes a patch changes.</summary>
public sealed record PpfRecord
{
    /// <summary>Byte offset into the target image.</summary>
    public required long Offset { get; init; }
    /// <summary>The bytes to write there.</summary>
    public required byte[] Data { get; init; }
    /// <summary>The bytes that were there before, when the patch carries undo
    /// data (PPF 3.0 only). Null otherwise. Same length as <see cref="Data"/>.</summary>
    public byte[]? Undo { get; init; }
}

/// <summary>A parsed PPF patch: everything the file says, decoded.</summary>
public sealed record PpfPatchFile
{
    public required PpfVersion Version { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<PpfRecord> Records { get; init; }

    /// <summary>True when the patch carries a 1024-byte block from the original
    /// image (at <see cref="ValidationOffset"/>) so an apply can confirm it is
    /// being applied to the right file. PPF 2.0 always; PPF 3.0 optionally.</summary>
    public bool HasValidationBlock => ValidationBlock is not null;
    public byte[]? ValidationBlock { get; init; }

    /// <summary>Where in the target the validation block was taken from.</summary>
    public long ValidationOffset { get; init; }

    /// <summary>Original file length the patch was built against (PPF 2.0 only;
    /// 0 when not recorded).</summary>
    public long OriginalSize { get; init; }

    /// <summary>True when every record carries undo data — the patch can be
    /// reverted (PPF 3.0 with undo).</summary>
    public bool CanUndo => Records.Count > 0 && Records.All(r => r.Undo is not null);

    /// <summary>The appended file_id.diz text, if any. Metadata only.</summary>
    public string? FileId { get; init; }

    /// <summary>Highest byte the patch writes to — the minimum image size it fits.</summary>
    public long MaxTouchedOffset =>
        Records.Count == 0 ? 0 : Records.Max(r => r.Offset + r.Data.Length);
}

/// <summary>
/// Read, apply, revert and write PlayStation Patch Files (PPF) — the format
/// PPF-O-Matic, the PPF Patch Engine, and PAL region patchers (PAL4U and its
/// kin) all speak. A PPF is a list of "at this offset, these bytes become
/// those" edits against a disc image, most often a PlayStation 1 BIN track:
/// fan translations, region fixes, un-protects and hacks are all shipped this
/// way.
///
/// All three revisions are read; PPF 3.0 is written, because it is the only one
/// that carries undo data (so a patch can be cleanly reverted) and an optional
/// validation block (so an apply can refuse a mismatched image before it
/// corrupts it). The layouts, established and stable since the Paradox PPF3.0
/// specification:
///
///   PPF 1.0   "PPF10", 50-byte description, then records of
///             [4-byte LE offset][1-byte length N][N bytes].
///
///   PPF 2.0   "PPF20", description, 4-byte original size, a 1024-byte block
///             from the original at offset 0x9320, then 4-byte-offset records.
///             The size and block let an apply validate the target.
///
///   PPF 3.0   "PPF30", description, image-type / block-check / undo flags,
///             an optional 1024-byte validation block, then records of
///             [8-byte LE offset][1-byte N][N bytes][N undo bytes if present].
///
/// This is patch application, not circumvention: a PPF is an edit list a person
/// already holds, and applying one to a backup of a disc they own is exactly
/// what the patch was published for. DiscForge neither generates protection
/// bypasses nor ships any patch content.
/// </summary>
public static class PpfPatch
{
    // The block-check sample is 1024 bytes taken at a fixed offset. BIN images
    // (2352-byte sectors) use 0x9320; PrimoDVD "GI" images use 0x80A0. The BIN
    // offset is the one PS1 patches use.
    private const int ValidationLength = 1024;
    private const long ValidationOffsetBin = 0x9320;
    private const long ValidationOffsetGi = 0x80A0;

    private const int DescriptionLength = 50;
    private static readonly byte[] FileIdBegin = Encoding.ASCII.GetBytes("@BEGIN_FILE_ID.DIZ");

    // ---- parsing -----------------------------------------------------------

    /// <summary>Parse a PPF from its bytes. Throws <see cref="PpfFormatException"/>
    /// if the file is not a well-formed PPF of a known revision.</summary>
    public static PpfPatchFile Parse(byte[] ppf)
    {
        ArgumentNullException.ThrowIfNull(ppf);
        if (ppf.Length < 6)
            throw new PpfFormatException("Too short to be a PPF — no magic.");

        string magic = Encoding.ASCII.GetString(ppf, 0, 5);
        return magic switch
        {
            "PPF10" => ParseV1(ppf),
            "PPF20" => ParseV2(ppf),
            "PPF30" => ParseV3(ppf),
            _ => throw new PpfFormatException(
                $"Not a PPF: magic is \"{Printable(magic)}\", expected PPF10, PPF20 or PPF30."),
        };
    }

    /// <summary>Parse a PPF from a file on disk.</summary>
    public static PpfPatchFile ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path));
    }

    private static PpfPatchFile ParseV1(byte[] ppf)
    {
        // "PPF10"(5) + method(1) + description(50) = 56, then records.
        const int dataStart = 56;
        string desc = ReadDescription(ppf, 6);
        var (records, fileId) = ReadRecords(ppf, dataStart, offsetBytes: 4, hasUndo: false);
        return new PpfPatchFile
        {
            Version = PpfVersion.V1,
            Description = desc,
            Records = records,
            FileId = fileId,
        };
    }

    private static PpfPatchFile ParseV2(byte[] ppf)
    {
        // "PPF20"(5) + method(1) + description(50) + origSize(4) + block(1024).
        if (ppf.Length < 56 + 4 + ValidationLength)
            throw new PpfFormatException("PPF 2.0 header is truncated.");

        string desc = ReadDescription(ppf, 6);
        long origSize = BinaryPrimitives.ReadUInt32LittleEndian(ppf.AsSpan(56));
        var block = ppf.AsSpan(60, ValidationLength).ToArray();
        int dataStart = 60 + ValidationLength;

        var (records, fileId) = ReadRecords(ppf, dataStart, offsetBytes: 4, hasUndo: false);
        return new PpfPatchFile
        {
            Version = PpfVersion.V2,
            Description = desc,
            Records = records,
            ValidationBlock = block,
            ValidationOffset = ValidationOffsetBin,
            OriginalSize = origSize,
            FileId = fileId,
        };
    }

    private static PpfPatchFile ParseV3(byte[] ppf)
    {
        // "PPF30"(5) + method(1) + description(50) + imagetype(1) +
        // blockcheck(1) + undo(1) + dummy(1) = 60, then optional 1024 block.
        if (ppf.Length < 60)
            throw new PpfFormatException("PPF 3.0 header is truncated.");

        string desc = ReadDescription(ppf, 6);
        int imageType = ppf[56];
        bool hasBlock = ppf[57] != 0;
        bool hasUndo = ppf[58] != 0;

        long validationOffset = imageType == 1 ? ValidationOffsetGi : ValidationOffsetBin;
        byte[]? block = null;
        int dataStart = 60;
        if (hasBlock)
        {
            if (ppf.Length < 60 + ValidationLength)
                throw new PpfFormatException("PPF 3.0 claims a validation block but is too short.");
            block = ppf.AsSpan(60, ValidationLength).ToArray();
            dataStart = 60 + ValidationLength;
        }

        var (records, fileId) = ReadRecords(ppf, dataStart, offsetBytes: 8, hasUndo);
        return new PpfPatchFile
        {
            Version = PpfVersion.V3,
            Description = desc,
            Records = records,
            ValidationBlock = block,
            ValidationOffset = validationOffset,
            FileId = fileId,
        };
    }

    private static string ReadDescription(byte[] ppf, int at)
    {
        int len = Math.Min(DescriptionLength, ppf.Length - at);
        if (len <= 0) return "";
        return Encoding.ASCII.GetString(ppf, at, len).TrimEnd('\0', ' ');
    }

    // Records run to end of file, or to the file_id.diz block if one is
    // appended. Because records are length-prefixed we always know where the
    // next one starts, and can test that position for the file_id marker rather
    // than scanning the patch bytes (which could contain the marker by chance).
    private static (List<PpfRecord>, string? FileId) ReadRecords(
        byte[] ppf, int start, int offsetBytes, bool hasUndo)
    {
        var records = new List<PpfRecord>();
        int p = start;
        int minRecord = offsetBytes + 1;   // offset + length byte (+ at least the data)

        while (p + minRecord <= ppf.Length)
        {
            if (StartsWith(ppf, p, FileIdBegin))
                return (records, ReadFileId(ppf, p));

            long offset = offsetBytes == 8
                ? (long)BinaryPrimitives.ReadUInt64LittleEndian(ppf.AsSpan(p))
                : BinaryPrimitives.ReadUInt32LittleEndian(ppf.AsSpan(p));
            p += offsetBytes;

            int n = ppf[p++];
            if (n == 0)
                throw new PpfFormatException($"Record at offset {offset} has zero length.");
            if (p + n > ppf.Length)
                throw new PpfFormatException(
                    $"Record at image offset {offset} runs past the end of the patch.");

            var data = ppf.AsSpan(p, n).ToArray();
            p += n;

            byte[]? undo = null;
            if (hasUndo)
            {
                if (p + n > ppf.Length)
                    throw new PpfFormatException(
                        $"Record at image offset {offset} is missing its undo data.");
                undo = ppf.AsSpan(p, n).ToArray();
                p += n;
            }

            records.Add(new PpfRecord { Offset = offset, Data = data, Undo = undo });
        }

        // Trailing bytes too short for a record and not a file_id: tolerate
        // silently only if nothing is left; otherwise the file is malformed.
        if (p != ppf.Length && !StartsWith(ppf, p, FileIdBegin))
        {
            // A lone file_id marker shorter than minRecord can still be here.
            if (p + FileIdBegin.Length <= ppf.Length && StartsWith(ppf, p, FileIdBegin))
                return (records, ReadFileId(ppf, p));
        }
        else if (StartsWith(ppf, p, FileIdBegin))
        {
            return (records, ReadFileId(ppf, p));
        }

        return (records, null);
    }

    private static string ReadFileId(byte[] ppf, int at)
    {
        // From the marker to end of file, trimming a trailing binary length/
        // ".DIZ" trailer some writers append. We keep the human-readable text.
        int end = ppf.Length;
        var text = Encoding.ASCII.GetString(ppf, at, end - at);
        int endMarker = text.IndexOf("@END_FILE_ID.DIZ", StringComparison.Ordinal);
        if (endMarker >= 0)
            text = text[..(endMarker + "@END_FILE_ID.DIZ".Length)];
        return text.Trim();
    }

    // ---- applying ----------------------------------------------------------

    /// <summary>The outcome of checking whether a patch fits a target image.</summary>
    public sealed record ApplyCheck(bool Ok, string? Problem, bool ValidationMatched);

    /// <summary>
    /// Confirm a patch can be applied to an image without doing it: the image is
    /// long enough for every record, and — when the patch carries one — its
    /// validation block matches the image. Returns the reason when it cannot.
    /// </summary>
    public static ApplyCheck CheckApplicable(PpfPatchFile patch, Stream image)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(image);

        if (patch.Records.Count == 0)
            return new ApplyCheck(false, "The patch contains no changes.", false);

        if (patch.MaxTouchedOffset > image.Length)
            return new ApplyCheck(false,
                $"The patch writes as far as offset {patch.MaxTouchedOffset:N0}, but the image is " +
                $"only {image.Length:N0} bytes. This patch is for a larger or different image.",
                false);

        bool validationMatched = false;
        if (patch.ValidationBlock is not null)
        {
            if (patch.ValidationOffset + ValidationLength > image.Length)
                return new ApplyCheck(false,
                    "The image is too short to hold the patch's validation block — it is not the " +
                    "image this patch was made for.", false);

            var actual = new byte[ValidationLength];
            image.Position = patch.ValidationOffset;
            ReadExactly(image, actual);
            if (!actual.AsSpan().SequenceEqual(patch.ValidationBlock))
                return new ApplyCheck(false,
                    "The image does not match this patch's validation block. Applying it would " +
                    "corrupt the wrong file — check you have the exact image the patch targets " +
                    "(same region, same dump).", false);
            validationMatched = true;
        }

        return new ApplyCheck(true, null, validationMatched);
    }

    /// <summary>
    /// Apply a patch to a seekable, writable image stream in place. Validates
    /// first unless <paramref name="force"/> is set; on a validation failure it
    /// throws rather than write. Returns the number of records applied.
    /// </summary>
    public static int Apply(PpfPatchFile patch, Stream image, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanWrite || !image.CanSeek)
            throw new ArgumentException("The image stream must be writable and seekable.", nameof(image));

        if (!force)
        {
            var check = CheckApplicable(patch, image);
            if (!check.Ok)
                throw new PpfFormatException(check.Problem!);
        }

        foreach (var r in patch.Records)
        {
            image.Position = r.Offset;
            image.Write(r.Data, 0, r.Data.Length);
        }
        image.Flush();
        return patch.Records.Count;
    }

    /// <summary>Apply a patch to an image file on disk, editing it in place.</summary>
    public static int ApplyToFile(PpfPatchFile patch, string imagePath, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(imagePath);
        using var image = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite);
        return Apply(patch, image, force);
    }

    /// <summary>
    /// Revert a PPF 3.0 patch that carries undo data, restoring the original
    /// bytes. Throws if the patch has no undo data. Where a validation block is
    /// present it must still match (the image should be the patched form of the
    /// same disc), unless <paramref name="force"/> is set.
    /// </summary>
    public static int Undo(PpfPatchFile patch, Stream image, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(image);
        if (!patch.CanUndo)
            throw new PpfFormatException(
                "This patch carries no undo data, so it cannot be reverted. Only PPF 3.0 patches " +
                "written with undo can be undone.");
        if (!image.CanWrite || !image.CanSeek)
            throw new ArgumentException("The image stream must be writable and seekable.", nameof(image));

        if (!force && patch.MaxTouchedOffset > image.Length)
            throw new PpfFormatException(
                "The image is shorter than the patch expects — it is not the image this patch " +
                "was applied to.");

        foreach (var r in patch.Records)
        {
            image.Position = r.Offset;
            image.Write(r.Undo!, 0, r.Undo!.Length);
        }
        image.Flush();
        return patch.Records.Count;
    }

    /// <summary>Revert a patch against an image file on disk, in place.</summary>
    public static int UndoToFile(PpfPatchFile patch, string imagePath, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(imagePath);
        using var image = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite);
        return Undo(patch, image, force);
    }

    // ---- creating ----------------------------------------------------------

    /// <summary>Options for building a PPF 3.0 from a before/after pair.</summary>
    public sealed record CreateOptions
    {
        /// <summary>Up to 50 characters shown by every PPF tool.</summary>
        public string Description { get; init; } = "Created by DiscForge";
        /// <summary>Store the original bytes of each change so the patch can be
        /// reverted. On by default — it roughly doubles patch size but makes the
        /// patch undoable.</summary>
        public bool IncludeUndo { get; init; } = true;
        /// <summary>Store a 1024-byte sample of the original so an apply can
        /// confirm the right image. On by default.</summary>
        public bool IncludeValidation { get; init; } = true;
        /// <summary>Optional file_id.diz text appended to the patch.</summary>
        public string? FileId { get; init; }
    }

    /// <summary>
    /// Build a PPF 3.0 patch that turns <paramref name="original"/> into
    /// <paramref name="modified"/>. The two must be the same length. Runs of
    /// differing bytes become records; a run longer than 255 bytes is split
    /// across several, as the format's one-byte length field requires.
    /// </summary>
    public static byte[] Create(byte[] original, byte[] modified, CreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        options ??= new CreateOptions();
        if (original.Length != modified.Length)
            throw new ArgumentException(
                $"A PPF describes byte replacements, so the images must be the same length " +
                $"(original {original.Length:N0}, modified {modified.Length:N0}).");

        var records = DiffToRecords(original, modified, options.IncludeUndo);
        return WriteV3(records, options, original);
    }

    /// <summary>Build a PPF 3.0 from two image files on disk.</summary>
    public static byte[] CreateFromFiles(string originalPath, string modifiedPath,
                                         CreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(originalPath);
        ArgumentNullException.ThrowIfNull(modifiedPath);
        return Create(File.ReadAllBytes(originalPath), File.ReadAllBytes(modifiedPath), options);
    }

    /// <summary>The differing runs between two equal-length buffers, chunked to
    /// the 255-byte record limit.</summary>
    internal static List<PpfRecord> DiffToRecords(byte[] original, byte[] modified, bool includeUndo)
    {
        var records = new List<PpfRecord>();
        int i = 0;
        while (i < modified.Length)
        {
            if (original[i] == modified[i]) { i++; continue; }

            int runStart = i;
            while (i < modified.Length && original[i] != modified[i] && i - runStart < 255)
                i++;
            int len = i - runStart;

            records.Add(new PpfRecord
            {
                Offset = runStart,
                Data = modified.AsSpan(runStart, len).ToArray(),
                Undo = includeUndo ? original.AsSpan(runStart, len).ToArray() : null,
            });
        }
        return records;
    }

    private static byte[] WriteV3(List<PpfRecord> records, CreateOptions options, byte[] original)
    {
        using var ms = new MemoryStream();
        void W(byte[] b) => ms.Write(b, 0, b.Length);

        W(Encoding.ASCII.GetBytes("PPF30"));
        ms.WriteByte(0x02);                         // encoding method: PPF 3.0
        W(FixedAscii(options.Description, DescriptionLength));
        ms.WriteByte(0x00);                         // image type: BIN
        bool hasBlock = options.IncludeValidation && original.Length >= ValidationOffsetBin + ValidationLength;
        ms.WriteByte((byte)(hasBlock ? 1 : 0));     // block check
        ms.WriteByte((byte)(options.IncludeUndo ? 1 : 0));
        ms.WriteByte(0x00);                         // dummy

        if (hasBlock)
            W(original.AsSpan((int)ValidationOffsetBin, ValidationLength).ToArray());

        Span<byte> off = stackalloc byte[8];
        foreach (var r in records)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(off, (ulong)r.Offset);
            ms.Write(off);
            ms.WriteByte((byte)r.Data.Length);
            W(r.Data);
            if (options.IncludeUndo)
                W(r.Undo ?? new byte[r.Data.Length]);
        }

        if (!string.IsNullOrEmpty(options.FileId))
            WriteFileIdBlock(ms, options.FileId);

        return ms.ToArray();
    }

    // ---- serialize, convert and edit ---------------------------------------

    /// <summary>Serialize a parsed patch back to bytes in its own revision. The
    /// round-trip is faithful: records, undo data, the validation block, the
    /// description and file_id are all preserved. This is the basis for editing a
    /// patch's metadata without rebuilding it from images.</summary>
    public static byte[] Serialize(PpfPatchFile patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return patch.Version switch
        {
            PpfVersion.V1 => WriteV1(patch),
            PpfVersion.V2 => WriteV2(patch),
            _ => WriteV3From(patch),
        };
    }

    /// <summary>
    /// Convert a patch to another PPF revision — the "convert down" older tools
    /// need. To PPF 1.0 always works (records only). To PPF 2.0 needs a validation
    /// block, so it works only from a patch that already carries one (a PPF 2.0 or
    /// a PPF 3.0 built with validation). To PPF 3.0 always works and gains undo
    /// only if the source had it.
    /// </summary>
    public static byte[] ConvertTo(PpfPatchFile patch, PpfVersion target)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return target switch
        {
            PpfVersion.V1 => WriteV1(patch),
            PpfVersion.V2 => patch.ValidationBlock is not null
                ? WriteV2(patch)
                : throw new PpfFormatException(
                    "Converting to PPF 2.0 needs a validation block, which this patch does not " +
                    "carry. PPF 2.0 always validates against a 1024-byte sample of the original; " +
                    "only a patch built with validation (PPF 2.0, or PPF 3.0 with a block) can " +
                    "become one. Convert to PPF 1.0, or keep it at 3.0."),
            _ => WriteV3From(patch),
        };
    }

    /// <summary>Return a copy of a patch with its description and/or file_id.diz
    /// changed — the metadata edit older "PPF editor" tools did. Everything else
    /// (records, undo, validation) is untouched.</summary>
    public static PpfPatchFile WithMetadata(PpfPatchFile patch, string? description = null, string? fileId = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return patch with
        {
            Description = description ?? patch.Description,
            FileId = fileId ?? patch.FileId,
        };
    }

    private static byte[] WriteV1(PpfPatchFile patch)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("PPF10"));
        ms.WriteByte(0x00);                                   // encoding method
        ms.Write(FixedAscii(patch.Description, DescriptionLength));

        Span<byte> off = stackalloc byte[4];
        foreach (var r in patch.Records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(off, (uint)r.Offset);
            ms.Write(off);
            ms.WriteByte((byte)r.Data.Length);
            ms.Write(r.Data);
        }
        if (!string.IsNullOrEmpty(patch.FileId)) WriteFileIdBlock(ms, patch.FileId);
        return ms.ToArray();
    }

    private static byte[] WriteV2(PpfPatchFile patch)
    {
        if (patch.ValidationBlock is null)
            throw new PpfFormatException("PPF 2.0 requires a validation block.");

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("PPF20"));
        ms.WriteByte(0x01);                                   // encoding method
        ms.Write(FixedAscii(patch.Description, DescriptionLength));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)patch.OriginalSize);
        ms.Write(size);
        ms.Write(patch.ValidationBlock);

        Span<byte> off = stackalloc byte[4];
        foreach (var r in patch.Records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(off, (uint)r.Offset);
            ms.Write(off);
            ms.WriteByte((byte)r.Data.Length);
            ms.Write(r.Data);
        }
        if (!string.IsNullOrEmpty(patch.FileId)) WriteFileIdBlock(ms, patch.FileId);
        return ms.ToArray();
    }

    private static byte[] WriteV3From(PpfPatchFile patch)
    {
        using var ms = new MemoryStream();
        void W(byte[] b) => ms.Write(b, 0, b.Length);

        bool hasUndo = patch.CanUndo;
        bool hasBlock = patch.ValidationBlock is not null;

        W(Encoding.ASCII.GetBytes("PPF30"));
        ms.WriteByte(0x02);
        W(FixedAscii(patch.Description, DescriptionLength));
        ms.WriteByte((byte)(patch.ValidationOffset == ValidationOffsetGi ? 1 : 0)); // image type
        ms.WriteByte((byte)(hasBlock ? 1 : 0));
        ms.WriteByte((byte)(hasUndo ? 1 : 0));
        ms.WriteByte(0x00);
        if (hasBlock) W(patch.ValidationBlock!);

        Span<byte> off = stackalloc byte[8];
        foreach (var r in patch.Records)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(off, (ulong)r.Offset);
            ms.Write(off);
            ms.WriteByte((byte)r.Data.Length);
            W(r.Data);
            if (hasUndo) W(r.Undo ?? new byte[r.Data.Length]);
        }
        if (!string.IsNullOrEmpty(patch.FileId)) WriteFileIdBlock(ms, patch.FileId);
        return ms.ToArray();
    }

    private static void WriteFileIdBlock(MemoryStream ms, string fileId)
    {
        // If the text already carries the markers (as a parsed FileId does), write
        // it verbatim; otherwise wrap it.
        if (fileId.Contains("@BEGIN_FILE_ID.DIZ", StringComparison.Ordinal))
        {
            ms.Write(Encoding.ASCII.GetBytes(fileId));
            return;
        }
        ms.Write(Encoding.ASCII.GetBytes("@BEGIN_FILE_ID.DIZ\n"));
        ms.Write(Encoding.ASCII.GetBytes(fileId));
        ms.Write(Encoding.ASCII.GetBytes("\n@END_FILE_ID.DIZ"));
    }

    // ---- helpers -----------------------------------------------------------

    private static byte[] FixedAscii(string s, int length)
    {
        var buffer = new byte[length];
        for (int i = 0; i < length; i++) buffer[i] = (byte)' ';
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, length));
        return buffer;
    }

    private static bool StartsWith(byte[] haystack, int at, byte[] needle)
    {
        if (at < 0 || at + needle.Length > haystack.Length) return false;
        for (int i = 0; i < needle.Length; i++)
            if (haystack[at + i] != needle[i]) return false;
        return true;
    }

    private static string Printable(string s) =>
        new(s.Select(c => c >= 32 && c < 127 ? c : '.').ToArray());

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
