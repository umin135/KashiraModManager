using Kashira.Core.Doa6;

namespace Kashira.Core.Mods;

/// <summary>
/// 저작 코스튬 매니페스트 → 에디터 재질 그리드 모델(읽기). UI 무관.
/// 재질마다 행=카테고리(역할), 열=변형. 셀 = 참조 텍스처(@파일). 변형 항목 null = 상속(base 폴백).
/// 별도로 BaseForm(기본형태 = BaseKtid) 단일 열 그리드를 노출.
/// @참조는 프로젝트 Content/ 안의 동일 파일명으로 해석해 절대경로를 채운다.
/// </summary>
public static class CostumeGrid
{
    public sealed record Cell(int Category, string Role, int Column, string? AtRef, string? FileName, string? FullPath, bool Inherited);
    public sealed record Row(int Category, string Role, IReadOnlyList<Cell> Cells);
    /// <summary>Index: 재질 인덱스(0..). Base form 은 -1(BaseKtid 편집 대상).</summary>
    public sealed record Material(int Index, string Name, int ColumnCount, IReadOnlyList<string> ColumnHeaders, IReadOnlyList<Row> Rows);
    public sealed record Model(string SetName, int VariationCount, Material? BaseForm, IReadOnlyList<Material> Materials);

    /// <summary>매니페스트 파일 경로(Content/&lt;set&gt;.json) → 그리드 모델. 저작 매니페스트가 아니면 null.</summary>
    public static Model? Build(KtmodProject project, string manifestPath)
    {
        var cm = CostumeManifest.Parse(File.ReadAllText(manifestPath));
        if (cm is null || !cm.IsAuthored) return null;

        string setName = Path.GetFileNameWithoutExtension(manifestPath);
        var index = FileIndex(project.ContentDir);

        // Base form(기본형태) — BaseKtid 단일 열
        Material? baseForm = null;
        if (cm.BaseKtid is { Count: > 0 })
        {
            var rows = new List<Row>();
            foreach (var cat in ShaderCategory.Sort(cm.BaseKtid.Keys))
            {
                cm.BaseKtid.TryGetValue(cat, out var atRef);
                rows.Add(new Row(cat, ShaderCategory.RoleName(cat),
                    new[] { MakeCell(cat, 0, atRef, index, inherited: false) }));
            }
            baseForm = new Material(-1, "Base form", 1, new[] { "base" }, rows);
        }

        // 재질별 변형 그리드
        var materials = new List<Material>();
        for (int m = 0; m < cm.Materials.Count; m++)
        {
            var spec = cm.Materials[m];
            int cols = Math.Max(1, spec.Slots.Count);

            // 이 재질에 등장하는 카테고리 합집합
            var cats = new HashSet<int>();
            foreach (var v in spec.Slots) if (v is not null) foreach (var k in v.Keys) cats.Add(k);

            var rows = new List<Row>();
            foreach (var cat in ShaderCategory.Sort(cats))
            {
                var cells = new List<Cell>(cols);
                for (int c = 0; c < cols; c++)
                {
                    var v = spec.Slots[c];
                    if (v is null)
                    {
                        // 변형 상속(base 폴백) — 참조 없음
                        cells.Add(new Cell(cat, ShaderCategory.RoleName(cat), c, null, null, null, Inherited: true));
                    }
                    else
                    {
                        v.TryGetValue(cat, out var atRef);
                        cells.Add(MakeCell(cat, c, atRef, index, inherited: false));
                    }
                }
                rows.Add(new Row(cat, ShaderCategory.RoleName(cat), cells));
            }

            var headers = new List<string>(cols);
            for (int c = 0; c < cols; c++) headers.Add($"var{c}");
            materials.Add(new Material(m, $"Material {m}", cols, headers, rows));
        }

        return new Model(setName, cm.VariationCount, baseForm, materials);
    }

    private static Cell MakeCell(int cat, int column, string? atRef, IReadOnlyDictionary<string, string> index, bool inherited)
    {
        if (string.IsNullOrEmpty(atRef))
            return new Cell(cat, ShaderCategory.RoleName(cat), column, null, null, null, inherited);
        string fileName = atRef.TrimStart('@');
        index.TryGetValue(fileName, out var full);
        return new Cell(cat, ShaderCategory.RoleName(cat), column, atRef, fileName, full, inherited);
    }

    /// <summary>Content/ 하위 파일명 → 절대경로(중복은 첫 항목).</summary>
    private static IReadOnlyDictionary<string, string> FileIndex(string contentDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(contentDir)) return map;
        foreach (var path in Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (!map.ContainsKey(name)) map[name] = path;
        }
        return map;
    }
}
