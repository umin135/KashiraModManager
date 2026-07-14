using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// Character.sid — DOA6 전역 셰이더/렌더/소프트리지드 레지스트리 편집기 (FileKtid 0x5f150754).
///
/// 오브젝트 그래프 DB. 메시 레코드 = <c>[meshHash, child…, 0]</c>:
///   · 해시 존재     = 렌더 게이트 (미등록 → invisible)
///   · 자식(matB 등) = 셰이더/머티리얼-인스턴스 (텍스처 아님)
///   · 자식 개수     = 2 리지드 / 1 소프트
/// 파서는 헤더 0x08(전체 레코드 수)만큼만 읽으므로, 노드 추가 시 반드시 0x08 을 갱신한다.
///
/// 편집 모델(fresh 해시 격리): 새/커스텀 메시는 <see cref="Register"/> 로 append(전역 공유 해시 불변).
/// 기존 메시의 셰이더만 바꾸는 건 <see cref="SetChildren"/>(같은 개수, 크기 불변).
/// 참조: _docs/_sid_research/sid_format_spec.md, tools/verify/sid_register.py
/// </summary>
public sealed class CharacterSid
{
    public const uint FileKtid = 0x5f150754;
    private const int CountOffset = 0x08;

    private byte[] _data;
    private bool _dirty;

    private CharacterSid(byte[] data) => _data = data;

    public static CharacterSid Parse(byte[] data)
    {
        if (data.Length < 0x40)
            throw new InvalidDataException("Character.sid too small");
        return new CharacterSid(data);
    }

    public bool IsDirty => _dirty;

    /// <summary>헤더 0x08 = 파서가 읽는 전체 레코드 수.</summary>
    public uint RecordCount => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(CountOffset));

    /// <summary>메시 해시가 등록돼 있으면 그 u32 위치(4바이트 정렬), 아니면 -1.</summary>
    public int FindMesh(uint meshHash)
    {
        var span = _data.AsSpan();
        for (int o = 0; o + 4 <= span.Length; o += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[o..]) == meshHash)
                return o;
        return -1;
    }

    public bool IsRegistered(uint meshHash) => FindMesh(meshHash) >= 0;

    /// <summary>메시 레코드의 자식(셰이더/머티리얼-ID)들 — 해시 뒤 비-0 u32들(다음 0까지). 미등록이면 null.</summary>
    public uint[]? GetChildren(uint meshHash)
    {
        int i = FindMesh(meshHash);
        if (i < 0) return null;
        var span = _data.AsSpan();
        var list = new List<uint>(4);
        int o = i + 4;
        while (o + 4 <= span.Length)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(span[o..]);
            if (v == 0) break;
            list.Add(v);
            o += 4;
        }
        return list.ToArray();
    }

    /// <summary>기존 메시의 자식(셰이더)을 교체 — **개수 동일**(크기 불변)일 때만. 소프트/리지드 유지.</summary>
    public void SetChildren(uint meshHash, uint[] children)
    {
        int i = FindMesh(meshHash);
        if (i < 0) throw new InvalidOperationException($"mesh 0x{meshHash:x8} 미등록 — SetChildren 불가(Register 사용)");
        var cur = GetChildren(meshHash)!;
        if (cur.Length != children.Length)
            throw new InvalidOperationException(
                $"자식 개수 변경({cur.Length}→{children.Length})은 크기가 바뀜 — 격리 위해 Register 로 새 해시를 append 하라");
        for (int k = 0; k < children.Length; k++)
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(i + 4 + k * 4), children[k]);
        _dirty = true;
    }

    /// <summary>
    /// 새 메시 해시를 등록(append) — EOF 에 <c>[meshHash, children…, 0]</c> 를 붙이고 헤더 0x08(레코드 수)을 +1.
    /// children 개수로 소프트/리지드 결정(2=리지드, 1=소프트). 이미 등록된 해시면 예외.
    /// </summary>
    public void Register(uint meshHash, uint[] children)
    {
        if (children.Length == 0)
            throw new ArgumentException("children 최소 1개(셰이더/머티리얼-ID)");
        if (IsRegistered(meshHash))
            throw new InvalidOperationException($"mesh 0x{meshHash:x8} 이미 등록됨");

        int add = (1 + children.Length + 1) * 4;               // [hash, children…, 0]
        var buf = new byte[_data.Length + add];
        _data.AsSpan().CopyTo(buf);
        int o = _data.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), meshHash); o += 4;
        foreach (var c in children) { BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), c); o += 4; }
        // 마지막 u32 는 0 종료(버퍼가 0 초기화라 그대로).
        _data = buf;

        BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(CountOffset), RecordCount + 1);
        _dirty = true;
    }

    /// <summary>
    /// 도너 메시의 자식(셰이더+소프트/리지드)을 그대로 복사해 새 해시를 등록.
    /// 셰이더 타입 적용의 표준 경로(TEST4 검증: 도너 자식 통째 복사 = 정상 렌더).
    /// </summary>
    public void RegisterFromDonor(uint newMeshHash, uint donorMeshHash)
    {
        var donor = GetChildren(donorMeshHash)
            ?? throw new InvalidOperationException($"도너 mesh 0x{donorMeshHash:x8} 미등록");
        Register(newMeshHash, donor);
    }

    public byte[] Build() => _data;
}
