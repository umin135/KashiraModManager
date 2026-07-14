using Kashira.Core.Formats;

namespace Kashira.Core.Doa6;

/// <summary>
/// 에디터 "메시 계층" 모델(결정 B) — g1m 메시그룹 + 서브메시 재질 + sid 셰이더를 합쳐
/// <c>메시(=셰이더 1개) → 슬롯(=재질, 텍스처)</c> 트리로 만든다.
///
/// 셰이더는 메시(메시그룹 해시)별, 텍스처는 슬롯(g1m 재질 인덱스)별.
/// sid 레코드 [meshHash, matA, matB] 에서 <b>matB(2번째 자식) = 셰이더-타입</b>(리지드), 소프트는 셰이더노드 없음.
/// 참조: _docs/_plans/editor_full_rereview.md §3, tools/verify/sid_shaders.py
/// </summary>
public static class CostumeMeshModel
{
    /// <summary>슬롯 = g1m 재질 인덱스 + 그 재질을 쓰는 서브메시들.</summary>
    public sealed record Slot(int MaterialIndex, IReadOnlyList<int> Submeshes);

    /// <summary>메시 = 메시그룹 이름해시 + meshType + 슬롯들 + 셰이더(matB, 없으면 소프트/미등록).</summary>
    public sealed record Mesh(uint NameHash, int MeshType, IReadOnlyList<Slot> Slots, uint? ShaderMatB);

    /// <summary>g1m(+선택 sid) → 메시 계층. sid 있으면 각 메시의 셰이더(matB) 채움.</summary>
    public static IReadOnlyList<Mesh> Build(G1mContainer g1m, CharacterSid? sid = null)
    {
        var groups = G1mGeometry.ParseMeshGroups(g1m);
        var subs = G1mGeometry.ParseSubmeshes(g1m);
        return BuildFrom(groups, subs, sid is null ? null : sid.GetChildren);
    }

    /// <summary>순수 로직(테스트용): 메시그룹 + 서브메시 + 셰이더조회 → 메시 계층.</summary>
    public static IReadOnlyList<Mesh> BuildFrom(
        IReadOnlyList<G1mGeometry.MeshGroup> groups,
        IReadOnlyList<G1mGeometry.Submesh> subs,
        Func<uint, uint[]?>? shaderLookup)
    {
        // 이름해시별로 메시그룹 엔트리 병합(한 해시가 여러 엔트리=LOD/패스).
        var byHash = new Dictionary<uint, (int meshType, List<int> submeshes)>();
        var order = new List<uint>();
        foreach (var g in groups)
        {
            if (!byHash.TryGetValue(g.NameHash, out var acc))
            {
                acc = (g.MeshType, new List<int>());
                byHash[g.NameHash] = acc;
                order.Add(g.NameHash);
            }
            foreach (var si in g.Submeshes)
                if (!acc.submeshes.Contains(si)) acc.submeshes.Add(si);
        }

        var result = new List<Mesh>(order.Count);
        foreach (var hash in order)
        {
            var (meshType, submeshes) = byHash[hash];

            // 슬롯 = 서브메시들을 재질 인덱스로 묶기(등장 순서 유지).
            var slotOrder = new List<int>();
            var slotSubs = new Dictionary<int, List<int>>();
            foreach (var si in submeshes)
            {
                if (si < 0 || si >= subs.Count) continue;
                int mat = subs[si].Material;
                if (!slotSubs.TryGetValue(mat, out var l)) { l = new List<int>(); slotSubs[mat] = l; slotOrder.Add(mat); }
                l.Add(si);
            }
            var slots = slotOrder.Select(m => new Slot(m, slotSubs[m])).ToList();

            // 셰이더 matB = sid 자식[1](리지드 2체인). 소프트/미등록 = null.
            uint? matB = null;
            var children = shaderLookup?.Invoke(hash);
            if (children is { Length: >= 2 }) matB = children[1];

            result.Add(new Mesh(hash, meshType, slots, matB));
        }
        return result;
    }
}
