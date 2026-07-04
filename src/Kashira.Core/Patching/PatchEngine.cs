using System.Security.Cryptography;
using Kashira.Core.Formats;
using Kashira.Core.Games;

namespace Kashira.Core.Patching;

/// <summary>
/// 전략 B(RDB 리다이렉트) 패치 엔진. 원본 불변 — 항상 pristine 원본에서 재계산.
/// 복수 DB(root/system) 인지: 각 file_ktid 가 속한 DB 만 패치.
/// Apply 시 _Kashira/rdbpatch.json 에 지문을 기록 → 게임 업데이트로 rdb 가 덮이면 감지.
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
        var record = new PatchRecord { PatchedAtUtc = DateTime.UtcNow.ToString("o") };

        // 이전 mods.fdata 정리 (깨끗한 재계산)
        foreach (var f in SafeGlob(ws.PackageDir, ModsGlob)) TryDelete(f);

        int dbIndex = 0;
        foreach (var db in ws.Databases)
        {
            string liveRdb = Path.Combine(ws.PackageDir, db + ".rdb");
            string liveRdx = Path.Combine(ws.PackageDir, db + ".rdx");
            string bakRdb = Path.Combine(ws.BackupDir, db + ".rdb");
            string bakRdx = Path.Combine(ws.BackupDir, db + ".rdx");
            if (!File.Exists(liveRdb) || !File.Exists(liveRdx)) { dbIndex++; continue; }

            // pristine 기준 확보:
            //  - 라이브가 우리 패치 상태면 → 기존 백업이 pristine (없으면 복구 불가 → 스킵)
            //  - 라이브가 순정이면(최초 or 게임 업데이트로 덮임) → 이걸 새 원본으로 재기준
            if (RdxHasModsHash(liveRdx))
            {
                if (!File.Exists(bakRdb) || !File.Exists(bakRdx))
                {
                    notes.Add($"{db}: SKIPPED — live index already patched but no backup exists (restore game files first)");
                    dbIndex++;
                    continue;
                }
            }
            else
            {
                File.Copy(liveRdb, bakRdb, overwrite: true); // rebaseline
                File.Copy(liveRdx, bakRdx, overwrite: true);
            }

            var rdb = RdbFile.Parse(File.ReadAllBytes(bakRdb)); // pristine
            var rdx = RdxFile.Parse(File.ReadAllBytes(bakRdx));

            var here = replacements.Where(r => rdb.Find(r.FileKtid) is not null).ToList();
            if (here.Count == 0)
            {
                File.Copy(bakRdb, liveRdb, overwrite: true); // 이 DB 는 순정 그대로
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
                builder.Add(r.FileKtid, r.AssetBytes, ReadTemplateHeader(ws.PackageDir, rdx, e));
                matched.Add(r.FileKtid);
            }
            foreach (var b in builder.Blocks)
                rdb.Redirect(rdb.Find(b.Ktid)!, newId, b.Offset, b.BlockSize, b.RawSize);
            var rdx2 = rdx.WithEntry(newId, modsHash);

            File.WriteAllBytes(Path.Combine(ws.PackageDir, modsName), builder.ToBytes());
            File.WriteAllBytes(liveRdb, rdb.Data);
            File.WriteAllBytes(liveRdx, rdx2.Data);

            record.Databases.Add(new DbPatchInfo
            {
                Db = db,
                ModsFdata = modsName,
                Replacements = here.Select(h => $"0x{h.FileKtid:x8}").ToList(),
                RdbSize = rdb.Data.Length,
                RdbHash = Hash(rdb.Data),
                RdxSize = rdx2.Data.Length,
                RdxHash = Hash(rdx2.Data),
            });
            notes.Add($"{db}: {here.Count} asset(s) → {modsName} (fdata_id {newId})");
            dbIndex++;
        }

        if (record.Databases.Count > 0) record.Save(ws.PatchRecordPath);
        else TryDelete(ws.PatchRecordPath);

        var notFound = replacements.Where(r => !matched.Contains(r.FileKtid)).Select(r => r.FileKtid).ToList();
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
        TryDelete(ws.PatchRecordPath);
    }

    /// <summary>
    /// rdbpatch.json 기록과 현재 rdb/rdx 지문을 대조해 상태 판별.
    /// 기록 없음=NotPatched, 일치=Patched, 불일치(업데이트/덮어씀)=NeedsReapply.
    /// </summary>
    public static PatchStatus GetStatus(GameWorkspace ws)
    {
        var rec = PatchRecord.Load(ws.PatchRecordPath);
        if (rec is null || rec.Databases.Count == 0) return PatchStatus.NotPatched;

        foreach (var d in rec.Databases)
        {
            if (!Matches(Path.Combine(ws.PackageDir, d.Db + ".rdb"), d.RdbSize, d.RdbHash) ||
                !Matches(Path.Combine(ws.PackageDir, d.Db + ".rdx"), d.RdxSize, d.RdxHash))
                return PatchStatus.NeedsReapply;
        }
        return PatchStatus.Patched;
    }

    public static PatchRecord? LoadRecord(GameWorkspace ws) => PatchRecord.Load(ws.PatchRecordPath);

    // ---- helpers ----

    private static bool Matches(string path, long size, string hash)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == size && Hash(bytes) == hash;
        }
        catch { return false; }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static bool IsModsHash(uint fileHash) => (fileHash >> 16) == 0xCA5E;

    private static bool RdxHasModsHash(string rdxPath)
    {
        try
        {
            if (!File.Exists(rdxPath)) return false;
            return RdxFile.Parse(File.ReadAllBytes(rdxPath)).Map.Values.Any(IsModsHash);
        }
        catch { return false; }
    }

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
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
