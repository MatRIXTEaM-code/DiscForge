// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiscForge.Core.Devices;

/// <summary>
/// Native SCSI passthrough on Linux via the SG_IO ioctl — the Linux counterpart of the Windows SPTI
/// layer, and the foundation for running DiscForge's own drive I/O (INQUIRY, TOC reads, raw sector
/// reads) on Linux without shelling out to external tools. It sends the same MMC CDBs
/// <see cref="DiscForge.Core.Mmc.MmcCommands"/> builds for SPTI, so the command layer above is shared
/// between platforms; only this transport differs.
///
/// The sg_io_hdr marshalling — the one part that silently corrupts everything if a field is at the
/// wrong offset — is a fixed sequential layout asserted by unit tests against the kernel's documented
/// 64-bit offsets. Command failures surface CHECK CONDITION sense data rather than pretending success.
/// </summary>
public static class LinuxSgIo
{
    /// <summary>Candidate optical device nodes present on this machine (/dev/sr*, /dev/scd*).</summary>
    public static IReadOnlyList<string> EnumerateDevicePaths()
    {
        if (!OperatingSystem.IsLinux()) return Array.Empty<string>();
        var found = new List<string>();
        for (int i = 0; i < 16; i++)
        {
            if (File.Exists($"/dev/sr{i}")) found.Add($"/dev/sr{i}");
            if (File.Exists($"/dev/scd{i}")) found.Add($"/dev/scd{i}");
        }
        return found;
    }

    public enum Direction { None, ToDevice, FromDevice }

    public sealed record ScsiResult(byte Status, byte[] Sense, int Residual,
                                    ushort HostStatus = 0, ushort DriverStatus = 0)
    {
        /// <summary>Success means the WHOLE path succeeded: SCSI status GOOD and no host-adapter or
        /// driver error. A timeout or dropped device reports SCSI status 0 with a nonzero host/driver
        /// status — treating that as OK would hand the caller an unfilled buffer as good data.</summary>
        public bool Ok => Status == 0 && HostStatus == 0 && (DriverStatus & 0x0F) == 0;
        /// <summary>Sense key / ASC / ASCQ, handling both fixed (0x70/0x71) and descriptor
        /// (0x72/0x73) sense formats.</summary>
        public (byte Key, byte Asc, byte Ascq)? SenseCode
        {
            get
            {
                if (Sense.Length < 4) return null;
                byte rc = (byte)(Sense[0] & 0x7F);
                if (rc is 0x72 or 0x73)                       // descriptor format
                    return ((byte)(Sense[1] & 0x0F), Sense[2], Sense[3]);
                if (Sense.Length >= 14)                       // fixed format
                    return ((byte)(Sense[2] & 0x0F), Sense[12], Sense[13]);
                return null;
            }
        }
    }

    public sealed record InquiryData(string Vendor, string Product, string Revision, byte PeripheralType)
    {
        public bool IsOptical => PeripheralType == 0x05;   // MMC "CD/DVD device"
    }

    // ---- the sg_io_hdr native structure ------------------------------------
    // Layout per <scsi/sg.h>; offsets on 64-bit asserted by SgIoHdrLayoutTests.

    [StructLayout(LayoutKind.Sequential)]
    internal struct SgIoHdr
    {
        public int InterfaceId;          // 'S'
        public int DxferDirection;       // SG_DXFER_*
        public byte CmdLen;
        public byte MxSbLen;
        public ushort IovecCount;
        public uint DxferLen;
        public IntPtr Dxferp;
        public IntPtr Cmdp;
        public IntPtr Sbp;
        public uint Timeout;             // milliseconds
        public uint Flags;
        public int PackId;
        public IntPtr UsrPtr;
        public byte Status;
        public byte MaskedStatus;
        public byte MsgStatus;
        public byte SbLenWr;
        public ushort HostStatus;
        public ushort DriverStatus;
        public int Resid;
        public uint Duration;
        public uint Info;
    }

    internal const int SG_IO = 0x2285;
    internal const int SG_DXFER_NONE = -1;
    internal const int SG_DXFER_TO_DEV = -2;
    internal const int SG_DXFER_FROM_DEV = -3;

    /// <summary>Native size of sg_io_hdr — 88 bytes on every 64-bit Linux ABI.</summary>
    internal static int NativeSize => Marshal.SizeOf<SgIoHdr>();

