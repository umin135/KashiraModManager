"""
KatanaEngine .fdata IDRK 블록 리더/라이터 + zlibext 코덱 (검증용).

오프셋 근거: _docs/_ModManagerDocs/02_rdb_fdata_format.md §3.
payload 위치는 반드시 "블록 끝에서 compressed_size 만큼" 으로 계산한다.
"""

from __future__ import annotations

import struct
import zlib
from dataclasses import dataclass

IDRK_HEADER_SIZE = 0x58


@dataclass
class IdrkBlock:
    total_block_size: int
    compressed_size: int
    uncompressed_size: int
    param_data_size: int
    header: bytes          # 원본 0x58 헤더 (재사용/참고용)
    payload: bytes         # zlibext 압축 payload (raw)


# ---------------- zlibext 코덱 ----------------

def decompress_zlibext(payload: bytes, expected_size: int | None = None) -> bytes:
    """
    zlibext → 원본 바이트.
    청크 = [u16 zlib_stream_size][8B 미상(청크별 해시 추정)][zlib 스트림(78 9c…adler32)]
    실측(DOA6): raw deflate 가 아니라 full zlib 스트림이다.
    """
    out = bytearray()
    pos = 0
    n = len(payload)
    while pos + 0x0A <= n:
        chunk_size = struct.unpack_from("<H", payload, pos)[0]
        start = pos + 0x0A  # u16(2) + 미상(8)
        end = start + chunk_size
        if chunk_size == 0 or end > n:
            break
        out += zlib.decompress(payload[start:end])  # wbits 기본(15) → zlib 헤더 처리
        pos = end
        if expected_size is not None and len(out) >= expected_size:
            break
    return bytes(out)


def compress_zlibext(data: bytes, chunk_size: int = 0x4000, level: int = 9) -> bytes:
    """
    원본 바이트 → zlibext payload.
    청크당 full zlib 스트림 생성. 8B 미상 필드는 0으로 채운다.
    (엔진이 이 8B를 검증하는지는 미확인 — IDRK 무결성 오픈 이슈. Step 2에서 판명.)
    """
    result = bytearray()
    if not data:
        data = b""
    for i in range(0, len(data), chunk_size):
        chunk = data[i:i + chunk_size]
        stream = zlib.compress(chunk, level)  # full zlib (78 9c … adler32)
        result += struct.pack("<H", len(stream)) + b"\x00" * 8 + stream
    return bytes(result)


# ---------------- IDRK 블록 read/build ----------------

def read_idrk_block(fdata: bytes, block_start: int) -> IdrkBlock:
    if fdata[block_start:block_start + 4] != b"IDRK":
        raise ValueError(f"IDRK magic 불일치 @0x{block_start:x}: {fdata[block_start:block_start+4]!r}")
    total = struct.unpack_from("<Q", fdata, block_start + 0x08)[0]
    comp = struct.unpack_from("<Q", fdata, block_start + 0x10)[0]
    uncomp = struct.unpack_from("<Q", fdata, block_start + 0x18)[0]
    pds = struct.unpack_from("<I", fdata, block_start + 0x20)[0]
    header = fdata[block_start:block_start + IDRK_HEADER_SIZE]
    block_end = block_start + total
    payload = fdata[block_end - comp:block_end]  # ★ 끝에서 compressed_size 만큼
    return IdrkBlock(total, comp, uncomp, pds, header, payload)


def extract_asset(fdata: bytes, block_start: int) -> bytes:
    """IDRK 블록 → 원본 에셋 바이트 (압축 해제 포함)."""
    blk = read_idrk_block(fdata, block_start)
    return decompress_zlibext(blk.payload, blk.uncompressed_size)


def build_idrk_block(raw: bytes, template_header: bytes | None = None,
                     chunk_size: int = 0x4000) -> bytes:
    """
    원본 에셋 바이트 → 완성된 IDRK 블록 (헤더 0x58 + payload).
    template_header: 있으면 param 등 세부 필드를 그대로 복사 (동일 타입 원본 헤더 권장).
    """
    payload = compress_zlibext(raw, chunk_size)
    total = IDRK_HEADER_SIZE + len(payload)

    if template_header is not None and len(template_header) >= IDRK_HEADER_SIZE:
        header = bytearray(template_header[:IDRK_HEADER_SIZE])
    else:
        header = bytearray(IDRK_HEADER_SIZE)
        header[0:4] = b"IDRK"
        header[4:8] = b"0000"
        struct.pack_into("<I", header, 0x20, 0)  # param_data_size=0

    struct.pack_into("<Q", header, 0x08, total)
    struct.pack_into("<Q", header, 0x10, len(payload))
    struct.pack_into("<Q", header, 0x18, len(raw))
    return bytes(header) + payload
