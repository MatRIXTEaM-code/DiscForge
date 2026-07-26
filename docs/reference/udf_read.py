#!/usr/bin/env python3
"""
UDF (ECMA-167 / OSTA) reader — reference oracle.

Validated against real images produced by `genisoimage -udf`, which is the only
honest way to do this: unlike ISO 9660 there's no `isoinfo` equivalent to check
a listing against, so the oracle is "make a UDF with known contents, read it back,
compare".

The chain, and it is a chain — every link points at the next:

  1. Volume Recognition Sequence at sector 16+: BEA01 / NSR02|NSR03 / TEA01.
     This only says "there is a UDF here"; it carries no locations.
  2. Anchor Volume Descriptor Pointer (tag 2) at sector 256 (also N-256 and N).
     -> extent of the Main Volume Descriptor Sequence.
  3. Main VDS: a run of descriptors, ending at a Terminating Descriptor (tag 8).
       - Partition Descriptor (tag 5)      -> partition starting sector, length
       - Logical Volume Descriptor (tag 6) -> logical block size, and a long_ad
                                              pointing at the File Set Descriptor
  4. File Set Descriptor (tag 256) -> long_ad of the root directory's ICB.
  5. File Entry (tag 261) or Extended File Entry (tag 266): the ICB describing a
     file or directory — its type, size, and where its data lives.
  6. Directory data is a run of File Identifier Descriptors (tag 257): name +
     the ICB of the thing named.

Key gotchas, all confirmed against the real image:
  - Addresses in ICBs are LOGICAL BLOCKS within a partition. The physical sector
    is partitionStart + logicalBlock. Forget that and you read garbage.
  - The ICB flags' low 3 bits select the allocation descriptor type: 0=short_ad,
    1=long_ad, 3=the file's data is EMBEDDED in the File Entry itself (common for
    tiny files — and easy to miss).
  - An extent's length has its top 2 bits as a type field; mask with 0x3FFFFFFF.
  - FID names are OSTA compressed Unicode: the first byte is a compression ID
    (8 = latin-1, 16 = UTF-16BE), not part of the name.
  - A FID is 38 + L_IU + L_FI bytes, padded to a 4-byte boundary.
  - File Entry and Extended File Entry put L_AD at different offsets (0xAC vs
    0xD4) with different header sizes (0xB0 vs 0xD8).
"""
import struct, sys

SECTOR = 2048

TAG_PRIMARY_VOLUME = 1
TAG_ANCHOR = 2
TAG_PARTITION = 5
TAG_LOGICAL_VOLUME = 6
TAG_TERMINATING = 8
TAG_FILE_SET = 256
TAG_FILE_IDENTIFIER = 257
TAG_FILE_ENTRY = 261
TAG_EXTENDED_FILE_ENTRY = 266

FILETYPE_DIRECTORY = 4
FILETYPE_REGULAR = 5


class UdfError(Exception):
    pass


def tag_id(img, offset):
    if offset + 16 > len(img):
        return 0
    return struct.unpack_from("<H", img, offset)[0]


def tag_checksum_ok(img, offset):
    """Descriptor tag checksum: bytes 0-3 and 5-15 summed mod 256 == byte 4."""
    b = img[offset:offset + 16]
    if len(b) < 16:
        return False
    return ((sum(b[0:4]) + sum(b[5:16])) & 0xFF) == b[4]


def find_anchor(img):
    """AVDP lives at 256, and mirrored at N-256 / N. Any will do."""
    n = len(img) // SECTOR
    for sector in (256, n - 256, n - 1):
        if sector < 0:
            continue
        o = sector * SECTOR
        if tag_id(img, o) == TAG_ANCHOR and tag_checksum_ok(img, o):
            main_len, main_loc = struct.unpack_from("<II", img, o + 16)
            return {"sector": sector, "vds_length": main_len, "vds_location": main_loc}
    raise UdfError("no Anchor Volume Descriptor Pointer (this image has no UDF)")


