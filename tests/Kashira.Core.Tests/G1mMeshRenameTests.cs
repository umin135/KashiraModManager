using System.Buffers.Binary;
using System.Text;
using Kashira.Core.Formats;
using Xunit;

namespace Kashira.Core.Tests;

public class G1mMeshRenameTests
{
    // 합성 0x10009 inner: 헤더 0x20 + 엔트리[name16 "@HEX", meshType@0x10, f@0x12, extid@0x14, idxc@0x18, subs].
    private static byte[] BuildSection(params (uint hash, int meshType, int[] subs)[] entries)
    {
        var buf = new List<byte>(new byte[0x20]);
        foreach (var (hash, mt, subs) in entries)
        {
            var name = new byte[16];
            var s = "@" + hash.ToString("X8");
            Encoding.ASCII.GetBytes(s).CopyTo(name, 0);
            buf.AddRange(name);
            buf.AddRange(BitConverter.GetBytes((ushort)mt));   // 0x10
            buf.AddRange(BitConverter.GetBytes((ushort)2));     // 0x12 f
            buf.AddRange(BitConverter.GetBytes(0xFFFFFFFFu));    // 0x14 extid
            buf.AddRange(BitConverter.GetBytes((uint)subs.Length)); // 0x18 idxc
            foreach (var si in subs) buf.AddRange(BitConverter.GetBytes((uint)si));
        }
        return buf.ToArray();
    }

    [Fact]
    public void RenameInSection_RewritesMatchingHashes_SizePreserving()
    {
        var d = BuildSection(
            (0x1FE387E1, 0, new[] { 8, 9 }),
            (0xDC694116, 0, new[] { 12 }),
            (0xF3040298, 4, new[] { 10 }));
        int len = d.Length;
        var map = new Dictionary<uint, uint> { [0x1FE387E1] = 0x1FAA87E1 };

        int changed = G1mGeometry.RenameInSection(d, map);

        Assert.Equal(1, changed);
        Assert.Equal(len, d.Length);                        // 크기 불변

        var groups = ParseHashes(d);
        Assert.Contains(0x1FAA87E1u, groups);               // 재작성됨
        Assert.DoesNotContain(0x1FE387E1u, groups);
        Assert.Contains(0xDC694116u, groups);               // 미매칭 유지
        Assert.Contains(0xF3040298u, groups);
    }

    [Fact]
    public void RenameInSection_NoMatch_ReturnsZero()
    {
        var d = BuildSection((0xAAAA0001, 0, new[] { 0 }));
        int changed = G1mGeometry.RenameInSection(d, new Dictionary<uint, uint> { [0xBBBB0002] = 0xCCCC0003 });
        Assert.Equal(0, changed);
    }

    // 재작성 결과를 다시 파싱해 이름해시 목록 확인(엔트리 순회는 idxc 로 stride).
    private static List<uint> ParseHashes(byte[] d)
    {
        var list = new List<uint>();
        int p = 0x20;
        while (p < d.Length && d[p] != 0x40) p += 4;
        while (p + 0x1C <= d.Length && d[p] == 0x40)
        {
            uint v = 0;
            for (int k = 1; k <= 8; k++)
            {
                int cc = d[p + k];
                int dig = cc is >= (byte)'0' and <= (byte)'9' ? cc - '0'
                        : cc is >= (byte)'A' and <= (byte)'F' ? cc - 'A' + 10 : 0;
                v = (v << 4) | (uint)dig;
            }
            list.Add(v);
            int idxc = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 0x18));
            p += 0x1C + idxc * 4;
        }
        return list;
    }
}
