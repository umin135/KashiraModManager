using System.Buffers.Binary;
using Kashira.Core.Formats;

namespace Kashira.Core.Doa6;

/// <summary>
/// DOA6LR 코스튬 체인의 싱글톤 DB(DOK) 4종을 로드/편집/직렬화하고, 체인을 해석한다.
/// 체인: costume 이름 → scndb(cos_oid) → CE1Common CharacterSetting → CE1MotorCharacter
///       → DM(Displayset::Model, CE) → g1m/grp/mtl/oidex/baseKtid.
/// 상수·검증 근거: tools/verify/ktmod_install.py, 메모리 ktmod-content-symbolic-design.
/// </summary>
public sealed class Doa6SingletonSet
{
    // 싱글톤 DB FileKtid
    public const uint ScnDbFk = 0x6D011726;
    public const uint CeFk = 0xB290631C;
    public const uint Ce1CommonFk = 0x2082AD97;
    public const uint MatEditorFk = 0xD956E4A2;

    // IDOK type
    public const uint T_ScnRoot = 0xDAC911D7;
    public const uint T_Dm = 0xD40B3C8F;
    public const uint T_CharSetting = 0xD186CEEB;
    public const uint T_MotorChar = 0xC4B9B28D;
    public const uint T_Mbe = 0xA8F14404;
    public const uint T_Tbc = 0x3059B9C3;
    public const uint T_TexCtx = 0xFF7DBFD4;

    // prop hash
    public const uint P_Names = 0x4D269345;       // scndb: 이름 문자열 blob
    public const uint P_CosOids = 0x8F9F2DA6;      // scndb: cos_oid 배열
    public const uint P_Dm_G1m = 0x8AB68B3F;
    public const uint P_Dm_Grp = 0x3BBFD9A5;
    public const uint P_Dm_Mtl = 0x7F0DE9A3;
    public const uint P_Dm_Oidex = 0x8DFD0584;
    public const uint P_Dm_Rigbin = 0x1B4FF321;
    public const uint P_Dm_TbcObj = 0xF92C5190;    // DM/MBE: TBC obj_id
    public const uint P_Cs_Cvn = 0xB3A1E5AA;       // ColorVariationNum
    public const uint P_Cs_Mi = 0x24C114F6;        // MI 배열
    public const uint P_Cs_Motor = 0x68A6F779;     // CharacterSetting → CE1MotorCharacter oid
    public const uint P_Mc_Dm = 0x0B6E1578;        // CE1MotorCharacter → DM oid
    public const uint P_Mc_Mrnh = 0x34CF9E5C;      // MaterialReplacementNameHash (name,MBE) 쌍
    public const uint P_Mc_NameArr = 0xCEE5DDAE;   // 재질 name_hash 배열
    public const uint P_Mbe_Kts = 0x0A3D837B;
    public const uint P_Mbe_MatIx = 0x3F86372D;
    public const uint P_Tbc_Ktid = 0x7A1E1EF8;     // TBC: ktid FK
    public const uint P_Tex_G1t = 0x6C7321D2;      // TexContext: g1t FK

    private readonly AssetExtractor _ex;
    private readonly Dictionary<uint, SingletonDb> _dok = new();
    private readonly HashSet<uint> _dirty = new();
    private readonly HashSet<uint> _allocFks = new();

    private Doa6SingletonSet(AssetExtractor ex) => _ex = ex;

    public static Doa6SingletonSet Load(AssetExtractor ex)
    {
        var s = new Doa6SingletonSet(ex);
        foreach (var fk in new[] { ScnDbFk, CeFk, Ce1CommonFk, MatEditorFk })
        {
            var bytes = ex.Extract(fk) ?? throw new InvalidDataException($"싱글톤 DB 0x{fk:X8} 추출 실패");
            s._dok[fk] = SingletonDb.Parse(bytes);
        }
        return s;
    }

    public SingletonDb Dok(uint fk) => _dok[fk];
    public SingletonDb Scn => _dok[ScnDbFk];
    public SingletonDb Ce => _dok[CeFk];
    public SingletonDb Ce1Common => _dok[Ce1CommonFk];
    public SingletonDb MatEditor => _dok[MatEditorFk];

    /// <summary>편집한 DOK 를 dirty 로 표시(직렬화 대상).</summary>
    public void MarkDirty(uint fk) => _dirty.Add(fk);