    // ---- libc ---------------------------------------------------------------

    private const int O_RDWR = 0x0002;
    private const int O_NONBLOCK = 0x0800;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, int request, ref SgIoHdr hdr);

    // ---- device -------------------------------------------------------------

    [SupportedOSPlatform("linux")]
    public sealed class Device : IDisposable
    {
        private int _fd;
        public string Path { get; }

        private Device(string path, int fd) { Path = path; _fd = fd; }

        public static Device OpenDevice(string path)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("SG_IO SCSI passthrough is Linux-only (Windows uses SPTI).");
            int fd = Open(path, O_RDWR | O_NONBLOCK);
            if (fd < 0)
                throw new IOException($"Cannot open {path} (errno {Marshal.GetLastPInvokeError()}) — " +
                                      "does the device exist, and does this user have rw access (cdrom group)?");
            return new Device(path, fd);
        }

        /// <summary>Send one CDB. <paramref name="data"/> receives (FromDevice) or supplies (ToDevice) bytes.</summary>
        public ScsiResult Execute(byte[] cdb, byte[]? data, Direction direction, uint timeoutMs = 10_000)
        {
            ArgumentNullException.ThrowIfNull(cdb);
            ObjectDisposedException.ThrowIf(_fd < 0, this);
            var sense = new byte[32];

            var hCdb = GCHandle.Alloc(cdb, GCHandleType.Pinned);
            var hSense = GCHandle.Alloc(sense, GCHandleType.Pinned);
            var hData = data is { Length: > 0 } ? GCHandle.Alloc(data, GCHandleType.Pinned) : default;
            try
            {
                var hdr = new SgIoHdr
                {
                    InterfaceId = 'S',
                    DxferDirection = direction switch
                    {
                        Direction.ToDevice => SG_DXFER_TO_DEV,
                        Direction.FromDevice => SG_DXFER_FROM_DEV,
                        _ => SG_DXFER_NONE,
                    },
                    CmdLen = (byte)cdb.Length,
                    MxSbLen = (byte)sense.Length,
                    DxferLen = (uint)(data?.Length ?? 0),
                    Dxferp = hData.IsAllocated ? hData.AddrOfPinnedObject() : IntPtr.Zero,
                    Cmdp = hCdb.AddrOfPinnedObject(),
                    Sbp = hSense.AddrOfPinnedObject(),
                    Timeout = timeoutMs,
                };
                if (Ioctl(_fd, SG_IO, ref hdr) < 0)
                    throw new IOException($"SG_IO ioctl failed on {Path} (errno {Marshal.GetLastPInvokeError()}).");
                int senseLen = Math.Min(hdr.SbLenWr, (byte)sense.Length);
                return new ScsiResult(hdr.Status, sense.AsSpan(0, senseLen).ToArray(), hdr.Resid,
                                      hdr.HostStatus, hdr.DriverStatus);
            }
            finally
            {
                hCdb.Free();
                hSense.Free();
                if (hData.IsAllocated) hData.Free();
            }
        }

        public InquiryData Inquiry()
        {
            var buf = new byte[96];
            var r = Execute(Mmc.MmcCommands.Inquiry(96), buf, Direction.FromDevice);
            if (!r.Ok)
                throw new IOException($"INQUIRY failed on {Path}" +
                    (r.SenseCode is var (k, a, q) ? $" (sense {k:X}/{a:X2}/{q:X2})" : "") + ".");
            return ParseInquiry(buf);
        }

        public bool TestUnitReady() => Execute(Mmc.MmcCommands.TestUnitReady(), null, Direction.None).Ok;

        public void Dispose()
        {
            if (_fd >= 0) { Close(_fd); _fd = -1; }
        }
    }

    /// <summary>Decode a standard INQUIRY response (pure; unit-tested).</summary>
    public static InquiryData ParseInquiry(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 36) throw new ArgumentException("A standard INQUIRY response is at least 36 bytes.", nameof(buf));
        static string Str(ReadOnlySpan<byte> s) => System.Text.Encoding.ASCII.GetString(s).Trim();
        return new InquiryData(
            Vendor: Str(buf.Slice(8, 8)),
            Product: Str(buf.Slice(16, 16)),
            Revision: Str(buf.Slice(32, 4)),
            PeripheralType: (byte)(buf[0] & 0x1F));
    }
}
