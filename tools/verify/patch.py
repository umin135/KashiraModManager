"""
검증용 RDB/RDX/FDATA 패처. 3가지 모드 지원. 원본은 절대 수정하지 않고
out_dir 에 패치본(root.rdb / root.rdx / mods.fdata)을 생성한다.

모드:
  step1  target_ktid source_ktid
         커스텀 fdata 없이 target 엔트리를 source 의 기존 위치로 in-place 리다이렉트.
         → root.rdb 만 출력. (Q1: 엔진이 RDB 위치를 신뢰하는가)

  step2  target_ktid asset_file
         asset_file 로 mods.fdata(IDRK 블록) 생성 + rdx 새 fdata_id 등록 +
         target 엔트리를 mods.fdata 로 리다이렉트.
         → root.rdb / root.rdx / mods.fdata 출력. (Q2: 커스텀 fdata 경로)

  step3  target_ktid asset_file
         target 원본 엔트리 유지 + RDB 끝에 같은 file_ktid 중복 엔트리 append +
         file_count += 1. mods.fdata/rdx 는 step2 와 동일.
         → root.rdb / root.rdx / mods.fdata 출력. (Q3: last-wins)

사용법:
  python patch.py <pkg> <out_dir> step1 <target_ktid> <source_ktid>
  python patch.py <pkg> <out_dir> step2 <target_ktid> <asset_file>
  python patch.py <pkg> <out_dir> step3 <target_ktid> <asset_file>

ktid 는 0x... 16진수 또는 10진수.
"""

import os
import struct
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

from katana_rdb import (
    parse_rdb_header, parse_rdb_entries, parse_rdx, fdata_name, RdbEntry,
)
from katana_fdata import build_idrk_block, read_idrk_block

# mods.fdata 파일명 해시 (미사용 값, 게임 내 어떤 fdata 와도 충돌하지 않아야 함)
MODS_FILE_HASH = 0xCA5E0001  # 'kashira mods'


def parse_ktid(s: str) -> int:
    return int(s, 16) if s.lower().startswith("0x") else int(s)


def find_entry(entries: list[RdbEntry], ktid: int) -> RdbEntry:
    matches = [e for e in entries if e.file_ktid == ktid]
    if not matches:
        raise SystemExit(f"file_ktid 0x{ktid:08x} 를 RDB 에서 찾을 수 없음")
    if len(matches) > 1:
        print(f"  ⚠ file_ktid 0x{ktid:08x} 가 이미 {len(matches)}개 존재 (바닐라 아님?)")
    return matches[0]


def write_location(buf: bytearray, e: RdbEntry, fdata_id: int, offset: int,
                   size_in_cont: int, file_size: int) -> None:
    """엔트리 e 의 위치 메타 + file_size 를 덮어쓴다 (data_size==0x0D 전용)."""
    if e.data_size != 0x0D:
        raise SystemExit(f"data_size 0x{e.data_size:x} 는 이 도구 미지원 (Location32 전용)")
    m = e.meta_start
    struct.pack_into("<I", buf, m + 0x02, offset)
    struct.pack_into("<I", buf, m + 0x06, size_in_cont)
    struct.pack_into("<H", buf, m + 0x0A, fdata_id)
    struct.pack_into("<Q", buf, e.pos + 0x18, file_size)  # file_size


def build_mods(pkg: str, asset_path: str, template_entry: RdbEntry):
    """asset_path → mods.fdata 바이트 + 블록 크기. 템플릿 헤더는 target 원본 블록에서."""
    with open(asset_path, "rb") as f:
        raw = f.read()
    # target 원본 IDRK 헤더를 템플릿으로 재사용 (param 등 세부 필드 보존 시도)
    tmpl = None
    try:
        src_name = None  # 템플릿은 target 자신의 블록에서 가져온다
        # target 의 fdata 파일에서 블록 헤더 읽기
        from katana_rdb import parse_rdx as _prdx
        rdx = open(os.path.join(pkg, "root.rdx"), "rb").read()
        id2hash = _prdx(rdx)
        src_name = fdata_name(id2hash[template_entry.fdata_id])
        with open(os.path.join(pkg, src_name), "rb") as f:
            f.seek(template_entry.fdata_offset)
            hdr_chunk = f.read(0x58)
        tmpl = hdr_chunk
    except Exception as ex:
        print(f"  (템플릿 헤더 로드 실패, 기본 헤더 사용: {ex})")

    block = build_idrk_block(raw, template_header=tmpl)
    # 무결성 자체검증
    assert read_idrk_block(block, 0).uncompressed_size == len(raw)
    return block, len(block), len(raw)


