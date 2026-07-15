namespace Kashira.Core.Doa6;

/// <summary>
/// 재질 슬롯 셰이더 지정(재질 인덱스 → matB)을 <b>메시그룹 해시 → matB</b> 로 팬아웃한다.
///
/// 셰이더는 엔진상 메시그룹(sid) 키, 텍스처는 재질 인덱스 키 — 다대다. 에디터는 재질 단위로만 편집하고,
/// install 시 여기서 "그 재질을 쓰는 메시그룹"으로 확장한다(공유는 Blender 에서 이미 결정됨).
///
/// <b>리지드(meshType 0)만</b> 팬아웃 — sid matB(2번째 자식)는 리지드 셰이더. 소프트/NUNO 는 자식 구조가 달라 제외.
/// 한 메시그룹이 <b>여러 재질을 걸치며</b> 그 재질들에 서로 다른 셰이더가 지정되면 sid 는 메시당 셰이더 1개라
/// 충돌 → 첫 재질(인덱스 오름차순) 우선 + Warnings 에 기록.
/// 참조: _docs/_plans/editor_full_rereview.md §8
/// </summary>
public static class MaterialShaderFanout
{
    public sealed record Result(IReadOnlyDictionary<uint, uint> MeshShaders, IReadOnlyList<string> Warnings);

    /// <summary>
    /// <see cref="Expand"/> 의 역: 메시별 셰이더(sid matB)를 그 메시가 쓰는 <b>재질</b>로 캡처 → { 재질 인덱스 → matB }.
    /// 리지드(meshType 0) + 셰이더 등록된 메시만. 한 재질을 여러 메시가 서로 다른 셰이더로 쓰면 <b>첫 메시(등장 순서) 우선</b>.
    /// 번들 import 시 소스 코스튬의 재질별 셰이더 타입을 매니페스트(Shader)로 캡처하는 데 쓴다.
    /// </summary>
    public static IReadOnlyDictionary<int, uint> Capture(IReadOnlyList<CostumeMeshModel.Mesh> meshes)
    {
        var shaders = new Dictionary<int, uint>();
        foreach (var m in meshes)                          // 등장 순서 = 결정적
        {
            if (m.MeshType != 0 || m.ShaderMatB is not { } matB) continue;   // 리지드 + 셰이더 등록된 것만
            foreach (var s in m.Slots)
                if (!shaders.ContainsKey(s.MaterialIndex)) shaders[s.MaterialIndex] = matB;  // 첫 메시 우선
        }
        return shaders;
    }

    public static Result Expand(IReadOnlyList<CostumeMeshModel.Mesh> meshes,
                                IReadOnlyDictionary<int, uint> materialShaders)
    {
        var map = new Dictionary<uint, uint>();
        var warnings = new List<string>();
        if (materialShaders.Count == 0) return new Result(map, warnings);

        foreach (var m in meshes)
        {
            if (m.MeshType != 0) continue;   // 리지드만(matB 유효)

            // 이 메시가 쓰는 재질 중 셰이더 지정된 것(인덱스 오름차순 = 결정적)
            var picks = m.Slots.Select(s => s.MaterialIndex)
                               .Where(materialShaders.ContainsKey)
                               .Distinct()
                               .OrderBy(i => i)
                               .Select(i => (mat: i, matB: materialShaders[i]))
                               .ToList();
            if (picks.Count == 0) continue;

            if (picks.Select(p => p.matB).Distinct().Count() > 1)
                warnings.Add($"mesh @{m.NameHash:X8} 가 여러 재질({string.Join(",", picks.Select(p => p.mat))})에 "
                           + $"서로 다른 셰이더 지정 — sid 는 메시당 1개만 가능, 첫 재질 {picks[0].mat} 사용");

            map[m.NameHash] = picks[0].matB;
        }
        return new Result(map, warnings);
    }
}
