using Kashira.Core.Formats;

namespace Kashira.Core.Patching;

/// <summary>여러 IDRK 블록을 하나의 mods.fdata 로 패킹. 각 블록의 오프셋을 추적한다.</summary>
public sealed class ModsFdataBuilder
{
    private readonly MemoryStream _ms = new();

    public readonly record struct Placed(uint Ktid, int Offset, int BlockSize, int RawSize);

    public List<Placed> Blocks { get; } = new();

    /// <summary>에셋 원본 바이트 → IDRK 블록 생성 후 추가. 배치 오프셋 반환.</summary>
    public Placed Add(uint ktid, byte[] raw, ReadOnlySpan<byte> templateHeader = default)
    {
        int offset = (int)_ms.Length;
        var block = IdrkBlock.Build(raw, templateHeader);
        _ms.Write(block);
        var placed = new Placed(ktid, offset, block.Length, raw.Length);
        Blocks.Add(placed);
        return placed;
    }

    public byte[] ToBytes() => _ms.ToArray();
}
