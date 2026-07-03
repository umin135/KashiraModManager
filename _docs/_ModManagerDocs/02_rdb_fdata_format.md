# RDB / RDX / FDATA 바이너리 포맷 상세

모드 매니저가 에셋을 교체하려면 이 세 파일 포맷을 읽고 쓸 수 있어야 한다.

---

## 1. root.rdb — 에셋 메타데이터 DB

### RDB 파일 헤더 (0x20 bytes)

```
offset  size  field
+0x00   4B    magic: "_DRK"
+0x04   4B    version (게임마다 다름)
+0x08   u32   header_size  — 엔트리 배열 시작 오프셋 (보통 0x20)
+0x0C   u32   system_id
+0x10   u32   file_count   — 총 에셋 수 (DOA6: 78,627)
+0x14   u32   database_id
+0x18   8B    folder_path  — ASCII, null-padded ("fdata_pa")
```

### RDB 엔트리 구조

엔트리들은 `header_size` 오프셋부터 4바이트 정렬로 연속 배치된다.
각 엔트리는 작은 IDRK 헤더를 포함한다.

```
offset  size  field
+0x00   4B    magic: "IDRK"
+0x04   4B    version
+0x08   u64   entry_size      — 이 엔트리 전체 크기 (다음 엔트리까지의 거리)
+0x10   u64   data_size       — 위치 메타 크기 (0x0D 또는 0x11)
+0x18   u64   file_size       — 압축 해제 후 실제 에셋 크기 (bytes)
+0x20   u32   entry_type
+0x24   u32   file_ktid       — 에셋 고유 ID ★
+0x28   u32   type_info_ktid  — 타입 클래스 ID (→ 03_ktid_system.md)
+0x2C   u32   flags
               bit[17]:   StorageType  (1=Internal, 0=External)
               bit[20-25]: CompressionType (0=None, 1=Zlib, 4=ZlibExt)
```

위치 메타는 엔트리 끝 (`entry_start + entry_size - data_size`)에 붙는다.

#### RdbLocation32 — data_size == 0x0D (오프셋 4GB 미만)

```
+0x00   u8     (예약)
+0x01   u8     (예약)
+0x02   u32    fdata_offset   — .fdata 파일 내 IDRK 블록 시작 오프셋 ★
+0x06   u32    size_in_cont   — .fdata 내 차지하는 크기 (IDRK 헤더 포함)
+0x0A   u16    fdata_id       — 어느 .fdata 파일인지 ★
```

#### RdbLocation40 — data_size == 0x11 (오프셋 4GB 이상)

```
+0x00   u8     (예약)
+0x01   u8     (예약)
+0x02   u8     offset_high    — 상위 8비트
+0x03   3B     (예약)
+0x06   u32    offset_low     — 하위 32비트
               실제 오프셋 = (offset_high << 32) | offset_low
+0x0A   u32    size_in_cont
+0x0E   u16    fdata_id
```

### C# 파싱 예시

```csharp
// 엔트리 탐색 (순차 방식)
long pos = rdbHeaderSize;
while (pos < rdbData.Length)
{
    if (BitConverter.ToUInt32(data, (int)pos) != 0x4B524449) break; // "IDRK"
    ulong entrySize = BitConverter.ToUInt64(data, (int)pos + 0x08);
    ulong dataSize  = BitConverter.ToUInt64(data, (int)pos + 0x10);
    uint  fileKtid  = BitConverter.ToUInt32(data, (int)pos + 0x24);

    long metaStart = pos + (long)entrySize - (long)dataSize;
    // dataSize == 0x0D → RdbLocation32
    uint fdataOffset = BitConverter.ToUInt32(data, (int)metaStart + 0x02);
    uint sizeInCont  = BitConverter.ToUInt32(data, (int)metaStart + 0x06);
    ushort fdataId   = BitConverter.ToUInt16(data, (int)metaStart + 0x0A);

    pos += (long)entrySize;
}
```

---

## 2. root.rdx — fdata_id → 파일명 해시 인덱스

RDX는 8바이트 엔트리의 단순 배열이다. (헤더는 RDB와 동일한 `_DRK` 구조)

```
offset  size  field
+0x00   u16   fdata_id    — RDB 위치 메타의 fdata_id와 대응
+0x02   u16   padding
+0x04   u32   file_hash   — .fdata 파일명에 사용되는 해시
```

