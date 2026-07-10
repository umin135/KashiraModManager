using System.IO.Compression;
using Kashira.Core.Games;
using Kashira.Core.Patching;

namespace Kashira.Core.Mods;

/// <summary>
/// .ktmod 패키지 로더 (생성 아님 — 읽기 전용).
/// .ktmod 는 확장자만 다른 (보통 무압축) ZIP. 내부 구조:
///   Content/*.*              — (예약) 싱글톤 DB 참조 등록용. 현재는 미사용 플레이스홀더.
///   Content_Legacy/&lt;db&gt;/*.* — 0x{ktid}.{ext} 날것 파일. 폴더명(root/system…)이 대상 RDB.
///                              그 DB 에서만 '리다이렉트 전용'으로 패치(미존재 시 skip).
///                              Content_Legacy/*.* (db 폴더 없음)도 허용 — 전체 DB 탐색 후 리다이렉트.
///   preview|thumb/*.png  — 상세 미리보기 갤러리 (정렬 상위 5개).
///   thumb.png            — 대표 썸네일.
///   mod.ini              — Target / Author / Description.
/// Target 은 게임 실행 exe 이름(확장자 제외). 예: DOA6LR.exe → Target=DOA6LR.
/// </summary>
public sealed class KtmodPackage
{
    public string FilePath { get; }
    public string Name => Path.GetFileNameWithoutExtension(FilePath);
    public string Target { get; }
    public string Author { get; }
    public string Description { get; }

    /// <summary>대표 썸네일(thumb.png) 바이트. 없으면 null.</summary>
    public byte[]? ThumbPng { get; }

    /// <summary>미리보기 갤러리 PNG 바이트들 (정렬 상위 5개).</summary>
    public IReadOnlyList<byte[]> Previews { get; }

    /// <summary>Content_Legacy 의 교체 대상 엔트리들.</summary>
    public IReadOnlyList<LegacyEntry> Legacy { get; }

    /// <summary>Content/ 코스튬 매니페스트들(Content/&lt;set&gt;.json).</summary>
    public IReadOnlyList<CostumeManifest> CostumeManifests { get; }

    /// <summary>Db 는 Content_Legacy 아래 폴더명(root/system…). null 이면 전체 DB 탐색.</summary>
    public sealed record LegacyEntry(uint FileKtid, string Ext, string? Db, string EntryName);

    private const int MaxPreviews = 5;

    private KtmodPackage(string filePath, string target, string author, string description,
        byte[]? thumb, IReadOnlyList<byte[]> previews, IReadOnlyList<LegacyEntry> legacy,
        IReadOnlyList<CostumeManifest> costumeManifests)
    {
        FilePath = filePath;
        Target = target;
        Author = author;
        Description = description;
        ThumbPng = thumb;
        Previews = previews;
        Legacy = legacy;
        CostumeManifests = costumeManifests;
    }

    /// <summary>.ktmod 를 파싱. 실패(손상/비ZIP 등)하면 null.</summary>
    public static KtmodPackage? Load(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            string target = "", author = "", description = "";
            var ini = zip.Entries.FirstOrDefault(e => LeafEquals(e.FullName, "mod.ini"));
            if (ini is not null)
            {
                var kv = ParseIni(ReadText(ini));
                kv.TryGetValue("target", out target!);
                kv.TryGetValue("author", out author!);
                kv.TryGetValue("description", out description!);
            }

            byte[]? thumb = null;
            var thumbEntry = zip.Entries.FirstOrDefault(e => LeafEquals(e.FullName, "thumb.png"));
            if (thumbEntry is not null) thumb = ReadAll(thumbEntry);

            var previews = zip.Entries
                .Where(e => IsGalleryPng(e.FullName))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxPreviews)
                .Select(ReadAll)
                .ToList();

            var legacy = new List<LegacyEntry>();
            foreach (var e in zip.Entries)
            {
                var lp = LegacyPath(e.FullName);
                if (lp is null) continue;
                var (db, file) = lp.Value;
                var stem = Path.GetFileNameWithoutExtension(file);
                if (!DebugMods.TryParseKtid(stem, out var ktid)) continue;
                var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                legacy.Add(new LegacyEntry(ktid, ext, db, e.FullName));
            }

