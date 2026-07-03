# KatanaEngine 에셋 시스템 개요

## 전체 구조

KatanaEngine은 에셋을 두 계층으로 관리한다.

```
[인덱스 계층]
  fdata_package/
  ├── root.rdb      ← 에셋 메타데이터 DB (file_ktid → 위치 정보)
  └── root.rdx      ← fdata_id → 파일명 해시 인덱스

[데이터 계층]
  fdata_package/
  ├── 0x1a2b3c4d.fdata   ← 수백 MB 컨테이너 (수백~수천 개 에셋 포함)
  ├── 0x9f8e7d6c.fdata
  └── ...                 (수백 개, 총 수십~수백 GB)
```

### 에셋 로드 흐름

```
게임 코드: LoadAsset(file_ktid=0xABCD1234)
    │
    ▼
RDB 검색: file_ktid=0xABCD1234 → {fdata_id=7, fdata_offset=0x3F200, size=0x8000}
    │
    ▼
RDX 조회: fdata_id=7 → file_hash=0x9f8e7d6c → "0x9f8e7d6c.fdata"
    │
    ▼
.fdata 파일 열기 → offset=0x3F200 읽기 → IDRK 블록 확인
    │
    ▼
zlibext 압축 해제 → 원본 파일 바이트 (G1M, G1T, KTID, ...)
```

---

## 파일 식별 체계: KTID

모든 에셋은 32비트 해시인 **FileKtid**로 식별된다.

- 원본 파일명(예: `CHA_KAS0001_body.g1m`)에서 특정 해시 알고리즘으로 생성
- 게임 내에서 파일명 없이 이 숫자만으로 에셋을 참조함
- 에셋 타입은 별도의 **TypeKtid** (타입 클래스 해시)로 구분

자세한 내용은 [03_ktid_system.md](03_ktid_system.md) 참조.

---

## .fdata 파일의 내부 구조

`.fdata` 파일은 단순한 플랫 컨테이너다. 내부에 여러 **IDRK 블록**이 불연속적인
오프셋에 배치되어 있다. 각 블록 사이에 패딩이 있을 수 있으며, 블록들의 순서나
배치는 에셋 DB와 무관하다 — RDB의 `fdata_offset`이 정확한 위치를 가리킨다.

```
0x9f8e7d6c.fdata (예시, 실제 크기 수백 MB)

offset 0x000000: [IDRK 블록] 에셋 A (G1M)        → RDB의 fdata_id=7, offset=0x000000
offset 0x018430: [IDRK 블록] 에셋 B (G1T)        → RDB의 fdata_id=7, offset=0x018430
offset 0x03F200: [IDRK 블록] 에셋 C (KTID 파일)  → RDB의 fdata_id=7, offset=0x03F200
offset 0x040000: [패딩/미사용]
offset 0x041800: [IDRK 블록] 에셋 D (kidsobjdb) → RDB의 fdata_id=7, offset=0x041800
...
```

> **주의**: 하나의 .fdata 파일에는 여러 타입의 에셋이 섞여 있다.
> "G1M fdata", "G1T fdata" 같은 구분은 없다 — 모두 같은 .fdata 파일 내의
> 다른 오프셋에 위치한 IDRK 블록일 뿐이다.

---

## 에셋 타입별 역할

| 파일 형식 | 역할 | 참조 방식 |
|-----------|------|-----------|
| `.g1m` | 3D 모델 (지오메트리 + 재질 슬롯) | kidsobjdb → FileKtid |
| `.g1t` | 텍스처 컨테이너 (BCn 압축 DDS) | kidsobjdb → FileKtid |
| `.ktid` | 텍스처 슬롯 매핑 (slot → G1T FileKtid) | G1M의 prop → FileKtid |
| `.kidsobjdb` | 오브젝트 의존성 DB (G1M↔G1T 체인의 중심) | 씬/캐릭터 코드에서 직접 |
| `.g1a` | 스켈레탈 애니메이션 | kidsobjdb → FileKtid |
| `.g1s` | 스켈레톤 데이터 | kidsobjdb → FileKtid |
| `.mtl` | 머티리얼 정의 | G1M 내부 참조 |
| `.srsa`/`.srst` | 사운드 아카이브/스트림 | kidsobjdb → FileKtid |

---

## 게임별 차이점

### DOA6 vs Atelier Yumia (주요 차이)

| 항목 | DOA6 | Atelier Yumia |
|------|------|---------------|
| FDATA debug 파일 | 없음 | 있음 (IRNK 레코드로 원본 파일명 복원 가능) |
| `.name` 파일 | 없음 | 있음 |
| G1M 버전 | 9200 계열 | 9300 계열 |
| NUNO 클로스 | NUNO1/NUNO4 | NUNO5 (로컬 스페이스 CP) |
| RDB 엔트리 수 | ~78,627 | 더 많음 (추정) |

---

## 에셋 참조 체계 요약

에셋들은 단순한 1:1 관계가 아니라 체인으로 연결되어 있다.
모드로 G1M을 교체할 때 텍스처까지 함께 교체하려면 이 체인 전체를 이해해야 한다.

```
캐릭터 씬
  └─ kidsobjdb
       ├─ IDOK [ModelData]  FileKtid=G1M_FK
       │    └─ prop 0xad260326 → ktid_obj_id
       ├─ IDOK [KtidFile]   FileKtid=KTID_FK  (ktid_obj_id와 일치)
       │    └─ .ktid 파일: [(slot0, G1T_FK_A), (slot1, G1T_FK_B), ...]
       └─ IDOK [TexContext] FileKtid=G1T_FK_A
            └─ 실제 텍스처 파일
```

자세한 내용은 [04_asset_reference_chain.md](04_asset_reference_chain.md) 참조.
