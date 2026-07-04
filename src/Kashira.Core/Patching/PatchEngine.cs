using Kashira.Core.Formats;
using Kashira.Core.Games;

namespace Kashira.Core.Patching;

/// <summary>
/// 전략 B(RDB 리다이렉트) 패치 엔진. 원본 불변 — 항상 깨끗한 백업에서 재계산.
/// 복수 DB(root/system) 인지: 각 file_ktid 가 속한 DB 를 찾아 그 DB만 패치.
/// mods.fdata 파일명 해시는 0xCA5E00xx 예약 네임스페이스.
/// </summary>
public static class PatchEngine
{
    private const uint ModsHashBase = 0xCA5E0001;
    private const string ModsGlob = "0xca5e*.fdata";

    public sealed record Replacement(uint FileKtid, byte[] AssetBytes);

    public sealed record Report(int Requested, int Applied, IReadOnlyList<uint> NotFound, IReadOnlyList<string> Notes);

    public static Report Apply(GameWorkspace ws, IReadOnlyList<Replacement> replacements)
    {
        Directory.CreateDirectory(ws.BackupDir);
        var notes = new List<string>();
        var matched = new HashSet<uint>();

        // 이전에 남은 mods.fdata 정리 (깨끗한 재계산)
        foreach (var f in SafeGlob(ws.PackageDir, ModsGlob)) TryDelete(f);

        int dbIndex = 0;
        foreach (var db in ws.Databases)
        {
            string liveRdb = Path.Combine(ws.PackageDir, db + ".rdb");
            string liveRdx = Path.Combine(ws.PackageDir, db + ".rdx");
            string bakRdb = Path.Combine(ws.BackupDir, db + ".rdb");
            string bakRdx = Path.Combine(ws.BackupDir, db + ".rdx");
            if (!File.Exists(liveRdb) || !File.Exists(liveRdx)) { dbIndex++; continue; }

            if (!File.Exists(bakRdb)) File.Copy(liveRdb, bakRdb);
            if (!File.Exists(bakRdx)) File.Copy(liveRdx, bakRdx);

            var rdb = RdbFile.Parse(File.ReadAllBytes(bakRdb)); // pristine
            var rdx = RdxFile.Parse(File.ReadAllBytes(bakRdx));

            var here = replacements.Where(r => rdb.Find(r.FileKtid) is not null).ToList();
            if (here.Count == 0)
            {
                // 이 DB 는 영향 없음 → 원본 그대로 복원
                File.Copy(bakRdb, liveRdb, overwrite: true);
                File.Copy(bakRdx, liveRdx, overwrite: true);
                dbIndex++;
                continue;
            }

            int newId = rdx.NextFreeId;
            uint modsHash = PickModsHash(rdx, dbIndex);
            string modsName = RdxFile.FdataName(modsHash);

            var builder = new ModsFdataBuilder();
            foreach (var r in here)
            {
                var e = rdb.Find(r.FileKtid)!;
                var template = ReadTemplateHeader(ws.PackageDir, rdx, e);
                builder.Add(r.FileKtid, r.AssetBytes, template);
                matched.Add(r.FileKtid);
            }

            foreach (var b in builder.Blocks)
                rdb.Redirect(rdb.Find(b.Ktid)!, newId, b.Offset, b.BlockSize, b.RawSize);

            var rdx2 = rdx.WithEntry(newId, modsHash);

            File.WriteAllBytes(Path.Combine(ws.PackageDir, modsName), builder.ToBytes());
            File.WriteAllBytes(liveRdb, rdb.Data);
            File.WriteAllBytes(liveRdx, rdx2.Data);

            notes.Add($"{db}: {here.Count} asset(s) → {modsName} (fdata_id {newId})");
            dbIndex++;
        }

        var notFound = replacements.Where(r => !matched.Contains(r.FileKtid))
                                   .Select(r => r.FileKtid).ToList();
        return new Report(replacements.Count, matched.Count, notFound, notes);
    }

    public static void Revert(GameWorkspace ws)
    {
        foreach (var db in ws.Databases)
        {
            string bakRdb = Path.Combine(ws.BackupDir, db + ".rdb");
            string bakRdx = Path.Combine(ws.BackupDir, db + ".rdx");
            if (File.Exists(bakRdb)) File.Copy(bakRdb, Path.Combine(ws.PackageDir, db + ".rdb"), true);
            if (File.Exists(bakRdx)) File.Copy(bakRdx, Path.Combine(ws.PackageDir, db + ".rdx"), true);
        }
        foreach (var f in SafeGlob(ws.PackageDir, ModsGlob)) TryDelete(f);
    }

    public static bool IsApplied(GameWorkspace ws) => SafeGlob(ws.PackageDir, ModsGlob).Any();

    // ---- helpers ----

    private static byte[] ReadTemplateHeader(string packageDir, RdxFile rdx, RdbEntry e)
    {
        try
        {
            if (!rdx.Map.TryGetValue(e.FdataId, out var hash)) return Array.Empty<byte>();
            var path = Path.Combine(packageDir, RdxFile.FdataName(hash));
            using var fs = File.OpenRead(path);
            fs.Seek(e.FdataOffset, SeekOrigin.Begin);
            var buf = new byte[IdrkBlock.HeaderSize];
            return fs.Read(buf, 0, buf.Length) == buf.Length ? buf : Array.Empty<byte>();
        }
        catch { return Array.Empty<byte>(); }
    }

    private static uint PickModsHash(RdxFile rdx, int dbIndex)
    {
        uint h = ModsHashBase + (uint)dbIndex;
        var used = new HashSet<uint>(rdx.Map.Values);
        while (used.Contains(h)) h++;
        return h;
    }

    private static IEnumerable<string> SafeGlob(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
