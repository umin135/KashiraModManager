"""
Step 0 정찰: root.rdb / root.rdx 를 파싱해 검증에 필요한 지형을 파악한다.

- RDB 헤더 요약
- data_size(위치 메타 종류) 분포
- type_info_ktid(에셋 타입) 히스토그램
- fdata_id → 파일명 매핑 (rdx)
- 전체 엔트리를 CSV로 덤프 (이후 Step에서 A/B 후보 조회용)

사용법:
  python step0_recon.py "G:/SteamLibrary/steamapps/common/Dead or Alive 6 Last Round/fdata_package" [out_dir]
"""

import csv
import os
import sys
from collections import Counter

# Windows 콘솔(cp949)에서 유니코드 출력 허용
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

from katana_rdb import (
    TYPE_KTID_EXT, parse_rdb_header, parse_rdb_entries, parse_rdx, fdata_name,
)


def main() -> None:
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    pkg = sys.argv[1]
    out_dir = sys.argv[2] if len(sys.argv) > 2 else "."
    os.makedirs(out_dir, exist_ok=True)

    rdb_path = os.path.join(pkg, "root.rdb")
    rdx_path = os.path.join(pkg, "root.rdx")

    with open(rdb_path, "rb") as f:
        rdb = f.read()
    with open(rdx_path, "rb") as f:
        rdx = f.read()

    header = parse_rdb_header(rdb)
    entries = parse_rdb_entries(rdb, header)
    id_to_hash = parse_rdx(rdx)

    print("=" * 60)
    print("RDB 헤더")
    print("=" * 60)
    print(f"  version      : {header.version!r}")
    print(f"  header_size  : 0x{header.header_size:x}")
    print(f"  system_id    : {header.system_id}")
    print(f"  file_count   : {header.file_count}  (헤더 선언값)")
    print(f"  database_id  : 0x{header.database_id:08x}")
    print(f"  folder_path  : {header.folder_path!r}")
    print(f"  파싱된 엔트리 : {len(entries)}  (실제 순차 파싱)")
    print(f"  RDB 파일 크기 : {len(rdb)} bytes")
    print()

    # data_size 분포
    ds = Counter(e.data_size for e in entries)
    print("data_size(위치 메타) 분포:")
    for k, v in sorted(ds.items()):
        label = {0x0D: "Location32(<4GB)", 0x11: "Location40(>=4GB)"}.get(k, "UNKNOWN")
        print(f"  0x{k:02x} {label:20s}: {v}")
    print()

    # 타입 히스토그램
    types = Counter(e.type_info_ktid for e in entries)
    print("type_info_ktid(에셋 타입) 히스토그램 (상위 30):")
    print(f"  {'typeKtid':12s} {'ext':10s} {'count':>8s}")
    for tk, cnt in types.most_common(30):
        ext = TYPE_KTID_EXT.get(tk, "?")
        print(f"  0x{tk:08x}   {ext:10s} {cnt:>8d}")
    print(f"  (총 고유 타입 수: {len(types)})")
    print()

    # rdx 요약
    print(f"root.rdx: {len(id_to_hash)} 개 fdata 인덱스")
    used_ids = sorted(id_to_hash)
    print(f"  fdata_id 범위: {used_ids[0]} ~ {used_ids[-1]}")
    all_ids = set(used_ids)
    gaps = [i for i in range(used_ids[0], used_ids[-1] + 1) if i not in all_ids]
    print(f"  범위 내 미사용 id: {len(gaps)} 개 {('예: ' + str(gaps[:10])) if gaps else ''}")
    print(f"  최초 미사용(추천 새 id): {used_ids[-1] + 1}")
    print()

    # RDB가 참조하지만 rdx에 없는 fdata_id (있으면 이상)
    rdb_ids = set(e.fdata_id for e in entries if e.fdata_id >= 0)
    missing = sorted(rdb_ids - all_ids)
    if missing:
        print(f"  ⚠ RDB가 참조하나 rdx에 없는 fdata_id: {missing[:20]}")
    else:
        print("  ✓ RDB의 모든 fdata_id가 rdx에 존재")
    print()

    # 전체 엔트리 CSV 덤프
    csv_path = os.path.join(out_dir, "root_rdb_entries.csv")
    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow([
            "index", "pos", "file_ktid", "type_ktid", "ext",
            "data_size", "file_size", "fdata_id", "fdata_name",
            "fdata_offset", "size_in_cont", "flags",
        ])
        for e in entries:
            fh = id_to_hash.get(e.fdata_id)
            w.writerow([
                e.index, e.pos, f"0x{e.file_ktid:08x}", f"0x{e.type_info_ktid:08x}",
                e.ext, f"0x{e.data_size:x}", e.file_size, e.fdata_id,
                fdata_name(fh) if fh is not None else "",
                e.fdata_offset, e.size_in_cont, f"0x{e.flags:08x}",
            ])
    print(f"전체 엔트리 CSV 저장: {csv_path}  ({len(entries)} rows)")


if __name__ == "__main__":
    main()
