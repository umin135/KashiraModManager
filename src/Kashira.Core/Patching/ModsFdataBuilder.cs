using Kashira.Core.Formats;

namespace Kashira.Core.Patching;

/// <summary>여러 IDRK 블록을 하나의 mods.fdata 로 패킹. 각 블록의 오프셋을 추적한다.</summary>
public sealed class ModsFdataBuilder
{
    // 엔진은 IDRK 블록이 16바이트 정렬돼 있다고 가정하고 읽는다(원본 fdata 블록 전부 16정렬).
    // 미정렬 블록은 로드 시 크래시 → 각 블록을 16바이트 경계에 배치한다.
    private const int Alignment = 16;

    private readonly MemoryStream _ms = new();

    public readonly record struct Placed(uint Ktid, int Offset, int BlockSize, int RawSize);

    public List<Placed> Blocks { get; } = new();

    /// <summary>에셋 원본 바이트 → IDRK 블록 생성 후 16바이트 정렬 위치에 추가. 배치 오프셋 반환.</summary>
    public Placed Add(uint ktid, byte[] raw, ReadOnlySpan<byte> templateHeader = default, bool compress = true)
    {
        int pad = (int)((Alignment - (_ms.Length % Alignment)) % Alignment);
        if (pad > 0) _ms.Write(new byte[pad]);

        int offset = (int)_ms.Length;
        var block = IdrkBlock.Build(raw, templateHeader, compress);
        _ms.Write(block);
        var placed = new Placed(ktid, offset, block.Length, raw.Length);
        Blocks.Add(placed);
        return placed;
    }

    public byte[] ToBytes() => _ms.ToArray();
}