            var manifests = new List<CostumeManifest>();
            foreach (var e in zip.Entries)
            {
                if (!IsContentManifest(e.FullName)) continue;
                var cm = CostumeManifest.Parse(ReadText(e));
                if (cm is not null) manifests.Add(cm);
            }

            return new KtmodPackage(path, target ?? "", author ?? "", description ?? "", thumb, previews, legacy, manifests);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Target 이 이 게임의 실행 exe 이름(확장자 제외)과 일치하는가.</summary>
    public bool MatchesGame(GameInstall game)
    {
        if (string.IsNullOrWhiteSpace(Target) || string.IsNullOrEmpty(game.ExePath)) return false;
        var exe = Path.GetFileNameWithoutExtension(game.ExePath);
        return string.Equals(exe, Target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Content_Legacy 를 리다이렉트 전용 교체 목록으로 변환 (바이트는 여기서 읽음).</summary>
    public List<PatchEngine.Replacement> BuildReplacements()
    {
        var reps = new List<PatchEngine.Replacement>();
        using var zip = ZipFile.OpenRead(FilePath);
        var byName = zip.Entries.ToDictionary(e => e.FullName, e => e, StringComparer.OrdinalIgnoreCase);
        foreach (var le in Legacy)
        {
            if (!byName.TryGetValue(le.EntryName, out var e)) continue;
            reps.Add(new PatchEngine.Replacement(le.FileKtid, ReadAll(e), le.Ext, RedirectOnly: true, TargetDb: le.Db));
        }
        return reps;
    }

    // ---- zip/경로 헬퍼 (래퍼 최상위 폴더가 있어도 관대하게 매칭) ----

    private static string[] Segments(string full) =>
        full.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>경로의 마지막 이름이 leaf 와 같은가 (루트든 래퍼폴더 안이든).</summary>
    private static bool LeafEquals(string full, string leaf)
    {
        var segs = Segments(full);
        return segs.Length > 0 && segs[^1].Equals(leaf, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Content_Legacy 경로 해석. 반환:
    ///   (db, file)  — Content_Legacy/&lt;db&gt;/file
    ///   (null, file)— Content_Legacy/file (db 폴더 없음)
    /// 그 외(더 깊은 중첩·해당없음)는 null.
    /// </summary>
    private static (string? Db, string File)? LegacyPath(string full)
    {
        var segs = Segments(full);
        for (int i = 0; i < segs.Length; i++)
        {
            if (!segs[i].Equals("Content_Legacy", StringComparison.OrdinalIgnoreCase)) continue;
            int rem = segs.Length - 1 - i; // Content_Legacy 뒤 세그먼트 수
            if (rem == 1) return (null, segs[i + 1]);           // 평면: Content_Legacy/file
            if (rem == 2) return (segs[i + 1], segs[i + 2]);    // db 폴더: Content_Legacy/db/file
            return null;                                        // 더 깊음 → 무시
        }
        return null;
    }

    /// <summary>파일이 지정 폴더 '바로 아래'에 있으면 파일명 반환, 아니면 null.</summary>
    private static string? FileDirectlyIn(string full, string folder)
    {
        var segs = Segments(full);
        for (int i = 0; i < segs.Length - 1; i++)
            if (segs[i].Equals(folder, StringComparison.OrdinalIgnoreCase) && i == segs.Length - 2)
                return segs[^1];
        return null;
    }

    /// <summary>Content/ 바로 아래의 .json 매니페스트인가 (Content/&lt;set&gt;.json). 하위 폴더의 파일 데이터는 제외.</summary>
    private static bool IsContentManifest(string full)
    {
        var segs = Segments(full);
        for (int i = 0; i < segs.Length; i++)
            if (segs[i].Equals("Content", StringComparison.OrdinalIgnoreCase)
                && i == segs.Length - 2
                && segs[^1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// 저작 매니페스트의 @참조를 Content/ 파일 바이트로 해석해 설치 입력을 만든다(zip 열어 읽음).
    /// @이름.ext → Content/ 아래 leaf 파일명이 "이름.ext" 인 파일. mesh/텍스처 누락 시 null.
    /// </summary>
    public Doa6.CostumeAuthorInstaller.AuthoredCostume? BuildAuthored(CostumeManifest m)
    {
        if (!m.IsAuthored || m.Mesh is null) return null;
        using var zip = ZipFile.OpenRead(FilePath);
        var files = ContentFilesByLeaf(zip);

        byte[]? Resolve(string? atRef)
        {
            if (string.IsNullOrWhiteSpace(atRef)) return null;
            var leaf = atRef.TrimStart('@');
            return files.TryGetValue(leaf, out var e) ? ReadAll(e) : null;
        }

        var g1m = Resolve(m.Mesh.G1m);
        var grp = Resolve(m.Mesh.Grp);
        var mtl = Resolve(m.Mesh.Mtl);
        if (g1m is null || grp is null || mtl is null) return null;

        // grp/mtl 은 JSON-first: .json 이면 설치 시 raw 바이너리로 변환
        if (m.Mesh.Grp!.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            grp = Formats.GrpDoc.FromJson(System.Text.Encoding.UTF8.GetString(grp)).ToBinary();
        if (m.Mesh.Mtl!.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            mtl = Formats.MtlDoc.FromJson(System.Text.Encoding.UTF8.GetString(mtl)).ToBinary();

        // 재질 스펙 → 설치 재질 + 참조된 텍스처 수집
        var texFiles = new Dictionary<string, byte[]>();
        var mats = new List<Doa6.CostumeAuthorInstaller.AuthoredMaterial>();
        foreach (var spec in m.Materials)
        {
            foreach (var perVar in spec.Slots)
                foreach (var atRef in perVar.Values)
                    if (!texFiles.ContainsKey(atRef))
                    {
                        var b = Resolve(atRef);
                        if (b is null) return null; // 필수 텍스처 누락
                        texFiles[atRef] = b;
                    }
            mats.Add(new Doa6.CostumeAuthorInstaller.AuthoredMaterial(spec.VariationAffecting, spec.Slots));
        }

        return new Doa6.CostumeAuthorInstaller.AuthoredCostume(
            m.TargetCostume, g1m, grp, mtl, m.VariationCount, mats, texFiles,
            RequireAllSlots: false, MaterialTemplateCostume: m.MaterialTemplate);
    }

    /// <summary>Content/ 아래 모든 비-json 파일을 leaf 파일명 → 엔트리로(@참조 전역 스코프).</summary>
    private static Dictionary<string, ZipArchiveEntry> ContentFilesByLeaf(ZipArchive zip)
    {
        var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var segs = Segments(e.FullName);
            int ci = Array.FindIndex(segs, s => s.Equals("Content", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || ci >= segs.Length - 1) continue;                 // Content 아래 아님
            var leaf = segs[^1];
            if (IsContentManifest(e.FullName)) continue;                    // 매니페스트만 제외(.mtl.json 등 에셋은 포함)
            map[leaf] = e;                                                  // leaf 유일성 가정
        }
        return map;
    }

    /// <summary>preview/ 또는 thumb/ 폴더 바로 아래의 .png 인가 (갤러리).</summary>
    private static bool IsGalleryPng(string full)
    {
        var file = FileDirectlyIn(full, "preview") ?? FileDirectlyIn(full, "thumb");
        return file is not null && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = e.Length > 0 ? new MemoryStream((int)e.Length) : new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadText(ZipArchiveEntry e)
    {
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    /// <summary>단순 INI: 'Key = Value' 또는 'Key: Value', 주석(; #)·[섹션] 무시. 키는 소문자.</summary>
    private static Dictionary<string, string> ParseIni(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;
            int sep = line.IndexOf('=');
            if (sep < 0) sep = line.IndexOf(':');
            if (sep <= 0) continue;
            var key = line[..sep].Trim().ToLowerInvariant();
            var val = line[(sep + 1)..].Trim();
            if (key.Length > 0) map[key] = val;
        }
        return map;
    }
}
