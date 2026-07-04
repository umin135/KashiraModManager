"""
KatanaEngine RDB / RDX 파서 (검증용).

DOA6 Last Round 실측으로 포맷 검증됨. 오프셋 근거는
_docs/_ModManagerDocs/02_rdb_fdata_format.md 참조.

주의 (실측으로 문서와 다른 점):
  - root.rdx 는 헤더가 없다. 오프셋 0부터 8바이트 엔트리 배열이 바로 시작.
  - rdx 엔트리의 padding 은 0xFFFF (문서는 0x0000).
"""

from __future__ import annotations

import struct
from dataclasses import dataclass


# ---- TypeKtid → 확장자 매핑 (03_ktid_system.md) ----
TYPE_KTID_EXT = {
    0x563BDEF1: "g1m",
    0xAFBEC60C: "g1t",
    0xAD57EBBA: "g1t",
    0x6FA91671: "g1a",
    0x7BCD279F: "g1s",
    0x5153729B: "mtl",
    0x8E39AA37: "ktid",
    0x20A6A0BB: "kidsobjdb",
    0xBBD39F2D: "srsa",
    0x0D34474D: "srst",
    0x7A2A8A4C: "g1h",
    0xB1258984: "g1co",
    0xA1A36B1A: "g1n",
    0xEDEE7EBB: "kidsscndb",
}


@dataclass
class RdbHeader:
    magic: bytes
    version: bytes
    header_size: int
    system_id: int
    file_count: int
    database_id: int
    folder_path: str


@dataclass
class RdbEntry:
    index: int            # 0-based 순번
    pos: int              # RDB 내 엔트리 시작 오프셋
    entry_size: int
    data_size: int        # 0x0D → Location32, 0x11 → Location40
    file_size: int        # 압축 해제 후 원본 크기
    entry_type: int
    file_ktid: int
    type_info_ktid: int
    flags: int
    # 위치 메타 (.fdata 내 위치)
    fdata_id: int
    fdata_offset: int
    size_in_cont: int
    meta_start: int       # 위치 메타가 시작되는 RDB 오프셋 (패치 시 사용)

    @property
    def ext(self) -> str:
        return TYPE_KTID_EXT.get(self.type_info_ktid, "?")

    @property
    def storage_internal(self) -> bool:
        return bool(self.flags & (1 << 17))

    @property
    def compression(self) -> int:
        return (self.flags >> 20) & 0x3F


def parse_rdb_header(data: bytes) -> RdbHeader:
    if data[0:4] != b"_DRK":
        raise ValueError(f"RDB magic 불일치: {data[0:4]!r}")
    return RdbHeader(
        magic=data[0:4],
        version=data[4:8],
        header_size=struct.unpack_from("<I", data, 0x08)[0],
        system_id=struct.unpack_from("<I", data, 0x0C)[0],
        file_count=struct.unpack_from("<I", data, 0x10)[0],
        database_id=struct.unpack_from("<I", data, 0x14)[0],
        folder_path=data[0x18:0x20].split(b"\x00")[0].decode("ascii", "replace"),
    )


def parse_rdb_entries(data: bytes, header: RdbHeader) -> list[RdbEntry]:
    entries: list[RdbEntry] = []
    pos = header.header_size
    idx = 0
    n = len(data)
    while pos + 0x30 <= n:
        if data[pos:pos + 4] != b"IDRK":
            break  # 엔트리 배열 끝
        entry_size = struct.unpack_from("<Q", data, pos + 0x08)[0]
        data_size = struct.unpack_from("<Q", data, pos + 0x10)[0]
        file_size = struct.unpack_from("<Q", data, pos + 0x18)[0]
        entry_type = struct.unpack_from("<I", data, pos + 0x20)[0]
        file_ktid = struct.unpack_from("<I", data, pos + 0x24)[0]
        type_info = struct.unpack_from("<I", data, pos + 0x28)[0]
        flags = struct.unpack_from("<I", data, pos + 0x2C)[0]

        meta_start = pos + entry_size - data_size
        if data_size == 0x0D:
            fdata_offset = struct.unpack_from("<I", data, meta_start + 0x02)[0]
            size_in_cont = struct.unpack_from("<I", data, meta_start + 0x06)[0]
            fdata_id = struct.unpack_from("<H", data, meta_start + 0x0A)[0]
        elif data_size == 0x11:
            off_hi = data[meta_start + 0x02]
            off_lo = struct.unpack_from("<I", data, meta_start + 0x06)[0]
            fdata_offset = (off_hi << 32) | off_lo
            size_in_cont = struct.unpack_from("<I", data, meta_start + 0x0A)[0]
            fdata_id = struct.unpack_from("<H", data, meta_start + 0x0E)[0]
        else:
            # 알 수 없는 위치 메타 크기 — 위치 정보 없이 기록
            fdata_offset = size_in_cont = fdata_id = -1

        entries.append(RdbEntry(
            index=idx, pos=pos, entry_size=entry_size, data_size=data_size,
            file_size=file_size, entry_type=entry_type, file_ktid=file_ktid,
            type_info_ktid=type_info, flags=flags, fdata_id=fdata_id,
            fdata_offset=fdata_offset, size_in_cont=size_in_cont,
            meta_start=meta_start,
        ))
        # 엔트리는 4바이트 정렬로 연속 배치 → stride = align_up(entry_size, 4)
        pos += (entry_size + 3) & ~3
        idx += 1
    return entries


def parse_rdx(data: bytes) -> dict[int, int]:
    """root.rdx → {fdata_id: file_hash}. 헤더 없음, 8바이트 엔트리 배열."""
    mapping: dict[int, int] = {}
    for off in range(0, len(data) - 7, 8):
        fdata_id = struct.unpack_from("<H", data, off)[0]
        file_hash = struct.unpack_from("<I", data, off + 4)[0]
        mapping[fdata_id] = file_hash
    return mapping


def fdata_name(file_hash: int) -> str:
    return f"0x{file_hash:08x}.fdata"
