using System.Buffers.Binary;
using System.Security.Cryptography;
using Kashira.Core.Formats;
using Kashira.Core.Games;

namespace Kashira.Core.Patching;

/// <summary>
/// 패치 엔진. 원본 불변 — 항상 pristine 원본에서 재계산.
/// 각 에셋: RDB 에 있으면 리다이렉트(전략 B), 없으면 같은 타입 엔트리를 복제해 신규 등록(전략 D).
/// 복수 DB(root/system) 인지. Apply 시 _Kashira/rdbpatch.json 에 지문 기록 → 업데이트로 rdb 덮이면 감지.
/// </summary>
public static class PatchEngine
{
    private const uint ModsHashBase = 0xCA5E0001;
    private const string ModsGlob = "0xca5e*.fdata";

    /// <summary>Ext 는 신규 등록 시 타입 결정용(파일 확장자). 기존 에셋 교체엔 불필요.</summary>
    public sealed record Replacement(uint FileKtid, byte[] AssetBytes, string? Ext = null);

    public sealed record Report(int Requested, int Applied, IReadOnlyList<uint> NotFound, IReadOnlyList<string> Notes);

    private sealed record DbCtx(int Index, string Db, string LiveRdb, string LiveRdx, RdbFile Rdb, RdxFile Rdx);

