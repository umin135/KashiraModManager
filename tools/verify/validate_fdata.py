"""
fdata 포맷 검증: 실제 IDRK 블록을 읽어 압축 해제 → 에셋 매직 확인,
그리고 compress→decompress 왕복 무결성 확인.

사용법:
  python validate_fdata.py "<fdata_package 경로>"
"""

import os
import struct
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

from katana_fdata import (
    read_idrk_block, extract_asset, decompress_zlibext, compress_zlibext,
    build_idrk_block,
)

# Step 0 CSV에서 뽑은 작은 g1t 엔트리 (검증용)
SAMPLES = [
    # (file_ktid, fdata_name, offset, size_in_cont, file_size, magic_hint)
    (0x8daee560, "0x8baaa1ce.fdata", 25120, 136, 48, b"GT1G"),
    (0xd8071e4f, "0x8baaa1ce.fdata", 10715392, 142, 56, b"GT1G"),
]


def check(label: str, cond: bool, extra: str = "") -> bool:
    mark = "✓" if cond else "✗"
    print(f"  [{mark}] {label}{('  ' + extra) if extra else ''}")
    return cond


def main() -> None:
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    pkg = sys.argv[1]

    all_ok = True
    for ktid, name, offset, size_in_cont, file_size, magic in SAMPLES:
        print("=" * 60)
        print(f"file_ktid=0x{ktid:08x}  {name}  off={offset}  block={size_in_cont}  raw={file_size}")
        path = os.path.join(pkg, name)
        with open(path, "rb") as f:
            f.seek(offset)
            # 블록 헤더+payload를 넉넉히 읽되, 파일 경계 고려
            chunk = f.read(size_in_cont + 0x100)
        blk = read_idrk_block(chunk, 0)

        all_ok &= check(f"IDRK magic", chunk[0:4] == b"IDRK")
        all_ok &= check(f"total_block_size == size_in_cont",
                        blk.total_block_size == size_in_cont,
                        f"{blk.total_block_size} vs {size_in_cont}")
        all_ok &= check(f"uncompressed_size == file_size",
                        blk.uncompressed_size == file_size,
                        f"{blk.uncompressed_size} vs {file_size}")

        raw = extract_asset(chunk, 0)
        all_ok &= check(f"압축 해제 크기 == file_size",
                        len(raw) == file_size, f"{len(raw)} vs {file_size}")
        all_ok &= check(f"에셋 매직 {magic!r}", raw[0:4] == magic, f"실제 {raw[0:4]!r}")

        # 왕복: raw → zlibext → raw
        rt = decompress_zlibext(compress_zlibext(raw), len(raw))
        all_ok &= check("compress→decompress 왕복 일치", rt == raw)

        # 블록 재빌드 → 다시 추출 무결성 (헤더 템플릿 재사용)
        rebuilt = build_idrk_block(raw, template_header=blk.header)
        raw2 = extract_asset(rebuilt, 0)
        all_ok &= check("build_idrk_block 재추출 일치", raw2 == raw,
                        f"rebuilt_block={len(rebuilt)}B (원본 {size_in_cont}B)")
        print()

    print("=" * 60)
    print("전체 결과:", "✅ PASS" if all_ok else "❌ FAIL")


if __name__ == "__main__":
    main()
