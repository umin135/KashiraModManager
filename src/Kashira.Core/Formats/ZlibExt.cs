using System.Buffers.Binary;
using System.IO.Compression;

namespace Kashira.Core.Formats;

/// <summary>
/// KatanaEngine zlibext 코덱. 청크 = [u16 zlib_stream_size][8B 미상][zlib 스트림(78 9c…adler32)].
/// 실측(DOA6): raw deflate 가 아니라 full zlib 스트림. 8B 미상 필드는 0으로 채운다(엔진 미검증).
/// 참조: _docs/_ModManagerDocs/02_rdb_fdata_format.md §3, tools/verify/katana_fdata.py
/// </summary>
public static class ZlibExt
{
    private const int ChunkSize = 0x4000;

    public static byte[] Decompress(ReadOnlySpan<byte> payload, long expectedSize = -1)
    {
        using var outMs = new MemoryStream();
        int pos = 0;
        while (pos + 0x0A <= payload.Length)
        {
            int chunkSize = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(pos));
            int start = pos + 0x0A; // u16(2) + 미상(8)
            int end = start + chunkSize;
            if (chunkSize == 0 || end > payload.Length) break;

            using (var inMs = new MemoryStream(payload.Slice(start, chunkSize).ToArray()))
            using (var zs = new ZLibStream(inMs, CompressionMode.Decompress))
                zs.CopyTo(outMs);

            pos = end;
            if (expectedSize >= 0 && outMs.Length >= expectedSize) break;
        }
        return outMs.ToArray();
    }

    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var outMs = new MemoryStream();
        var head = new byte[10]; // [u16 size][8B 미상=0]
        for (int i = 0; i < data.Length; i += ChunkSize)
        {
            int len = Math.Min(ChunkSize, data.Length - i);

            byte[] stream;
            using (var chunkMs = new MemoryStream())
            {
                // 원본 DOA6 블록은 zlib 레벨6(헤더 78 9c). 레벨9(78 da)는 엔진 inflate 가
                // 복잡한 실데이터에서 크래시 → 원본과 동일 레벨로 압축.
                using (var zs = new ZLibStream(chunkMs, CompressionLevel.Optimal, leaveOpen: true))
                    zs.Write(data.Slice(i, len));
                stream = chunkMs.ToArray();
            }

            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)stream.Length);
            Array.Clear(head, 2, 8); // 8B 미상 필드 = 0
            outMs.Write(head);
            outMs.Write(stream);
        }
        return outMs.ToArray();
    }
}
