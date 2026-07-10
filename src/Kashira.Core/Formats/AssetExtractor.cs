using System.Buffers.Binary;
using Kashira.Core.Games;

namespace Kashira.Core.Formats;

/// <summary>
/// FileKtid → 원본 에셋 바이트 추출기. 인덱스(rdb/rdx)는 깨끗한 백업 우선(없으면 라이브),
/// 데이터(.fdata)는 라이브 PackageDir 에서 블록만 seek-read(전체 로드 안 함).
/// 전략 B 는 원본 .fdata 를 건드리지 않으므로 라이브 fdata = 원본과 동일.
/// 참조: tools/verify/ktmod_install.py GameDB.extract
/// </summary>
public sealed class AssetExtractor : IDisposable
{
    private sealed record DbIndex(RdbFile Rdb, RdxFile Rdx);

    private readonly string _fdataDir;
    private readonly List<DbIndex> _dbs = new();
    private readonly Dictionary<string, FileStream> _fdata = new(StringComparer.OrdinalIgnoreCase);

    private AssetExtractor(string fdataDir) => _fdataDir = fdataDir;

    /// <summary>워크스페이스에서 열기. 백업 rdb/rdx 가 있으면 그것을(깨끗함), 없으면 라이브를 인덱스로 사용.</summary>
    public static AssetExtractor Open(GameWorkspace ws)
    {
        var ex = new AssetExtractor(ws.PackageDir);
        foreach (var db in ws.Databases)
        {
            string bakRdb = Path.Combine(ws.BackupDir, db + ".rdb");
            string bakRdx = Path.Combine(ws.BackupDir, db + ".rdx");
            string liveRdb = Path.Combine(ws.PackageDir, db + ".rdb");
            string liveRdx = Path.Combine(ws.PackageDir, db + ".rdx");
            string rdbPath = File.Exists(bakRdb) ? bakRdb : liveRdb;
            string rdxPath = File.Exists(bakRdx) ? bakRdx : liveRdx;
            if (!File.Exists(rdbPath) || !File.Exists(rdxPath)) continue;
            ex._dbs.Add(new DbIndex(RdbFile.Load(rdbPath), RdxFile.Load(rdxPath)));
        }
        return ex;
    }

    public RdbEntry? Find(uint fileKtid)
    {
        foreach (var db in _dbs)
            if (db.Rdb.Find(fileKtid) is { } e) return e;
        return null;
    }

    /// <summary>FileKtid 의 원본(압축해제된) 에셋 바이트. 없으면 null.</summary>
    public byte[]? Extract(uint fileKtid)
    {
        foreach (var db in _dbs)
        {
            var e = db.Rdb.Find(fileKtid);
            if (e is null) continue;
            if (!db.Rdx.Map.TryGetValue(e.FdataId, out var hash)) return null;
            var fs = GetFdata(RdxFile.FdataName(hash));
            if (fs is null) return null;

            fs.Seek(e.FdataOffset, SeekOrigin.Begin);
            var head = new byte[IdrkBlock.HeaderSize];
            if (fs.Read(head, 0, head.Length) != head.Length) return null;
            long total = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x08));
            if (total < IdrkBlock.HeaderSize || total > int.MaxValue) return null;

            var block = new byte[total];
            fs.Seek(e.FdataOffset, SeekOrigin.Begin);
            if (fs.Read(block, 0, block.Length) != block.Length) return null;
            return IdrkBlock.Extract(block, 0);
        }
        return null;
    }

    private FileStream? GetFdata(string name)
    {
        if (_fdata.TryGetValue(name, out var fs)) return fs;
        string path = Path.Combine(_fdataDir, name);
        if (!File.Exists(path)) { _fdata[name] = null!; return null; }
        fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _fdata[name] = fs;
        return fs;
    }

    public void Dispose()
    {
        foreach (var fs in _fdata.Values) fs?.Dispose();
        _fdata.Clear();
    }
}
