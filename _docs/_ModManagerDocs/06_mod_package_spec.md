# 모드 패키지 포맷 명세 (제안)

이 문서는 KatanaEngine 범용 모드 매니저를 위한 **모드 패키지 포맷(.kmod)**
설계 제안이다. 구현되지 않은 초안이며, 실제 구현 시 변경될 수 있다.

---

## 설계 원칙

1. **원본 불변**: 게임 원본 파일(RDB/FDATA)을 직접 수정하지 않는다
2. **단순한 패키지**: 모드 제작자가 복잡한 포맷을 몰라도 에셋 파일만 있으면 패키지 가능
3. **모드 우선순위**: 여러 모드가 같은 에셋을 교체할 때 우선순위로 충돌 해결
4. **게임별 호환**: 동일한 모드 패키지를 DOA6/Yumia 등 여러 게임에서 사용 가능

---

## 파일 구조

```
mymod.kmod   (ZIP 호환 아카이브)
├── mod.json          ← 모드 메타데이터 + 교체 매핑 (필수)
└── assets/
    ├── 0xABCD1234.g1m    ← 교체할 에셋 원본 (압축 전 원본 바이너리)
    ├── 0x11223344.g1t    ← 파일명 = file_ktid (hex, 소문자)
    ├── 0x55667788.ktid
    └── ...
```

`.kmod` 파일은 ZIP 포맷으로, 파일 탐색기에서 `.zip`으로 열어 내용 확인 가능.

---

## mod.json 스키마

```json
{
  "name": "Kasumi Summer Costume",
  "author": "modder_name",
  "version": "1.0.0",
  "description": "Replaces KAS0001 default costume with summer outfit",
  "gameCompatibility": ["doa6", "doa6lr"],

  "replacements": [
    {
      "fileKtid": "0xABCD1234",
      "assetPath": "assets/0xABCD1234.g1m",
      "typeKtid": "0x563BDEF1",
      "comment": "Kasumi body model"
    },
    {
      "fileKtid": "0x11223344",
      "assetPath": "assets/0x11223344.g1t",
      "typeKtid": "0xAFBEC60C",
      "comment": "Kasumi body texture"
    }
  ],

  "dependencies": [],

  "priority": 100
}
```

### 필드 설명

| 필드 | 타입 | 설명 |
|------|------|------|
| `name` | string | 모드 표시 이름 |
| `author` | string | 제작자 |
| `version` | string | semver 형식 권장 |
| `gameCompatibility` | string[] | 호환 게임 ID 목록 |
| `replacements` | array | 교체 에셋 목록 |
| `replacements[].fileKtid` | string | 교체 대상 에셋의 FileKtid (hex 문자열) |
| `replacements[].assetPath` | string | 패키지 내 에셋 경로 |
| `replacements[].typeKtid` | string | 에셋 타입 (03_ktid_system.md 참조) |
| `priority` | int | 충돌 시 우선순위 (높을수록 우선, 기본 100) |

---

## 모드 매니저 동작 흐름

### 1. 설치 단계

```
사용자: 모드 활성화
  ↓
매니저: 활성 모드 목록 + priority 정렬
  ↓
매니저: 충돌 감지 (같은 fileKtid를 교체하는 모드가 2개 이상)
  → 사용자에게 경고 + priority 기준 자동 해결
  ↓
매니저: 각 모드의 에셋을 zlibext 압축 → IDRK 블록 생성
  ↓
매니저: 모든 IDRK 블록을 하나의 "mods.fdata"에 패킹
  ↓
매니저: "mods.fdata"를 fdata_package/에 복사
매니저: 패치된 root.rdb 생성 (원본 엔트리 리다이렉트)
매니저: 패치된 root.rdx 생성 (mods fdata_id 추가)
  ↓
게임 실행 (패치된 RDB로)
```

### 2. 제거 단계

```
사용자: 모드 비활성화 또는 매니저 제거
  ↓
mods.fdata 삭제
패치된 RDB/RDX 삭제 (또는 원본으로 복원)
  ↓
원본 파일 불변 → 게임 정상 동작
```

---

## 패치된 RDB 생성 알고리즘

```python
def patch_rdb(original_rdb: bytes, replacements: dict[int, ModAsset]) -> bytes:
    """
    replacements: {file_ktid: ModAsset(fdata_id, fdata_offset, size_in_cont, file_size)}
    """
    result = bytearray(original_rdb)

    pos = read_u32(result, 8)  # header_size
    while pos < len(result):
        if result[pos:pos+4] != b'IDRK':
            break
        entry_size = read_u64(result, pos + 8)
        data_size  = read_u64(result, pos + 0x10)
        file_ktid  = read_u32(result, pos + 0x24)

        if file_ktid in replacements:
            mod = replacements[file_ktid]
            meta_start = pos + entry_size - data_size

            if data_size == 0x0D:
                write_u32(result, meta_start + 0x02, mod.fdata_offset)
                write_u32(result, meta_start + 0x06, mod.size_in_cont)
                write_u16(result, meta_start + 0x0A, mod.fdata_id)
            elif data_size == 0x11:
                # 4GB 이상 오프셋 처리
                write_u8(result,  meta_start + 0x02, (mod.fdata_offset >> 32) & 0xFF)
                write_u32(result, meta_start + 0x06, mod.fdata_offset & 0xFFFFFFFF)
                write_u32(result, meta_start + 0x0A, mod.size_in_cont)
                write_u16(result, meta_start + 0x0E, mod.fdata_id)

            # file_size 업데이트
            write_u64(result, pos + 0x18, mod.file_size)

        pos += entry_size

    return bytes(result)
```

---

## 게임 ID 규약 (제안)

| 게임 | game ID |
|------|---------|
| Dead or Alive 6 Last Round | `doa6lr` |
| Dead or Alive 6 | `doa6` |
| Atelier Yumia | `yumia` |
| Nioh 2 | `nioh2` |
| Wo Long: Fallen Dynasty | `wolong` |

게임 감지는 게임 실행 파일명 또는 root.rdb의 `database_id`로 수행 가능.

---

## 미해결 과제

1. **Last-wins 검증**: 전략 C([05_mod_strategies.md](05_mod_strategies.md))가 작동한다면
   RDB 패치 없이 단순히 `file_count`만 늘리고 엔트리를 끝에 추가하는 방식으로 단순화 가능.

2. **IDRK 무결성 검증**: 엔진이 IDRK 블록의 해시/CRC를 체크하는지 확인 필요.
   체크한다면 수정된 블록에 맞는 체크섬 재계산 로직 필요.

3. **kidsobjdb 자동 패치**: 모드가 G1M의 텍스처 슬롯 구성을 바꿀 경우,
   해당 G1M을 참조하는 모든 kidsobjdb도 갱신해야 한다.
   kidsobjdb가 어느 fdata에 있는지 찾는 역방향 인덱스 구축 필요.

4. **Steam Workshop 통합**: Steam Workshop API로 모드 배포/업데이트 자동화 가능.