    public static Report Apply(GameWorkspace ws, IReadOnlyList<Replacement> replacements)
    {
        Directory.CreateDirectory(ws.BackupDir);
        var notes = new List<string>();
        var matched = new HashSet<uint>();
        var record = new PatchRecord { PatchedAtUtc = DateTime.UtcNow.ToString("o") };

        foreach (var f in SafeGlob(ws.PackageDir, ModsGlob)) TryDelete(f);

        // Phase 1 — pristine rdb/rdx 로드 (rebaseline 포함)
        var loaded = new List<DbCtx>();
        int dbIndex = 0;
        foreach (var db in ws.Databases)
        {
            string liveRdb = Path.Combine(ws.PackageDir, db + ".rdb");
            string liveRdx = Path.Combine(ws.PackageDir, db + ".rdx");
            string bakRdb = Path.Combine(ws.BackupDir, db + ".rdb");
            string bakRdx = Path.Combine(ws.BackupDir, db + ".rdx");
            if (!File.Exists(liveRdb) || !File.Exists(liveRdx)) { dbIndex++; continue; }

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

            loaded.Add(new DbCtx(dbIndex, db, liveRdb, liveRdx,
                RdbFile.Parse(File.ReadAllBytes(bakRdb)),
                RdxFile.Parse(File.ReadAllBytes(bakRdx))));
            dbIndex++;
        }

        // Phase 2 — replacement 을 DB에 배정 (redirect vs 신규)
        var work = loaded.ToDictionary(c => c.Db, _ => new List<(Replacement r, bool isNew, uint typeKtid)>());
        foreach (var r in replacements)
        {
            var host = loaded.FirstOrDefault(c => c.Rdb.Find(r.FileKtid) is not null);
            if (host is not null) { work[host.Db].Add((r, false, 0)); continue; }

            if (!AssetTypes.TryGetTypeKtid(r.Ext, out var typeKtid))
            {
                notes.Add($"0x{r.FileKtid:x8}: new asset but unknown extension '{r.Ext}' — skipped");
                continue;
            }
            var target = PickTargetDb(loaded, typeKtid);
            if (target is null) { notes.Add($"0x{r.FileKtid:x8}: no target database — skipped"); continue; }
            work[target.Db].Add((r, true, typeKtid));
        }

        // Phase 3 — DB별 처리
        foreach (var c in loaded)
        {
            var items = work[c.Db];
            if (items.Count == 0)
            {
                File.Copy(Path.Combine(ws.BackupDir, c.Db + ".rdb"), c.LiveRdb, true);
                File.Copy(Path.Combine(ws.BackupDir, c.Db + ".rdx"), c.LiveRdx, true);
                continue;
            }

            int newId = c.Rdx.NextFreeId;
            uint modsHash = PickModsHash(c.Rdx, c.Index);
            string modsName = RdxFile.FdataName(modsHash);
            var builder = new ModsFdataBuilder();
            var newEntries = new List<(uint Ktid, byte[] Blob)>();
            int redirected = 0, added = 0;

            foreach (var (r, isNew, typeKtid) in items)
            {
                if (isNew)
                {
                    var template = c.Rdb.FindByType(typeKtid);
                    if (template is null)
                    {
                        notes.Add($"{c.Db}: 0x{r.FileKtid:x8}: no template entry for type 0x{typeKtid:x8} — skipped");
                        continue;
                    }
                    var placed = builder.Add(r.FileKtid, r.AssetBytes, ReadTemplatePrefix(ws.PackageDir, c.Rdx, template), compress: false);
                    var blob = c.Rdb.BuildClonedEntry(template, r.FileKtid, newId, placed.Offset, placed.BlockSize, placed.RawSize);
                    ClearBlobCompression(blob); // 무압축 저장에 맞춰 새 엔트리 압축플래그 해제
                    newEntries.Add((r.FileKtid, blob));
                    added++;
                }
                else
                {
                    var e = c.Rdb.Find(r.FileKtid)!;
                    var placed = builder.Add(r.FileKtid, r.AssetBytes, ReadTemplatePrefix(ws.PackageDir, c.Rdx, e), compress: false);
                    c.Rdb.Redirect(e, newId, placed.Offset, placed.BlockSize, placed.RawSize);
                    c.Rdb.SetUncompressed(e); // 무압축 저장에 맞춰 엔트리 압축플래그 해제
                    redirected++;
                }
                matched.Add(r.FileKtid);
            }

            // 리다이렉트 완료 후 신규 엔트리를 정렬 위치에 삽입 (재구성)
            c.Rdb.InsertEntriesSorted(newEntries);

            var rdx2 = c.Rdx.WithEntry(newId, modsHash);
            File.WriteAllBytes(Path.Combine(ws.PackageDir, modsName), builder.ToBytes());
            File.WriteAllBytes(c.LiveRdb, c.Rdb.Data);
            File.WriteAllBytes(c.LiveRdx, rdx2.Data);

            record.Databases.Add(new DbPatchInfo
            {
                Db = c.Db,
                ModsFdata = modsName,
                Replacements = items.Select(it => $"0x{it.r.FileKtid:x8}").ToList(),
                RdbSize = c.Rdb.Data.Length,
                RdbHash = Hash(c.Rdb.Data),
                RdxSize = rdx2.Data.Length,
                RdxHash = Hash(rdx2.Data),
            });
            notes.Add($"{c.Db}: {redirected} redirected, {added} added → {modsName} (fdata_id {newId})");
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

    /// <summary>rdbpatch.json 지문과 현재 rdb/rdx 대조. 없음=NotPatched, 일치=Patched, 불일치=NeedsReapply.</summary>
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

    private static DbCtx? PickTargetDb(List<DbCtx> loaded, uint typeKtid)
    {
        if (typeKtid == AssetTypes.G1s)
        {
            var sys = loaded.FirstOrDefault(c => c.Db.Equals("system", StringComparison.OrdinalIgnoreCase));
            if (sys is not null) return sys;
        }
        return loaded.FirstOrDefault(c => c.Db.Equals("root", StringComparison.OrdinalIgnoreCase))
               ?? loaded.FirstOrDefault();
    }

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

    /// <summary>클론된 엔트리 blob 의 CompressionType(flags bit 20-25)을 None 으로.</summary>
    private static void ClearBlobCompression(byte[] blob)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x2C)) & ~(0x3Fu << 20);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x2C), flags);
    }

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

    /// <summary>
    /// 원본 블록의 payload 앞 전체 prefix(헤더 + 타입별 param 영역, = total−comp 바이트)를 읽는다.
    /// g1m 처럼 헤더 뒤 param 영역이 있는 타입을 위해 0x58 헤더만이 아니라 payload 시작까지 보존.
    /// </summary>
    private static byte[] ReadTemplatePrefix(string packageDir, RdxFile rdx, RdbEntry e)
    {
        try
        {
            if (!rdx.Map.TryGetValue(e.FdataId, out var hash)) return Array.Empty<byte>();
            var path = Path.Combine(packageDir, RdxFile.FdataName(hash));
            using var fs = File.OpenRead(path);
            fs.Seek(e.FdataOffset, SeekOrigin.Begin);

            var head = new byte[IdrkBlock.HeaderSize];
            if (fs.Read(head, 0, head.Length) != head.Length) return Array.Empty<byte>();
            long total = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x08));
            long comp = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x10));
            int prefixLen = (int)(total - comp);
            if (prefixLen < 0x20 || prefixLen > 0x10000) return head; // 이상치 → 헤더만

            var prefix = new byte[prefixLen];
            fs.Seek(e.FdataOffset, SeekOrigin.Begin);
            return fs.Read(prefix, 0, prefixLen) == prefixLen ? prefix : head;
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