파일명 구성: `0x{file_hash:x8}.fdata`

```
fdata_id=7 → RDX 검색 → file_hash=0x9f8e7d6c → "0x9f8e7d6c.fdata"
```

> fdata_id는 **u16** (0~65535 범위). 새 .fdata 파일을 등록하려면 미사용 ID를 할당해야 한다.
> DOA6 기준 실제 사용 범위 확인 권장 (RDX 전체 스캔으로 파악 가능).

---

## 3. 0x????????.fdata — 에셋 컨테이너

### IDRK 블록 헤더 (0x58 bytes 고정)

```
offset  size  field
+0x00   4B    magic: "IDRK"
+0x04   4B    version
+0x08   u64   total_block_size    — IDRK 블록 전체 크기 (헤더 + 오버헤드 + payload)
+0x10   u64   compressed_size     — zlibext payload 크기
+0x18   u64   uncompressed_size   — 압축 해제 후 원본 파일 크기
+0x20   u32   param_data_size
+0x24   20B   (패딩)
+0x38   32B   param block
; 총 헤더 = 0x58 bytes
```

### Payload 위치 계산

```
block_start   = fdata_offset (RDB에서 가져옴)
block_end     = block_start + total_block_size
payload_end   = block_end
payload_start = block_end - compressed_size
payload_size  = compressed_size
```

> `param_data_size`나 `paramCount`로 payload 위치를 계산하면 잘못된 결과가 나온다.
> 반드시 **"블록 끝에서 compressed_size만큼"** 방식을 사용할 것.

### ZlibExt 압축 해제

Payload는 zlibext 포맷: zlib deflate 청크들의 연속

```
청크 구조 (반복):
  [0x00] u16  compressed_chunk_size
  [0x02] 8B   padding
  [0x0A] ...  zlib deflate stream (compressed_chunk_size bytes)
```

모든 청크를 순서대로 DeflateStream으로 해제 후 이어붙이면 원본 파일 완성.

### ZlibExt 압축 (모드 제작 시 필요)

```python
import zlib, struct

def zlibext_compress(data: bytes, chunk_size: int = 0x4000) -> bytes:
    result = b''
    for i in range(0, len(data), chunk_size):
        chunk = data[i:i+chunk_size]
        compressed = zlib.compress(chunk)[2:-4]  # zlib 헤더/체크섬 제거 → raw deflate
        result += struct.pack('<H', len(compressed)) + b'\x00' * 8 + compressed
    return result
```

---

## 4. Debug FDATA / .name 파일 (Atelier Yumia 등)

일부 게임은 원본 파일명을 복원할 수 있는 debug 데이터를 포함한다.

### IRNK 레코드 (debug fdata 내부)

```
+0x00   4B    "IRNK"
+0x04   u32   entry_size       — 레코드 전체 크기
+0x08   u32   ptr_to_string1   — 오프셋: 원본 파일명 (전각 괄호 포함 가능)
+0x0C   u32   ptr_to_string2   — 오프셋: TypeInfo 계층 경로
[strings...]  null-terminated UTF-8
```

예시:
- String1: `MPR_Muscle_Character_KAS0001_body_kidsalb`
- String2: `TypeInfo::Object::Texture::StaticTexture`

String1에서 KTID 해시(변형 B)를 계산하면 FileKtid와 대조하여 이름 복원 가능.
(참고 구현: [DeathChaos25/fdata_dump](https://github.com/DeathChaos25/fdata_dump))

---

## 5. RDB 수정 시 주의사항

1. **엔트리 크기 고정**: 각 RDB 엔트리는 `entry_size`를 갖는다. 엔트리를 수정할 때는 크기를 바꾸지 않아야 한다 (내부 필드 덮어쓰기만 안전).

2. **file_count 갱신**: 엔트리를 추가할 경우 RDB 헤더의 `file_count`를 갱신해야 한다.

3. **fdata_id 범위**: u16이므로 0~65535. 새 .fdata 등록 시 미사용 ID 사용.

4. **중복 file_ktid**: 동일 file_ktid가 두 개 이상의 엔트리에 있을 때의 엔진 동작은 미검증 (→ [05_mod_strategies.md](05_mod_strategies.md) 참조).
