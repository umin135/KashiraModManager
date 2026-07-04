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
        return ZlibExt.Decompress(payload, uncomp);
    }

    /// <summary>
    /// 원본 에셋 바이트 → 완성된 IDRK 블록(헤더 0x58 + payload).
    /// templateHeader 가 있으면 param 등 세부 필드를 그대로 복사(동일 타입 원본 헤더 권장).
    /// </summary>
    public static byte[] Build(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> templateHeader = default)
    {
        byte[] payload = ZlibExt.Compress(raw);
        int total = HeaderSize + payload.Length;

        var header = new byte[HeaderSize];
        if (templateHeader.Length >= HeaderSize)
            templateHeader.Slice(0, HeaderSize).CopyTo(header);
        else
        {
            "IDRK"u8.CopyTo(header);
            "0000"u8.CopyTo(header.AsSpan(4));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x08), (ulong)total);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x10), (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x18), (ulong)raw.Length);

        var block = new byte[total];
        header.CopyTo(block, 0);
        payload.CopyTo(block, HeaderSize);
        return block;
    }
}
