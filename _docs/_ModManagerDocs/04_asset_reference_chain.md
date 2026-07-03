# 에셋 간 참조 관계

KatanaEngine의 에셋들은 단독으로 존재하지 않고 체인으로 연결된다.
모드로 에셋을 교체할 때 어떤 파일들을 함께 수정해야 하는지 파악하기 위해
이 체인을 이해하는 것이 필수적이다.

---

## 전체 참조 체인 (캐릭터 예시)

```
캐릭터 씬 코드
  │
  └─► kidsobjdb (0x20A6A0BB)
        │
        ├─ IDOK [ModelData / 0x563BDEF1]
        │    file_ktid = G1M_FK        ← G1M 에셋
        │    prop[0xD2D2D5AF] = G1M_FK  (OIDResourceNameHash)
        │    prop[0xAD260326] = KTID_ObjId  ← G1M→KTID 링크 ★
        │
        ├─ IDOK [KtidFile / 0x8E39AA37]
        │    file_ktid = KTID_FK       ← .ktid 에셋
        │    prop[0xD2D2D5AF] = KTID_FK
        │    (KTID_ObjId == KTID_FK 또는 kidsobjdb 내 object_id)
        │    .ktid 파일 내용:
        │      [(slot=0, G1T_FK_A),
        │       (slot=1, G1T_FK_B),
        │       (slot=7, G1T_FK_C), ...]
        │
        ├─ IDOK [TexContext / 0xAFBEC60C]
        │    file_ktid = G1T_FK_A      ← G1T 에셋 A
        │
        ├─ IDOK [TexContext / 0xAFBEC60C]
        │    file_ktid = G1T_FK_B      ← G1T 에셋 B
        │
        └─ IDOK [TexContext / 0xAFBEC60C]
             file_ktid = G1T_FK_C      ← G1T 에셋 C
```

---

## kidsobjdb 바이너리 포맷

### DOK 헤더 (0x1C bytes)

```
+0x00   4B    "_DOK"
+0x04   4B    "0000"
+0x08   u32   hdr_size     (보통 0x1C)
+0x0C   u32   prop_Field0C (보통 0x0A)
+0x10   u32   elementsCount — IDOK + RDOK 레코드 총 수
+0x14   u32   prop_Name    — 루트 DB 이름 해시
+0x18   u32   declaredFileSize
```

### IDOK 서브레코드 헤더 (0x18 bytes)

```
+0x00   4B    "IDOK"
+0x04   4B    "0000"
+0x08   u32   declaredSize  — 이 IDOK 레코드 전체 크기
+0x0C   u32   prop_Name     — DOK 로컬 고유 ID (= object_id)
+0x10   u32   prop_TypeName — 타입 클래스 해시 (→ 03_ktid_system.md)
+0x14   u32   prop_PropsCount
// prop_PropsCount × 12B: 프로퍼티 메타 배열
//   각 항목: {type u32, arraySize u32, propHash u32}
// 이후: 프로퍼티 값 데이터 (순서대로 배치)
```

### RDOK 서브레코드 헤더 (0x1C bytes) — 크로스파일 참조

```
+0x00   4B    "RDOK"
+0x04   4B    "0000"
+0x08   u32   declaredSize
+0x0C   u32   prop_Name
+0x10   u32   prop_Hash2   — 참조하는 외부 kidsobjdb 파일 해시
+0x14   u32   prop_Hash3   — 외부 파일 내 대상 object 해시
+0x18   u32   prop_PropsCount
// prop_PropsCount × 12B + 값 데이터
```

### 프로퍼티 타입 → 단위 크기

```
Type 0 (SByte)   → 1B      Type 1 (Byte)   → 1B
Type 2 (Int16)   → 2B      Type 3 (UInt16) → 2B
Type 4 (Int32)   → 4B      Type 5 (UInt32) → 4B
Type 8 (Single)  → 4B
Type 10 (Vector4)→ 16B     Type 12 (Vector2)→ 8B    Type 13 (Vector3)→ 12B
Types 6,7,9,11: 미정의 (0B)
```

값 총 크기 = `unitSize × arraySize` (arraySize=0 → 0B)

---

## G1M 내부의 텍스처 슬롯 (섹션 0x10002)

G1M 파일의 `0x10002` 섹션은 서브메쉬(폴리곤 그룹)와 머티리얼의 매핑을 담는다.
각 머티리얼 엔트리에는 `texIndex` 필드가 있으며, 이 값이 `.ktid` 파일의 `slot`과 대응한다.

```
G1M 섹션 0x10002
  머티리얼 엔트리 i:
    texIndex = 7   →  .ktid 파일의 slot=7에 해당하는 G1T_FK 사용
```

---

## 게임별 참조 체인 차이

### DOA6: objId 경유

kidsobjdb의 G1M IDOK에서 `prop[0xAD260326]`은 **같은 DOK 내의 다른 IDOK의 `prop_Name`** 을 가리킨다.

```
G1M IDOK: prop_Name=0xBFB20001, prop[0xAD260326]=0x388720F6
KTID IDOK: prop_Name=0x388720F6  ← 이 IDOK의 FileKtid가 .ktid 에셋
```

### Ronin / 다른 게임: FileKtid 직접 참조

`prop[0xAD260326]` 값이 곧 KTID 에셋의 FileKtid일 수 있다.
DOK 내에 해당 objId를 가진 IDOK가 없을 경우 이 방식으로 폴백.

---

## 모드로 G1M을 교체할 때 수정해야 하는 파일

### Case 1: 기존 텍스처 슬롯 수/레이아웃 유지

교체할 파일: **G1M만**

기존 `.ktid`와 G1T들이 그대로 호환되므로 G1M 파일만 교체하면 된다.

### Case 2: 텍스처 슬롯 수/레이아웃 변경

교체할 파일: **G1M + .ktid + G1T(들) + kidsobjdb**

```
1. 새 G1M 제작 (새 texIndex 레이아웃)
2. 새 .ktid 파일 제작 (새 slot → G1T_FK 매핑)
3. 새 G1T 파일 준비 (실제 텍스처)
4. kidsobjdb 수정:
   - G1M IDOK의 prop[0xAD260326] → 새 KTID objId 로 업데이트
   - 새 KTID IDOK 추가 (또는 기존 것 교체)
   - 새 G1T IDOK 추가 (또는 기존 것 교체)
```

### Case 3: 완전히 새로운 캐릭터 추가

가장 복잡한 경우. 새 FileKtid들을 할당하고, RDB에 모든 에셋을 등록하고,
씬 DB까지 수정해야 한다. 이론상 가능하나 미검증.

---

## G1T 파일 포맷 간략 요약

```
[0x00] "GT1G"              magic
[0x04] version             (DOA6: "0600", Yumia: "1600")
[0x0C] header_size         u32 = 텍스처 오프셋 테이블 기준점
[0x14] tex_count           u32
[header_size + i*4]        텍스처 i의 오프셋 (header_size 기준)

각 텍스처 엔트리 (ep = 엔트리 포인터):
  [ep+0] byte0: (mipCount<<4) | flags
  [ep+1] fmt_code: BC1=0x59, BC4=0x5A, BC5=0x5C, BC6H=0x5E, BC7=0x5F
  [ep+2] dim: (log2_h<<4) | log2_w  ← 상위 nibble=높이, 하위=너비
  [ep+8] ext_size u32  (DOA6: 항상 0x0C)
  [ep+8+ext_size] 픽셀 데이터
```
