using Kashira.Core.Doa6;
using Xunit;

namespace Kashira.Core.Tests;

public class ShaderCatalogTests
{
    private const string Json = """
    {
      "game": "doa6lr",
      "schema": 1,
      "shaders": {
        "0x4bf9b7f1": { "name": "skin_SSS", "occ": 4, "meshTypes": ["rigid"], "exampleMeshes": ["0x2d23bc66"] },
        "0xacb28496": { "name": "", "occ": 4, "meshTypes": ["rigid"], "exampleMeshes": ["0x1fe387e1", "0x11380057"] }
      }
    }
    """;

    [Fact]
    public void Parse_ReadsEntries()
    {
        var cat = ShaderCatalog.Parse(Json);

        var e = cat.Get(0x4bf9b7f1)!;
        Assert.Equal("skin_SSS", e.Name);
        Assert.Equal(0x2d23bc66u, e.DonorMeshHash);
        Assert.Equal(4, e.Occ);
        Assert.Contains("rigid", e.MeshTypes);
        Assert.True(cat.Contains(0x4bf9b7f1));
        Assert.False(cat.Contains(0xdeadbeef));
        Assert.Equal(2, cat.All.Count());
    }

    [Fact]
    public void Display_NameWithValue_UnknownFallback()
    {
        var cat = ShaderCatalog.Parse(Json);
        Assert.Equal("skin_SSS (0x4bf9b7f1)", cat.Display(0x4bf9b7f1)); // 라벨됨
        Assert.Equal("unknown (0xacb28496)", cat.Display(0xacb28496)); // name 빈값
        Assert.Equal("unknown (0xdeadbeef)", cat.Display(0xdeadbeef)); // 미등록
    }

    [Fact]
    public void DonorMeshHash_FirstExample()
    {
        var cat = ShaderCatalog.Parse(Json);
        Assert.Equal(0x1fe387e1u, cat.Get(0xacb28496)!.DonorMeshHash); // exampleMeshes[0]
    }

    [Fact]
    public void LoadFile_Missing_ReturnsEmpty()
    {
        var cat = ShaderCatalog.LoadFile(Path.Combine(Path.GetTempPath(), "no_such_shaders.json"));
        Assert.Empty(cat.All);
    }
}
