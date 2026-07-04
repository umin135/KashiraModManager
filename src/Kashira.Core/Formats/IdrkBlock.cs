using System.Buffers.Binary;

namespace Kashira.Core.Formats;

/// <summary>
/// .fdata 내부 IDRK 블록. 헤더 0x58 + zlibext payload.
/// payload 위치는 반드시 "블록 끝에서 compressed_size 만큼".
/// 참조: 02_rdb_fdata_format.md §3, tools/verify/katana_fdata.py
/// </summary>
public static class IdrkBlock
{
    public const int HeaderSize = 0x58;

    /// <summary>fdata 의 blockStart 에서 원본 에셋 바이트 추출(압축 해제 포함).</summary>
    public static byte[] Extract(ReadOnlySpan<byte> fdata, int blockStart)
    {
        if (fdata.Slice(blockStart, 4).IndexOf("IDRK"u8) != 0)
            throw new InvalidDataException($"IDRK magic mismatch @0x{blockStart:x}");

        long total = (long)BinaryPrimitives.ReadUInt64LittleEndian(fdata.Slice(blockStart + 0x08));
        long comp = (long)BinaryPrimitives.ReadUInt64LittleEndian(fdata.Slice(blockStart + 0x10));
        long uncomp = (long)BinaryPrimitives.ReadUInt64LittleEndian(fdata.Slice(blockStart + 0x18));

        int blockEnd = blockStart + (int)total;
        var payload = fdata.Slice(blockEnd - (int)comp, (int)comp);
        if (comp == uncomp) return payload.ToArray(); // 무압축 저장(stored) — 그대로 반환
        return ZlibExt.Decompress(payload, uncomp);
    }

    /// <summary>
    /// 원본 에셋 바이트 → 완성된 IDRK 블록.
    /// templatePrefix = payload 앞의 전체 영역(헤더 + 타입별 param 영역, = 원본 total−comp 바이트).
    /// 이 prefix 를 통째로 보존해야 한다(g1m 등은 헤더 뒤에 param 영역이 있어 이를 빠뜨리면 크래시).
    /// prefix 가 없으면 최소 0x58 헤더만 생성.
    /// </summary>
    public static byte[] Build(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> templatePrefix = default, bool compress = true)
    {
        // compress=false → 무압축 저장(compressed==uncompressed, 청크/mid8 없음). 참조 툴이 쓰는 방식이며
        // 엔진의 zlibext 청크 검증(mid8 등)을 통째로 우회한다. (엔트리 압축플래그도 함께 해제해야 함)
        byte[] payload = compress ? ZlibExt.Compress(raw) : raw.ToArray();

        byte[] prefix;
        if (templatePrefix.Length >= 0x20)
            prefix = templatePrefix.ToArray();
        else
        {
            prefix = new byte[HeaderSize];
            "IDRK"u8.CopyTo(prefix);
            "0000"u8.CopyTo(prefix.AsSpan(4));
        }

        int total = prefix.Length + payload.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(0x08), (ulong)total);
        BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(0x10), (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(0x18), (ulong)raw.Length);

        if (!compress && prefix.Length >= 0x30)
        {
            // 무압축: 블록 헤더 0x2c 의 CompressionType(bit 20-25)도 None 으로.
            // (참조 툴 RDBExplorer: rawFlags & ~(0x3F<<20)) — 안 지우면 엔진이 raw 를 해제하려다 크래시.
            uint bf = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(0x2C)) & ~(0x3Fu << 20);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(0x2C), bf);
        }

        var block = new byte[total];
        prefix.CopyTo(block, 0);
        payload.CopyTo(block, prefix.Length);
        return block;
    }
}
