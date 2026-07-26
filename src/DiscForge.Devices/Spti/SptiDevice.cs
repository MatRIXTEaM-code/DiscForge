// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DiscForge.Devices.Spti;

/// <summary>
/// User-mode raw MMC command access to an optical drive via the Windows SCSI
/// Pass-Through Interface. No kernel driver — works on stock Windows 11 (raw
/// pass-through generally needs an elevated process). Uses
/// SCSI_PASS_THROUGH_DIRECT so the data buffer is transferred directly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SptiDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    public string DevicePath { get; }

    public SptiDevice(char driveLetter)
    {
        DevicePath = $@"\\.\{char.ToUpperInvariant(driveLetter)}:";
        _handle = CreateFile(
            DevicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (_handle.IsInvalid)
            throw new IOException(
                $"Cannot open {DevicePath} (error {Marshal.GetLastPInvokeError()}). " +
                "Raw drive access typically requires elevation.");
    }

    /// <summary>
    /// Sends a CDB and (for reads) fills <paramref name="dataBuffer"/>. Returns
    /// SCSI status and sense data. Transport core for every MMC command.
    /// </summary>
    public SptiResult SendCommand(
        ReadOnlySpan<byte> cdb, Span<byte> dataBuffer,
        SptiDataDirection direction, uint timeoutSeconds = 30)
    {
        if (cdb.Length is 0 or > 16)
            throw new ArgumentException("CDB must be 1..16 bytes.", nameof(cdb));

        var sptd = new SCSI_PASS_THROUGH_DIRECT
        {
            Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>(),
            CdbLength = (byte)cdb.Length,
            SenseInfoLength = SenseLen,
            DataIn = direction switch
            {
                SptiDataDirection.In => SCSI_IOCTL_DATA_IN,
                SptiDataDirection.Out => SCSI_IOCTL_DATA_OUT,
                _ => SCSI_IOCTL_DATA_UNSPECIFIED,
            },
            DataTransferLength = (uint)dataBuffer.Length,
            TimeOutValue = timeoutSeconds,
            SenseInfoOffset = (uint)Marshal.OffsetOf<SPTD_WITH_SENSE>(nameof(SPTD_WITH_SENSE.Sense)),
            Cdb = new byte[16],
        };
        cdb.CopyTo(sptd.Cdb);

        // Pin the data buffer and marshal the header+sense in one allocation.
        var wrapper = new SPTD_WITH_SENSE { Spt = sptd, Sense = new byte[SenseLen] };

        unsafe
        {
            fixed (byte* dataPtr = dataBuffer)
            {
                wrapper.Spt.DataBuffer = dataBuffer.Length == 0 ? IntPtr.Zero : (IntPtr)dataPtr;

                int size = Marshal.SizeOf<SPTD_WITH_SENSE>();
                IntPtr native = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(wrapper, native, false);
                    bool ok = DeviceIoControl(
                        _handle, IOCTL_SCSI_PASS_THROUGH_DIRECT,
                        native, (uint)size, native, (uint)size, out _, IntPtr.Zero);

                    var outWrapper = Marshal.PtrToStructure<SPTD_WITH_SENSE>(native);
                    if (!ok)
                        return new SptiResult(false, outWrapper.Spt.ScsiStatus, outWrapper.Sense);
                    return new SptiResult(
                        outWrapper.Spt.ScsiStatus == 0, outWrapper.Spt.ScsiStatus, outWrapper.Sense);
                }
                finally { Marshal.FreeHGlobal(native); }
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    // --- Win32 / SPTI interop ---

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;
    private const byte SCSI_IOCTL_DATA_OUT = 0;
    private const byte SCSI_IOCTL_DATA_IN = 1;
    private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;
    private const byte SenseLen = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SPTD_WITH_SENSE
    {
        public SCSI_PASS_THROUGH_DIRECT Spt;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SenseLen)]
        public byte[] Sense;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}

public enum SptiDataDirection { None, In, Out }

/// <summary>
/// Result of a pass-through command. When a command fails the sense data is the
/// only clue as to why, so it's decoded here rather than left as raw bytes —
/// "medium not present" is a very different problem from "invalid field in CDB".
/// </summary>
public readonly record struct SptiResult(bool Success, byte ScsiStatus, byte[] SenseData)
{
    /// <summary>SPC sense key (low nibble of sense byte 2).</summary>
    public byte SenseKey => SenseData is { Length: > 2 } ? (byte)(SenseData[2] & 0x0F) : (byte)0;
    /// <summary>Additional Sense Code.</summary>
    public byte Asc => SenseData is { Length: > 12 } ? SenseData[12] : (byte)0;
    /// <summary>Additional Sense Code Qualifier.</summary>
    public byte Ascq => SenseData is { Length: > 13 } ? SenseData[13] : (byte)0;

    /// <summary>Human-readable diagnosis, for logs and error messages.</summary>
    public string Describe()
    {
        if (Success) return "OK";

        string key = SenseKey switch
        {
            0x00 => "No sense",
            0x01 => "Recovered error",
            0x02 => "Not ready",
            0x03 => "Medium error",
            0x04 => "Hardware error",
            0x05 => "Illegal request",
            0x06 => "Unit attention",
            0x07 => "Data protect",
            0x08 => "Blank check",
            0x0B => "Aborted command",
            0x0D => "Volume overflow",
            0x0E => "Miscompare",
            _ => $"Sense key 0x{SenseKey:X2}",
        };

        // The ASC/ASCQ pairs an optical reader actually runs into.
        string detail = (Asc, Ascq) switch
        {
            (0x3A, _) => "no disc in the drive",
            (0x04, 0x01) => "drive is still spinning up — try again shortly",
            (0x20, 0x00) => "drive does not support this command",
            (0x21, 0x00) => "address out of range (past the lead-out?)",
            (0x24, 0x00) => "invalid field in CDB (the drive rejected this request shape)",
            (0x11, 0x05) => "uncorrectable read error — the disc is damaged or dirty here",
            (0x30, 0x00) => "incompatible medium",
            (0x30, 0x02) => "cannot read medium — unknown format",
            (0x64, 0x00) => "illegal mode for this track (wrong sector type requested)",

            // 0x6F: copy-protection key exchange. The disc is encrypted (CSS) and
            // the drive will not release scrambled sectors. Report this plainly —
            // it is not a damaged disc, and mislabelling it sends people chasing
            // a fault that isn't there. DiscForge does not implement CSS.
            (0x6F, 0x00) => "copy protection: authentication failed — this disc is CSS-encrypted",
            (0x6F, 0x01) => "copy protection: key not present — this disc is CSS-encrypted",
            (0x6F, 0x02) => "copy protection: key not established — this disc is CSS-encrypted",
            (0x6F, 0x03) => "copy protection: the drive will not read scrambled sectors without " +
                            "CSS authentication. This is an encrypted DVD-Video disc; DiscForge " +
                            "does not circumvent copy protection",
            (0x6F, 0x04) => "copy protection: disc region does not match the drive's region",
            (0x6F, 0x05) => "copy protection: drive region setting error",
            (0x6F, _) => "copy protection: key exchange failure — this disc is encrypted",

            (0x00, 0x00) => "",
            _ => $"ASC 0x{Asc:X2} ASCQ 0x{Ascq:X2}",
        };

        return detail.Length > 0
            ? $"{key}: {detail} (status 0x{ScsiStatus:X2})"
            : $"{key} (status 0x{ScsiStatus:X2})";
    }
}
