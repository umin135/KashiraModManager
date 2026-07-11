using System.Buffers.Binary;
using Kashira.Core.Formats;

namespace Kashira.Core.Doa6;

/// <summary>
/// 새 머티리얼 체인 생성(텍스처 신규 저작 모드의 핵심). 검증된 메커니즘(메모리 ktmod-content-symbolic-design):
/// 기존 레코드를 클론 후 몇 prop만 수정 → 새 MBE→TBC→MPR ktid→TexContext→g1t 체인을 만들고
/// ME DOK 에 정렬삽입한다(복잡한 prop 은 클론으로 보존).
///   - 슬롯별 새 g1t 지정 시 그 슬롯의 TexContext 를 새로 만들어(clone→P_Tex_G1t) MPR ktid 슬롯을 재지정
///   - 미지정 슬롯은 템플릿의 기존 TexContext 유지
/// MPR ktid 는 rdb 신규 에셋(NewAsset)으로 반환, MBE/TBC/TexContext 는 ME DOK 에 삽입된다.
/// </summary>
public static class MaterialChainFactory
{
    /// <summary>MPR ktid 엔트리 = 8바이트(? u32, TexContext objID u32).</summary>
    private const int MprEntry = 8;
    private const int MprObjOff = 4;

    public sealed record NewMaterial(uint MbeOid, IReadOnlyList<CostumeOverride.NewAsset> NewAssets);

    /// <summary>
    /// templateMbeOid 를 바탕으로 새 MBE 체인을 만든다. slotG1ts[slot] = 그 텍스처 슬롯에 넣을 g1t FK.
    /// 반환: 새 MBE oid + 신규등록할 MPR ktid 에셋(들). ME DOK 은 편집됨(dirty 표시됨).
    /// </summary>
    public static NewMaterial Create(Doa6SingletonSet set, uint templateMbeOid,
        IReadOnlyDictionary<int, uint> slotG1ts, bool requireAllSlots = false)
    {
        var me = set.MatEditor;
        var mbeTpl = me.Find(templateMbeOid)
                     ?? throw new KeyNotFoundException($"템플릿 MBE 0x{templateMbeOid:X8} 없음");
        uint tbcOid = mbeTpl.ReadU32(Doa6SingletonSet.P_Dm_TbcObj);
        var tbcTpl = me.Find(tbcOid) ?? throw new KeyNotFoundException($"템플릿 TBC 0x{tbcOid:X8} 없음");
        uint mprFk = tbcTpl.ReadU32(Doa6SingletonSet.P_Tbc_Ktid);
        var mpr = set.Extractor.Extract(mprFk)?.ToArray()
                  ?? throw new InvalidDataException($"MPR ktid 0x{mprFk:X8} 추출 실패");

        // 1) 지정된 슬롯마다 새 TexContext 생성 후 MPR 슬롯 재지정
        int slots = mpr.Length / MprEntry;
        if (requireAllSlots)
        {
            var missing = Enumerable.Range(0, slots).Where(s => !slotG1ts.ContainsKey(s)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"텍스처 슬롯 미지정: [{string.Join(",", missing)}] (템플릿 MBE 0x{templateMbeOid:X8} 는 {slots}슬롯 전부 필요)");
        }
        for (int s = 0; s < slots; s++)
        {
            if (!slotG1ts.TryGetValue(s, out uint g1t)) continue;
            uint texTplOid = BinaryPrimitives.ReadUInt32LittleEndian(mpr.AsSpan(s * MprEntry + MprObjOff));
            var texTpl = FindTexContext(set, texTplOid)
                         ?? throw new KeyNotFoundException($"TexContext 0x{texTplOid:X8} 없음");
            uint newTex = me.AllocOid();
            var texRec = texTpl.Clone(newTex);
            texRec.SetU32(Doa6SingletonSet.P_Tex_G1t, g1t);
            me.Insert(texRec);
            BinaryPrimitives.WriteUInt32LittleEndian(mpr.AsSpan(s * MprEntry + MprObjOff), newTex);
        }

        // 2) 새 MPR ktid = rdb 신규 에셋
        uint newMprFk = set.AllocFk();
        var newAssets = new List<CostumeOverride.NewAsset> { new(newMprFk, mpr, "ktid") };

        // 3) 새 TBC(→새 MPR ktid) + 새 MBE(→새 TBC), ME 에 삽입
        uint newTbc = me.AllocOid();
        var tbcRec = tbcTpl.Clone(newTbc);
        tbcRec.SetU32(Doa6SingletonSet.P_Tbc_Ktid, newMprFk);
        me.Insert(tbcRec);

        uint newMbe = me.AllocOid();
        var mbeRec = mbeTpl.Clone(newMbe);
        mbeRec.SetU32(Doa6SingletonSet.P_Dm_TbcObj, newTbc);
        me.Insert(mbeRec);

        set.MarkDirty(Doa6SingletonSet.MatEditorFk);
        return new NewMaterial(newMbe, newAssets);
    }

