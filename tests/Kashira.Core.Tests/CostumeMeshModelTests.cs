using Kashira.Core.Doa6;
using Kashira.Core.Formats;
using Xunit;

namespace Kashira.Core.Tests;

public class CostumeMeshModelTests
{
    private static G1mGeometry.Submesh Sub(int index, int material)
        => new(index, 0, 0, 0, material, 0, 0, 0, 0);

    private static G1mGeometry.MeshGroup Group(uint hash, int meshType, params int[] subs)
        => new(hash, meshType, 0xFFFFFFFF, subs);

    [Fact]
    public void Build_GroupsSubmeshesByMaterial_AndReadsShader()
    {
        // 실제 g1m 처럼 리스트 위치 == 서브메시 인덱스. 재질: 8→0, 9→1, 10→2, 12→2, 나머지 0.
        var subs = Enumerable.Range(0, 13)
            .Select(i => Sub(i, i switch { 9 => 1, 10 => 2, 12 => 2, _ => 0 }))
            .ToArray();
        var groups = new[]
        {
            Group(0x1FE387E1, 0, 8, 9),   // rigid, mat0+mat1
            Group(0xDC694116, 0, 12),     // rigid, mat2
            Group(0xF3040298, 4, 10),     // soft
        };
        uint[]? Shader(uint h) => h switch
        {
            0x1FE387E1 => new uint[] { 0xDE5EE740, 0xACB28496 },  // 리지드 2체인, matB=acb28496
            0xDC694116 => new uint[] { 0xF2001215, 0x2DB3EFCD },
            0xF3040298 => new uint[] { 0x45E869B7 },              // 소프트 1자식 → matB 없음
            _ => null,
        };

        var meshes = CostumeMeshModel.BuildFrom(groups, subs, Shader);

        Assert.Equal(3, meshes.Count);

        var m0 = meshes[0];
        Assert.Equal(0x1FE387E1u, m0.NameHash);
        Assert.Equal(0, m0.MeshType);
        Assert.Equal(0xACB28496u, m0.ShaderMatB);                // matB = 2번째 자식
        Assert.Equal(2, m0.Slots.Count);                          // mat0, mat1 = 2 슬롯
        Assert.Equal(0, m0.Slots[0].MaterialIndex);
        Assert.Equal(new[] { 8 }, m0.Slots[0].Submeshes);
        Assert.Equal(1, m0.Slots[1].MaterialIndex);
        Assert.Equal(new[] { 9 }, m0.Slots[1].Submeshes);

        var mSoft = meshes[2];
        Assert.Equal(4, mSoft.MeshType);
        Assert.Null(mSoft.ShaderMatB);                            // 소프트 = 셰이더노드 없음
        Assert.Single(mSoft.Slots);
        Assert.Equal(2, mSoft.Slots[0].MaterialIndex);
    }

    [Fact]
    public void Build_MergesMultiEntryNameHash()
    {
        var subs = new[] { Sub(0, 5), Sub(1, 5) };
        var groups = new[]
        {
            Group(0x795E78B4, 0, 0),   // 같은 해시 두 엔트리(LOD/패스)
            Group(0x795E78B4, 2, 1),
        };

        var meshes = CostumeMeshModel.BuildFrom(groups, subs, _ => null);

        Assert.Single(meshes);                                    // 이름해시별 1개로 병합
        Assert.Equal(new[] { 0, 1 }, meshes[0].Slots[0].Submeshes); // 서브메시 합쳐짐(같은 mat5)
    }

    [Fact]
    public void Build_NoSid_ShaderNull()
    {
        var meshes = CostumeMeshModel.BuildFrom(
            new[] { Group(0xAAAA0001, 0, 0) }, new[] { Sub(0, 0) }, shaderLookup: null);
        Assert.Null(meshes[0].ShaderMatB);
    }
}