    /// <summary>dirty DOK 들을 (FileKtid → 직렬화 바이트) 로 반환.</summary>
    public IReadOnlyDictionary<uint, byte[]> DirtyBytes()
        => _dirty.ToDictionary(fk => fk, fk => _dok[fk].Serialize());

    public AssetExtractor Extractor => _ex;

    /// <summary>rdb 에 없고 이미 배정한 FK 와도 겹치지 않는 새 FileKtid(설치 전역에서 공유). 신규 에셋 등록용.</summary>
    public uint AllocFk(uint seed = 0x0FA00000)
    {
        while (_ex.Find(seed) is not null || _allocFks.Contains(seed)) seed++;
        _allocFks.Add(seed);
        return seed;
    }

    // ── 체인 해석 ──────────────────────────────────────────

    /// <summary>코스튬 이름 → cos_oid (= CharacterSetting oid). 못 찾으면 예외.</summary>
    public uint CostumeOid(string name)
    {
        var root = Scn.Records.FirstOrDefault(r => r.Type == T_ScnRoot)
                   ?? throw new InvalidDataException("scndb SCNROOT 없음");
        var namesBlob = root.Prop(P_Names)?.Value ?? Array.Empty<byte>();
        var names = SplitAsciiZ(namesBlob);
        var oids = root.ReadU32Array(P_CosOids);
        int i = names.IndexOf(name);
        if (i < 0 || i >= oids.Length) throw new KeyNotFoundException($"코스튬 '{name}' 없음");
        return oids[i];
    }

    public IdokRecord CharSetting(uint cosOid) =>
        Ce1Common.Find(cosOid) ?? throw new KeyNotFoundException($"CharacterSetting 0x{cosOid:X8} 없음");

    public IdokRecord MotorChar(uint cosOid) =>
        Ce1Common.Find(CharSetting(cosOid).ReadU32(P_Cs_Motor))
        ?? throw new KeyNotFoundException("CE1MotorCharacter 없음");

    public IdokRecord DisplaysetModel(uint cosOid) =>
        Ce.Find(MotorChar(cosOid).ReadU32(P_Mc_Dm))
        ?? throw new KeyNotFoundException("Displayset::Model 없음");

    /// <summary>코스튬의 메시/재질 참조 에셋 FK 묶음.</summary>
    public sealed record DmAssets(uint G1m, uint Mtl, uint Grp, uint Oidex, uint Rigbin, uint TbcObj, uint BaseKtid);

    public DmAssets ResolveAssets(uint cosOid)
    {
        var dm = DisplaysetModel(cosOid);
        uint tbc = dm.ReadU32(P_Dm_TbcObj);
        uint baseKtid = Ce.Find(tbc) is { } t ? t.ReadU32(P_Tbc_Ktid) : 0;
        return new DmAssets(
            dm.ReadU32(P_Dm_G1m), dm.ReadU32(P_Dm_Mtl), dm.ReadU32(P_Dm_Grp),
            dm.ReadU32(P_Dm_Oidex), dm.ReadU32(P_Dm_Rigbin), tbc, baseKtid);
    }

    /// <summary>코스튬 재질/변형 요약.</summary>
    public sealed record MaterialInfo(int Cvn, int SlotCount, uint[] Mi, uint[] NameArr, uint[] Mrnh);

    public MaterialInfo ResolveMaterial(uint cosOid)
    {
        var cs = CharSetting(cosOid);
        int cvn = (int)cs.ReadU32(P_Cs_Cvn);
        var mi = cs.ReadU32Array(P_Cs_Mi);
        var mc = MotorChar(cosOid);
        return new MaterialInfo(cvn, cvn > 0 ? mi.Length / cvn : 0, mi,
            mc.ReadU32Array(P_Mc_NameArr), mc.ReadU32Array(P_Mc_Mrnh));
    }

    private static List<string> SplitAsciiZ(ReadOnlySpan<byte> blob)
    {
        var list = new List<string>();
        int start = 0;
        for (int i = 0; i < blob.Length; i++)
        {
            if (blob[i] != 0) continue;
            if (i > start) list.Add(System.Text.Encoding.ASCII.GetString(blob.Slice(start, i - start)));
            start = i + 1;
        }
        if (start < blob.Length) list.Add(System.Text.Encoding.ASCII.GetString(blob.Slice(start)));
        return list;
    }
}
