using System.Buffers.Binary;
using Kashira.Core.Doa6;
using Kashira.Core.Formats;
using Xunit;

namespace Kashira.Core.Tests;

public class ShaderOverridePlanTests
{
    // 도너 메시가 등록된 합성 sid: [donor, matA, matB, 0].
    private static CharacterSid SidWithDonor(uint donor, uint matA, uint matB)
    {
        var buf = new byte[0x40 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x08), 1); // count
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x40), donor);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x44), matA);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x48), matB);
        return CharacterSid.Parse(buf);
    }

    private static ShaderCatalog Catalog(uint matB, uint donor) => ShaderCatalog.Parse($$"""
    { "shaders": { "0x{{matB:x8}}": { "name": "test", "exampleMeshes": ["0x{{donor:x8}}"] } } }
    """);

    [Fact]
    public void Build_AllocatesFresh_RenamesAndRegisters()
    {
        // 도너 0x2d23bc66 (matB 0x4bf9b7f1) 이 sid 에 등록됨. 메시 0x1fe387e1 에 그 셰이더 지정.
        var sid = SidWithDonor(0x2d23bc66, 0x3db5c705, 0x4bf9b7f1);
        var cat = Catalog(0x4bf9b7f1, 0x2d23bc66);
        var overrides = new Dictionary<uint, uint> { [0x1FE387E1] = 0x4bf9b7f1 };

        var plan = ShaderOverridePlan.Build(overrides, cat, sid, allocBase: 0x0FA10000);

        // rename: 0x1fe387e1 → fresh(할당). fresh 는 sid 미등록.
        Assert.Single(plan.RenameMap);
        uint fresh = plan.RenameMap[0x1FE387E1];
        Assert.False(sid.IsRegistered(fresh));
        Assert.Equal(0x0FA10000u, fresh);                 // 첫 할당 = base

        // sid 등록: fresh ← 도너 0x2d23bc66.
        Assert.Single(plan.SidRegs);
        Assert.Equal(fresh, plan.SidRegs[0].NewMeshHash);
        Assert.Equal(0x2d23bc66u, plan.SidRegs[0].DonorMeshHash);
    }

    [Fact]
    public void Build_UnknownShader_Skipped()
    {
        var sid = SidWithDonor(0x2d23bc66, 0x3db5c705, 0x4bf9b7f1);
        var cat = ShaderCatalog.Empty;                    // 카탈로그 없음
        var overrides = new Dictionary<uint, uint> { [0x1FE387E1] = 0x4bf9b7f1 };

        var plan = ShaderOverridePlan.Build(overrides, cat, sid);
        Assert.Empty(plan.RenameMap);
        Assert.Empty(plan.SidRegs);
    }

    [Fact]
    public void Build_DeterministicAllocation_Sorted()
    {
        var sid = SidWithDonor(0x2d23bc66, 0x3db5c705, 0x4bf9b7f1);
        var cat = Catalog(0x4bf9b7f1, 0x2d23bc66);
        var overrides = new Dictionary<uint, uint>
        {
            [0xBBBB0002] = 0x4bf9b7f1,
            [0xAAAA0001] = 0x4bf9b7f1,
        };

        var plan = ShaderOverridePlan.Build(overrides, cat, sid, allocBase: 0x0FA10000);

        // 정렬(오름차순) 순으로 할당 → AAAA0001=base, BBBB0002=base+1 (결정적).
        Assert.Equal(0x0FA10000u, plan.RenameMap[0xAAAA0001]);
        Assert.Equal(0x0FA10001u, plan.RenameMap[0xBBBB0002]);
    }
}
