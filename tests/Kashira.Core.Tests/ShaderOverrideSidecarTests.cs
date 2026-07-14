using Kashira.Core.Doa6;
using Xunit;

namespace Kashira.Core.Tests;

public class ShaderOverrideSidecarTests
{
    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc_{Guid.NewGuid():N}.shaders.json");
        try
        {
            var s = new ShaderOverrideSidecar();
            s.Set(0x1FE387E1, 0x4BF9B7F1);
            s.Set(0xDC694116, 0x2DB3EFCD);
            s.Save(path);

            var loaded = ShaderOverrideSidecar.Load(path);
            Assert.Equal(2, loaded.Overrides.Count);
            Assert.Equal(0x4BF9B7F1u, loaded.Get(0x1FE387E1));
            Assert.Equal(0x2DB3EFCDu, loaded.Get(0xDC694116));
            Assert.Null(loaded.Get(0xAAAA0001));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_Empty_DeletesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc_{Guid.NewGuid():N}.shaders.json");
        File.WriteAllText(path, "{}");
        new ShaderOverrideSidecar().Save(path);          // 비어 있으면 삭제
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SetClear()
    {
        var s = new ShaderOverrideSidecar();
        s.Set(0x1FE387E1, 0x4BF9B7F1);
        Assert.False(s.IsEmpty);
        s.Clear(0x1FE387E1);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Load_Missing_Empty() => Assert.True(
        ShaderOverrideSidecar.Load(Path.Combine(Path.GetTempPath(), "no_such.shaders.json")).IsEmpty);

    [Fact]
    public void PathFor_AppendsExtension()
        => Assert.Equal(@"C:\x\body.g1m.shaders.json", ShaderOverrideSidecar.PathFor(@"C:\x\body.g1m"));
}