def add_rdx_entry(rdx: bytes, new_id: int, file_hash: int) -> bytes:
    return rdx + struct.pack("<HHI", new_id, 0xFFFF, file_hash)


def main() -> None:
    if len(sys.argv) < 5:
        print(__doc__)
        sys.exit(1)
    pkg, out_dir, mode = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out_dir, exist_ok=True)

    rdb = bytearray(open(os.path.join(pkg, "root.rdb"), "rb").read())
    rdx = open(os.path.join(pkg, "root.rdx"), "rb").read()
    header = parse_rdb_header(rdb)
    entries = parse_rdb_entries(rdb, header)
    id2hash = parse_rdx(rdx)
    new_id = max(id2hash) + 1

    if MODS_FILE_HASH in id2hash.values():
        raise SystemExit(f"MODS_FILE_HASH 0x{MODS_FILE_HASH:08x} 가 이미 사용 중 — 다른 값 필요")

    target = find_entry(entries, parse_ktid(sys.argv[4]))
    print(f"target: 0x{target.file_ktid:08x} ({target.ext}) "
          f"fdata_id={target.fdata_id} off={target.fdata_offset} "
          f"size={target.size_in_cont} file_size={target.file_size}")

    out_rdb = os.path.join(out_dir, "root.rdb")
    out_rdx = os.path.join(out_dir, "root.rdx")
    out_fdata = os.path.join(out_dir, fdata_name(MODS_FILE_HASH))

    if mode == "step1":
        source = find_entry(entries, parse_ktid(sys.argv[5]))
        print(f"source: 0x{source.file_ktid:08x} ({source.ext}) "
              f"fdata_id={source.fdata_id} off={source.fdata_offset} "
              f"size={source.size_in_cont} file_size={source.file_size}")
        write_location(rdb, target, source.fdata_id, source.fdata_offset,
                       source.size_in_cont, source.file_size)
        open(out_rdb, "wb").write(rdb)
        print(f"\n생성: {out_rdb}")
        print("배치: root.rdb 백업 후 이 파일로 교체. rdx/fdata 변경 없음.")

    elif mode in ("step2", "step3"):
        asset = sys.argv[5]
        block, block_size, raw_size = build_mods(pkg, asset, target)
        open(out_fdata, "wb").write(block)
        print(f"mods.fdata: {os.path.basename(out_fdata)} "
              f"({block_size}B block, raw {raw_size}B), 새 fdata_id={new_id}")

        if mode == "step2":
            write_location(rdb, target, new_id, 0, block_size, raw_size)
        else:  # step3: 원본 유지 + 중복 엔트리 append
            stride = (target.entry_size + 3) & ~3
            clone = bytearray(rdb[target.pos:target.pos + stride])
            ne = RdbEntry(  # append 위치 기준으로 meta_start 재계산
                index=-1, pos=len(rdb), entry_size=target.entry_size,
                data_size=target.data_size, file_size=target.file_size,
                entry_type=target.entry_type, file_ktid=target.file_ktid,
                type_info_ktid=target.type_info_ktid, flags=target.flags,
                fdata_id=0, fdata_offset=0, size_in_cont=0,
                meta_start=len(rdb) + (target.meta_start - target.pos),
            )
            rdb += clone
            write_location(rdb, ne, new_id, 0, block_size, raw_size)
            struct.pack_into("<I", rdb, 0x10, header.file_count + 1)  # file_count++
            print(f"append: 중복 엔트리 @pos={ne.pos}, file_count {header.file_count} → {header.file_count+1}")

        open(out_rdb, "wb").write(rdb)
        open(out_rdx, "wb").write(add_rdx_entry(rdx, new_id, MODS_FILE_HASH))
        print(f"\n생성: {out_rdb}\n     {out_rdx}\n     {out_fdata}")
        print("배치: root.rdb / root.rdx 백업 후 교체, mods.fdata 를 fdata_package/ 에 복사.")
    else:
        raise SystemExit(f"알 수 없는 mode: {mode}")


if __name__ == "__main__":
    main()
