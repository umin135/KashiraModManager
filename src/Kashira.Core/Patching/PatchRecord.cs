using System.Text.Json;

namespace Kashira.Core.Patching;

/// <summary>패치 상태.</summary>
public enum PatchStatus
{
    /// <summary>패치 기록 없음(순정).</summary>
    NotPatched,
    /// <summary>패치됨 + 현재 rdb/rdx 가 기록과 일치(유효).</summary>
    Patched,
    /// <summary>패치 기록은 있으나 rdb/rdx 가 그 이후 변경/덮어써짐 → 재-Apply 필요.</summary>
    NeedsReapply,
}

/// <summary>한 DB(root/system 등)에 대한 패치 기록.</summary>
public sealed class DbPatchInfo
{
    public string Db { get; set; } = "";
    public string ModsFdata { get; set; } = "";
    public List<string> Replacements { get; set; } = new();
    // Apply 직후 우리가 쓴 rdb/rdx 의 지문(변경 감지용)
    public long RdbSize { get; set; }
    public string RdbHash { get; set; } = "";
    public long RdxSize { get; set; }
    public string RdxHash { get; set; } = "";
    // Apply 당시 사용한 pristine(백업) rdb/rdx 의 지문 — 백업 무결성·rebaseline 진단용
    public long PreRdbSize { get; set; }
    public string PreRdbHash { get; set; } = "";
    public long PreRdxSize { get; set; }
    public string PreRdxHash { get; set; } = "";
}

/// <summary>_Kashira/rdbpatch.json — 마지막 Apply 시점 기록.</summary>
public sealed class PatchRecord
{
    public string PatchedAtUtc { get; set; } = "";
    public List<DbPatchInfo> Databases { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static PatchRecord? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PatchRecord>(File.ReadAllText(path))
                : null;
        }
        catch { return null; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