    private static IdokRecord? FindTexContext(Doa6SingletonSet set, uint oid) =>
        set.MatEditor.Find(oid) ?? set.Ce.Find(oid);

    /// <summary>
    /// ★ 카테고리 기반 재질 체인 생성. **프로젝트에 저장된 KTS(에디터가 g1m 에서 임포트 시 생성)** 로부터:
    ///   - KTS = 주어진 슬롯(primary/physics)으로 새 FK 등록. (슬롯 순서 = KTS 순서)
    ///   - MPR ktid = KTS 슬롯 순서로 처음부터 빌드: 슬롯 j → categoryToG1t[primary] → 새 TexContext → (j, objID).
    ///   - MBE/TBC 는 템플릿 클론(MatIx/blob prop 보존)하되 KTS·TBC 만 새 것으로 교체.
    /// 카테고리 매칭이라 변형별 KTS 순서 차이(변형 버그)가 구조적으로 불가. 미매핑 카테고리는 템플릿 TexContext g1t 유지.
    /// </summary>
    public static NewMaterial CreateFromKts(Doa6SingletonSet set, uint templateMbeOid,
        IReadOnlyList<Formats.KtsFile.Slot> ktsSlots, IReadOnlyDictionary<int, uint> categoryToG1t)
    {
        var me = set.MatEditor;
        var mbeTpl = me.Find(templateMbeOid) ?? throw new KeyNotFoundException($"템플릿 MBE 0x{templateMbeOid:X8} 없음");
        uint tbcOid = mbeTpl.ReadU32(Doa6SingletonSet.P_Dm_TbcObj);
        var tbcTpl = me.Find(tbcOid) ?? throw new KeyNotFoundException($"템플릿 TBC 0x{tbcOid:X8} 없음");
        uint tplMprFk = tbcTpl.ReadU32(Doa6SingletonSet.P_Tbc_Ktid);
        var tplMpr = set.Extractor.Extract(tplMprFk)?.ToArray()
                     ?? throw new InvalidDataException($"템플릿 MPR 0x{tplMprFk:X8} 추출 실패");
        uint tplTexOid = BinaryPrimitives.ReadUInt32LittleEndian(tplMpr.AsSpan(MprObjOff)); // 첫 슬롯 TexContext = 클론 원본
        var texTpl = FindTexContext(set, tplTexOid) ?? throw new KeyNotFoundException($"TexContext 0x{tplTexOid:X8} 없음");

        // 1) KTS = 프로젝트 KTS 슬롯을 새 FK 로 등록
        uint ktsFk = set.AllocFk();
        var newAssets = new List<CostumeOverride.NewAsset> { new(ktsFk, Formats.KtsFile.Build(ktsSlots), "kts") };

        // 2) MPR ktid = KTS 슬롯 순서로 빌드(슬롯 j → 카테고리 → g1t → 새 TexContext)
        var mpr = new byte[ktsSlots.Count * MprEntry];
        for (int j = 0; j < ktsSlots.Count; j++)
        {
            uint newTex = me.AllocOid();
            var texRec = texTpl.Clone(newTex);
            if (categoryToG1t.TryGetValue(ktsSlots[j].Primary, out uint g1t))
                texRec.SetU32(Doa6SingletonSet.P_Tex_G1t, g1t);
            me.Insert(texRec);
            BinaryPrimitives.WriteUInt32LittleEndian(mpr.AsSpan(j * MprEntry), (uint)j);
            BinaryPrimitives.WriteUInt32LittleEndian(mpr.AsSpan(j * MprEntry + MprObjOff), newTex);
        }
        uint newMprFk = set.AllocFk();
        newAssets.Add(new(newMprFk, mpr, "ktid"));

        // 3) 새 TBC(→새 MPR) + 새 MBE(→새 TBC, KTS=생성한 새 KTS)
        uint newTbc = me.AllocOid();
        var tbcRec = tbcTpl.Clone(newTbc);
        tbcRec.SetU32(Doa6SingletonSet.P_Tbc_Ktid, newMprFk);
        me.Insert(tbcRec);

        uint newMbe = me.AllocOid();
        var mbeRec = mbeTpl.Clone(newMbe);
        mbeRec.SetU32(Doa6SingletonSet.P_Dm_TbcObj, newTbc);
        mbeRec.SetU32(Doa6SingletonSet.P_Mbe_Kts, ktsFk);
        me.Insert(mbeRec);

        set.MarkDirty(Doa6SingletonSet.MatEditorFk);
        return new NewMaterial(newMbe, newAssets);
    }
}
