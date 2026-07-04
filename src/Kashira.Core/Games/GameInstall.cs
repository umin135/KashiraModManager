namespace Kashira.Core.Games;

/// <summary>
/// 감지되었거나 수동 추가된 게임 설치 항목. 런처 목록의 한 행.
/// </summary>
public sealed class GameInstall
{
    /// <summary>안정적 식별자 (프로파일 매칭 시 프로파일 id, 아니면 "app{appid}").</summary>
    public required string Id { get; init; }

    /// <summary>표시 이름 (Steam 매니페스트 name).</summary>
    public required string DisplayName { get; init; }

    /// <summary>게임 설치 루트 경로.</summary>
    public required string InstallPath { get; init; }

    /// <summary>Steam AppId (0 = 수동 추가 등 미상).</summary>
    public int SteamAppId { get; init; }

    /// <summary>rdb/rdx/fdata 가 있는 하위 폴더 (보통 fdata_package).</summary>
    public string PackageDir { get; init; } = "fdata_package";

    /// <summary>자동 탐색된 인덱스 DB 이름들 (예: root, system). *.rdb↔*.rdx 쌍.</summary>
    public IReadOnlyList<string> Databases { get; init; } = Array.Empty<string>();

    /// <summary>KatanaEngine 게임으로 감지됨(root.rdb 존재).</summary>
    public bool Supported { get; init; }

    /// <summary>appid로 매칭된 알려진 프로파일 id (nullable — 제네릭 감지 시 null).</summary>
    public string? ProfileId { get; init; }

    /// <summary>오버라이드가 실측 검증된 게임인지.</summary>
    public bool Verified { get; init; }

    /// <summary>대표 실행 파일 경로 (아이콘 추출용). 못 찾으면 null.</summary>
    public string? ExePath { get; init; }
}