def read_volume_structures(img):
    anchor = find_anchor(img)
    start = anchor["vds_location"]
    count = max(1, anchor["vds_length"] // SECTOR)

    partition = None
    logical = None

    for i in range(count):
        o = (start + i) * SECTOR
        tid = tag_id(img, o)
        if tid == 0:
            continue
        if not tag_checksum_ok(img, o):
            continue
        if tid == TAG_PARTITION:
            partition = {
                "number": struct.unpack_from("<H", img, o + 22)[0],
                "start": struct.unpack_from("<I", img, o + 188)[0],
                "length": struct.unpack_from("<I", img, o + 192)[0],
            }
        elif tid == TAG_LOGICAL_VOLUME:
            lb_size = struct.unpack_from("<I", img, o + 212)[0]
            fsd_len, fsd_lbn, fsd_part = struct.unpack_from("<IIH", img, o + 248)
            logical = {"block_size": lb_size, "fsd_block": fsd_lbn, "fsd_partition": fsd_part}
        elif tid == TAG_TERMINATING:
            break

    if partition is None:
        raise UdfError("UDF has no Partition Descriptor")
    if logical is None:
        raise UdfError("UDF has no Logical Volume Descriptor")
    if logical["block_size"] != SECTOR:
        raise UdfError(f"logical block size {logical['block_size']} is not supported (expected 2048)")

    return partition, logical


def read_file_entry(img, sector):
    o = sector * SECTOR
    tid = tag_id(img, o)
    if tid not in (TAG_FILE_ENTRY, TAG_EXTENDED_FILE_ENTRY):
        return None

    file_type = img[o + 16 + 11]
    flags = struct.unpack_from("<H", img, o + 16 + 18)[0]

    if tid == TAG_FILE_ENTRY:
        info = struct.unpack_from("<Q", img, o + 0x38)[0]
        l_ea = struct.unpack_from("<I", img, o + 0xA8)[0]
        l_ad = struct.unpack_from("<I", img, o + 0xAC)[0]
        ad_offset = o + 0xB0 + l_ea
    else:
        info = struct.unpack_from("<Q", img, o + 0x38)[0]
        l_ea = struct.unpack_from("<I", img, o + 0xD0)[0]
        l_ad = struct.unpack_from("<I", img, o + 0xD4)[0]
        ad_offset = o + 0xD8 + l_ea

    return {
        "tag": tid,
        "is_dir": file_type == FILETYPE_DIRECTORY,
        "ad_type": flags & 0x07,
        "size": info,
        "l_ad": l_ad,
        "ad_offset": ad_offset,
    }


def file_data(img, fe, partition_start):
    """The bytes this File Entry describes."""
    if fe["ad_type"] == 3:                     # embedded in the File Entry
        return img[fe["ad_offset"]:fe["ad_offset"] + fe["size"]]

    out = bytearray()
    o = fe["ad_offset"]
    end = o + fe["l_ad"]
    while o < end and len(out) < fe["size"]:
        if fe["ad_type"] == 0:                 # short_ad
            raw_len, pos = struct.unpack_from("<II", img, o)
            o += 8
        elif fe["ad_type"] == 1:               # long_ad
            raw_len, pos, _part = struct.unpack_from("<IIH", img, o)
            o += 16
        else:
            raise UdfError(f"allocation descriptor type {fe['ad_type']} is not supported")

        length = raw_len & 0x3FFFFFFF          # top 2 bits are an extent type
        extent_type = raw_len >> 30
        if length == 0:
            break
        if extent_type == 0:                   # recorded and allocated
            sector = partition_start + pos
            out += img[sector * SECTOR: sector * SECTOR + length]
        else:                                  # sparse/unrecorded: reads as zeros
            out += bytes(length)

    return bytes(out[:fe["size"]])


def read_fids(data):
    """Parse a directory's File Identifier Descriptors."""
    p = 0
    while p + 38 <= len(data):
        if struct.unpack_from("<H", data, p)[0] != TAG_FILE_IDENTIFIER:
            break
        characteristics = data[p + 18]
        l_fi = data[p + 19]
        _icb_len, icb_lbn, _icb_part = struct.unpack_from("<IIH", data, p + 20)
        l_iu = struct.unpack_from("<H", data, p + 36)[0]

        raw = bytes(data[p + 38 + l_iu: p + 38 + l_iu + l_fi])
        if l_fi == 0:
            name = ""
        elif raw[0] == 8:
            name = raw[1:].decode("latin-1")
        elif raw[0] == 16:
            name = raw[1:].decode("utf-16-be")
        else:
            name = raw.decode("latin-1", "replace")

        total = 38 + l_iu + l_fi
        total += (4 - (total % 4)) % 4         # pad to 4 bytes

        yield {
            "name": name,
            "is_parent": bool(characteristics & 0x08),
            "is_dir": bool(characteristics & 0x02),
            "is_deleted": bool(characteristics & 0x04),
            "icb_block": icb_lbn,
        }
        p += total


def walk(img, partition_start, root_block):
    """Return a flat list of {path, is_dir, size}."""
    entries = []

    def recurse(block, prefix, depth):
        if depth > 32:
            raise UdfError("directory nesting too deep (loop?)")
        fe = read_file_entry(img, partition_start + block)
        if fe is None or not fe["is_dir"]:
            return
        data = file_data(img, fe, partition_start)
        for fid in read_fids(data):
            if fid["is_parent"] or fid["is_deleted"] or not fid["name"]:
                continue
            child = read_file_entry(img, partition_start + fid["icb_block"])
            path = prefix + "/" + fid["name"]
            entries.append({
                "path": path,
                "is_dir": fid["is_dir"],
                "size": 0 if fid["is_dir"] or child is None else child["size"],
                "icb_block": fid["icb_block"],
            })
            if fid["is_dir"]:
                recurse(fid["icb_block"], path, depth + 1)

    recurse(root_block, "", 0)
    return entries


def read_image(img):
    partition, logical = read_volume_structures(img)

    fsd_sector = partition["start"] + logical["fsd_block"]
    if tag_id(img, fsd_sector * SECTOR) != TAG_FILE_SET:
        raise UdfError("File Set Descriptor not found where the Logical Volume says it is")

    o = fsd_sector * SECTOR
    _len, root_lbn, _part = struct.unpack_from("<IIH", img, o + 400)

    return {
        "partition_start": partition["start"],
        "root_block": root_lbn,
        "entries": walk(img, partition["start"], root_lbn),
    }


def extract(img, partition_start, icb_block):
    fe = read_file_entry(img, partition_start + icb_block)
    return file_data(img, fe, partition_start)


if __name__ == "__main__":
    import subprocess, os, tempfile, shutil

    ok = True
    work = tempfile.mkdtemp()
    try:
        src = os.path.join(work, "src")
        os.makedirs(os.path.join(src, "deep", "deeper"))

        readme = b"hello udf world\n"
        data = bytes(range(256)) * 40
        inner = b"nested file contents\n"
        tiny = b"x"                       # tiny files often get EMBEDDED in the FE

        open(os.path.join(src, "readme.txt"), "wb").write(readme)
        open(os.path.join(src, "data.bin"), "wb").write(data)
        open(os.path.join(src, "deep", "inner.txt"), "wb").write(inner)
        open(os.path.join(src, "deep", "deeper", "tiny.txt"), "wb").write(tiny)

        iso = os.path.join(work, "udf.iso")
        subprocess.run(["genisoimage", "-udf", "-V", "UDFTEST", "-o", iso, src],
                       capture_output=True, check=True)
        img = open(iso, "rb").read()

        result = read_image(img)
        paths = sorted(e["path"] for e in result["entries"])
        print(f"  image: {len(img):,} bytes, partition @ {result['partition_start']}")
        print(f"  found: {paths}")

        expected = ["/data.bin", "/deep", "/deep/deeper", "/deep/deeper/tiny.txt",
                    "/deep/inner.txt", "/readme.txt"]
        c1 = paths == expected
        print(f"  tree matches what we wrote     : {'OK' if c1 else f'FAIL {paths}'}")
        ok &= c1

        by_path = {e["path"]: e for e in result["entries"]}
        c2 = by_path["/readme.txt"]["size"] == len(readme) and by_path["/data.bin"]["size"] == len(data)
        print(f"  sizes correct                  : {'OK' if c2 else 'FAIL'}")
        ok &= c2

        checks = {"/readme.txt": readme, "/data.bin": data,
                  "/deep/inner.txt": inner, "/deep/deeper/tiny.txt": tiny}
        for path, want in checks.items():
            got = extract(img, result["partition_start"], by_path[path]["icb_block"])
            good = got == want
            ok &= good
            print(f"  extract {path:26} {'OK' if good else f'FAIL ({len(got)} vs {len(want)})'}")

        # Rubbish must be refused, not misread.
        try:
            read_image(bytes(600 * SECTOR))
            print("  reject non-UDF                 : FAIL (accepted)")
            ok = False
        except UdfError:
            print("  reject non-UDF                 : OK")

    finally:
        shutil.rmtree(work, ignore_errors=True)

    print("\nUDF READER:", "PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)
