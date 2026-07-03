# KTID 해시 시스템

KTID(KaTana ID)는 KatanaEngine에서 에셋과 타입을 식별하는 32비트 해시다.

---

## KTID의 두 가지 용도

### 1. FileKtid — 에셋 고유 ID

특정 파일(에셋)을 식별하는 해시. RDB의 `file_ktid` 필드, kidsobjdb의
`OIDResourceNameHash` 프로퍼티 등에서 사용된다.

원본 파일명에서 생성되며, 게임 내에서는 파일명 없이 이 숫자만으로 에셋을 참조한다.

### 2. TypeKtid — 타입 클래스 ID

에셋의 타입(파일 형식)을 식별하는 해시. RDB의 `type_info_ktid` 필드,
kidsobjdb의 `prop_TypeName` 필드에서 사용된다.

---

## 해시 알고리즘

파일명 → FileKtid 변환에는 두 가지 변형이 존재한다.

### 변형 A (DOA6 계열)

```python
def ktid_hash_a(name: str) -> int:
    """대소문자 구분 없이 소문자 변환 후 해시"""
    h = 0xFFFFFFFF
    for c in name.lower().encode('ascii'):
        h ^= c
        for _ in range(8):
            if h & 1:
                h = (h >> 1) ^ 0xEDB88320
            else:
                h >>= 1
    return (~h) & 0xFFFFFFFF
```

### 변형 B (Yumia / .name 파일 계열)

변형 A와 동일한 알고리즘이지만 입력 문자열 처리 방식이 다름.
.name 파일의 IRNK String1에서 확장자를 제거한 베이스명을 입력으로 사용.

> 두 변형 모두 CRC32 계열이나 표준 CRC32와는 다름.
> DOA6 검증 완료: 변형 A로 `root.rdb`의 file_ktid 재현 확인.

---

## TypeKtid → 파일 확장자 매핑

| TypeKtid | 확장자 | 설명 |
|----------|--------|------|
| `0x563BDEF1` | `.g1m` | 3D 모델 (ModelData) |
| `0xAFBEC60C` | `.g1t` | 텍스처 컨테이너 (TexContext A) |
| `0xAD57EBBA` | `.g1t` | 텍스처 컨테이너 (TexContext B) |
| `0x6FA91671` | `.g1a` | 스켈레탈 애니메이션 (G1AFile) |
| `0x7BCD279F` | `.g1s` | 스켈레톤 (G1SFile) |
| `0x5153729B` | `.mtl` | 머티리얼 정의 |
| `0x8E39AA37` | `.ktid` | 텍스처 슬롯 목록 (KtidFile) |
| `0x20A6A0BB` | `.kidsobjdb` | 오브젝트 의존성 DB |
| `0xBBD39F2D` | `.srsa` | 사운드 아카이브 |
| `0x0D34474D` | `.srst` | 사운드 스트림 |
| `0x7A2A8A4C` | `.g1h` | 히트박스/콜리전 |
| `0xB1258984` | `.g1co` | 클로스 시뮬레이션 파라미터 |
| `0xA1A36B1A` | `.g1n` | 지형/네비게이션 |
| `0xEDEE7EBB` | `.kidsscndb` | 씬 오브젝트 DB |

---

## kidsobjdb 관련 알려진 해시

### TypeName (prop_TypeName) 해시

```
0x563BDEF1 = "ModelData"              G1M
0xAFBEC60C = "TexContext"             G1T (변형 A)
0xAD57EBBA = "TexContext"             G1T (변형 B)
0x8E39AA37 = "KtidFile"              KTID 파일
0x20A6A0BB = "ObjectDatabaseFile"    kidsobjdb
0x6FA91671 = "G1AFile"               G1A 애니메이션
0x7BCD279F = "G1SFile"               G1S 스켈레톤
```

### PropName (propHash) 해시

```
0xD2D2D5AF = "OIDResourceNameHash"    FileKtid 참조 (첫 번째 프로퍼티)
0xAD260326 = "PropKtidLink"           G1M → KTID object_id 링크
0xBAF0DF79 = "EnableModelGroupBuffer" 모델 그룹 버퍼 플래그
0xD3C00659 = "ShadowCasterAlphaThreshold"
0xD69D6C64 = "ZeroLevel"
0x0FDA9260 = "CeilPower"
0x2841F996 = "UC_AM_ChangeToLowRange"  씬 LOD 거리
0x8F04DB22 = "UC_AM_ChangeToMidRange"  씬 LOD 거리
```

---

## KTID 파일 포맷 (.ktid)

`.ktid` 파일은 텍스처 슬롯 번호와 G1T FileKtid의 매핑 테이블이다.

```
구조: (slot u32, g1t_file_ktid u32) 쌍의 배열
크기: 파일 크기 / 8 = 슬롯 수

예시 (8바이트 단위):
  00 00 00 00  AB CD EF 01   → slot=0, g1t=0x01EFCDAB
  01 00 00 00  12 34 56 78   → slot=1, g1t=0x78563412
  07 00 00 00  9A BC DE F0   → slot=7, g1t=0xF0DEBC9A
```

> slot 값이 65535(0xFFFF)를 초과하면 유효하지 않은 파일로 간주.

---

## FileKtid 계산 예시

```python
# DOA6 변형 A 예시
name = "CHA_KAS0001_body"
ktid = ktid_hash_a(name)  # → 어떤 u32 값

# 확장자는 포함하지 않음 (일반적으로)
# 일부 경우 확장자 포함 여부가 게임마다 다를 수 있음 — 검증 필요
```

알려진 파일명이 없는 DOA6의 경우, 파일명 복원은 exe 내 문자열 스캔이나
브루트포스 방식으로만 가능하다.
