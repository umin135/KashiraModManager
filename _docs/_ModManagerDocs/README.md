# KatanaEngine 범용 모드 매니저 — 리서치 문서

이 폴더는 KatanaEngine 기반 게임(Dead or Alive 6, Atelier Yumia 등)의 에셋을
런타임에 교체하는 **범용 모드 매니저** 개발을 위한 리서치 결과물이다.

특정 구현 프로젝트에 종속되지 않도록 설계되었으며, 이 폴더만으로 전체 개념과
구현 방법론을 이해할 수 있다.

---

## 문서 목차

| 파일 | 내용 |
|------|------|
| [01_asset_system.md](01_asset_system.md) | KatanaEngine 에셋 파이프라인 전반 |
| [02_rdb_fdata_format.md](02_rdb_fdata_format.md) | RDB / RDX / FDATA 바이너리 포맷 상세 |
| [03_ktid_system.md](03_ktid_system.md) | KTID 해시 시스템 및 타입 → 확장자 매핑 |
| [04_asset_reference_chain.md](04_asset_reference_chain.md) | 에셋 간 참조 관계 (kidsobjdb / G1M → G1T 체인) |
| [05_mod_strategies.md](05_mod_strategies.md) | 모드 오버라이드 전략 4가지 분석 및 비교 |
| [06_mod_package_spec.md](06_mod_package_spec.md) | 제안 모드 패키지(.kmod) 포맷 명세 |

---

## 테스트 대상 게임

| 게임 | 엔진 버전 | 검증 상태 |
|------|-----------|-----------|
| Dead or Alive 6 Last Round | KatanaEngine (DOA6) | RDB/FDATA 파싱 완전 검증 |
| Atelier Yumia | KatanaEngine (Yumia) | FDATA debug/.name 파일 확인, G1M NUNO5 검증 |
| Wo Long / Nioh (참고) | KatanaEngine 계열 | 포맷 유사, 미검증 |

---

## 핵심 전제

### "Last-wins" 가설 (검증 필요)

RDB에 동일한 `file_ktid`를 가진 엔트리가 두 개 이상 존재할 때,
엔진이 **마지막에 등록된 엔트리를 우선**한다면 모드 오버라이드가 가능하다.

- 가설이 맞을 경우: RDB 끝에 엔트리를 추가하는 것만으로 기존 에셋을 교체할 수 있음
- 가설이 틀릴 경우: RDB 내 기존 엔트리를 직접 수정해야 함

이 가설의 검증이 모드 매니저 구현 방향을 결정하는 핵심 분기점이다.

---

## 빠른 시작

모드 매니저 개발을 시작한다면 이 순서로 읽는 것을 권장한다:

1. [01_asset_system.md](01_asset_system.md) — 전체 구조 파악
2. [02_rdb_fdata_format.md](02_rdb_fdata_format.md) — RDB 수정에 필요한 포맷 이해
3. [05_mod_strategies.md](05_mod_strategies.md) — 어떤 방식으로 구현할지 결정
4. [04_asset_reference_chain.md](04_asset_reference_chain.md) — 복잡한 에셋(G1M+G1T) 교체 시 필요
5. [06_mod_package_spec.md](06_mod_package_spec.md) — 모드 패키지 포맷 설계
