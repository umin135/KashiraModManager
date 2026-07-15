using System.Buffers.Binary;
using Kashira.Core.Formats;
using Xunit;

namespace Kashira.Core.Tests;

/// <summary>
/// CharacterSid 편집기 검증(합성 데이터 — 저작권 실제 sid 불요).
/// 로직은 인게임 검증된 tools/verify/sid_register.py 를 포팅한 것.
/// </summary>
public class CharacterSidTests
{
    // 헤더 0x40 + 레코드 [head, child…, 0]. count @0x08.
    private static byte[] BuildSid(uint recordCount, params uint[][] records)
    {
        var buf = new List<byte>(new byte[0x40]);   // 헤더 0x40 (전부 0)
        foreach (var rec in records)
        {
            foreach (var v in rec) buf.AddRange(BitConverter.GetBytes(v));
            buf.AddRange(BitConverter.GetBytes(0u)); // 종료
        }
        var arr = buf.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(arr.AsSpan(0x08), recordCount);
        return arr;
    }

    private static uint[] Rec(uint head, params uint[] children)
    {
        var a = new uint[children.Length + 1];
        a[0] = head;
        children.CopyTo(a, 1);
        return a;
    }

    [Fact]
    public void GetChildren_ReadsMeshRecord()
    {
        var sid = CharacterSid.Parse(BuildSid(2,
            Rec(0xAAAA0001, 0x11110001, 0x22220001),
            Rec(0xBBBB0002, 0x33330002)));

        Assert.Equal(new uint[] { 0x11110001, 0x22220001 }, sid.GetChildren(0xAAAA0001));
        Assert.Equal(new uint[] { 0x33330002 }, sid.GetChildren(0xBBBB0002));
        Assert.Null(sid.GetChildren(0xCCCC0003));
        Assert.True(sid.IsRegistered(0xAAAA0001));
        Assert.False(sid.IsRegistered(0xCCCC0003));
        Assert.Equal(2u, sid.RecordCount);
    }

    [Fact]
    public void Register_AppendsNode_AndBumpsCount()
    {
        var sid = CharacterSid.Parse(BuildSid(2,
            Rec(0xAAAA0001, 0x11110001, 0x22220001),
            Rec(0xBBBB0002, 0x33330002)));
        int before = sid.Build().Length;

        sid.Register(0xCCCC0003, new uint[] { 0x11110001, 0x22220001 });

        Assert.Equal(3u, sid.RecordCount);                       // 헤더 0x08 +1
        Assert.Equal(before + 16, sid.Build().Length);           // [hash,c0,c1,0] = 4 u32
        Assert.Equal(new uint[] { 0x11110001, 0x22220001 }, sid.GetChildren(0xCCCC0003));
        Assert.True(sid.IsDirty);
    }

    [Fact]
    public void Register_Soft_SingleChild()
    {
        var sid = CharacterSid.Parse(BuildSid(1, Rec(0xAAAA0001, 0x11110001, 0x22220001)));
        sid.Register(0xF0040298, new uint[] { 0x45E869B7 });     // 소프트 = 자식 1개
        Assert.Single(sid.GetChildren(0xF0040298)!);
        Assert.Equal(2u, sid.RecordCount);
    }

    [Fact]
    public void RegisterFromDonor_CopiesShaderChildren()
    {
        var sid = CharacterSid.Parse(BuildSid(1, Rec(0x1FE387E1, 0xDE5EE740, 0xACB28496)));
        sid.RegisterFromDonor(0x1FAA87E1, 0x1FE387E1);           // TEST4: 도너 자식 통째 복사

        Assert.Equal(sid.GetChildren(0x1FE387E1), sid.GetChildren(0x1FAA87E1));
        Assert.Equal(2u, sid.RecordCount);
    }

    [Fact]
    public void SetChildren_SameCount_SizePreserving()
    {
        var sid = CharacterSid.Parse(BuildSid(1, Rec(0xAAAA0001, 0x11110001, 0x22220001)));
        int len = sid.Build().Length;

        sid.SetChildren(0xAAAA0001, new uint[] { 0x99990001, 0x88880001 });

        Assert.Equal(len, sid.Build().Length);                   // 크기 불변
        Assert.Equal(new uint[] { 0x99990001, 0x88880001 }, sid.GetChildren(0xAAAA0001));
    }

    [Fact]
    public void SetChildren_DifferentCount_Throws()
    {
        var sid = CharacterSid.Parse(BuildSid(1, Rec(0xAAAA0001, 0x11110001, 0x22220001)));
        Assert.Throws<InvalidOperationException>(
            () => sid.SetChildren(0xAAAA0001, new uint[] { 0x99990001 }));   // 2→1 = 크기변경
    }

    [Fact]
    public void FindDonorFor_FindsRigidMeshUsingMatB()
    {
        var sid = CharacterSid.Parse(BuildSid(2,
            Rec(0x2D23BC66, 0x3DB5C705, 0x4BF9B7F1),   // 리지드 [mesh, matA, matB]
            Rec(0xF3040298, 0x45E869B7)));             // 소프트 1자식
        Assert.Equal(0x2D23BC66u, sid.FindDonorFor(0x4BF9B7F1));   // matB(2번째) 쓰는 메시
        Assert.Null(sid.FindDonorFor(0x3DB5C705));                 // matA(1번째)는 셰이더 아님
        Assert.Null(sid.FindDonorFor(0xDEADBEEF));                 // 없음
        Assert.Null(sid.FindDonorFor(0x45E869B7));                 // 소프트 1자식 = 리지드 도너 아님
    }

    [Fact]
    public void Register_AlreadyRegistered_Throws()
    {
        var sid = CharacterSid.Parse(BuildSid(1, Rec(0xAAAA0001, 0x11110001)));
        Assert.Throws<InvalidOperationException>(
            () => sid.Register(0xAAAA0001, new uint[] { 0x22220001 }));
    }
}
