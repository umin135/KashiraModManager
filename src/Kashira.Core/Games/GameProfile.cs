namespace Kashira.Core.Games;

/// <summary>
/// 알려진(테스트된) KatanaEngine 게임 정보. appid 로 매칭되는 선택적 오버라이드.
/// 감지 자체는 제네릭(root.rdb 존재)으로 하므로, 프로파일이 없어도 게임은 목록에 뜬다.
/// </summary>
public sealed class GameProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required int SteamAppId { get; init; }
    public string PackageDir { get; init; } = "fdata_package";

    /// <summary>오버라이드 검증 상태 (실측으로 모드 로드 확인됨).</summary>
    public bool Verified { get; init; }

    public static readonly IReadOnlyList<GameProfile> All = new[]
    {
        new GameProfile
        {
            Id = "doa6lr", DisplayName = "Dead or Alive 6 Last Round",
            SteamAppId = 4144680, Verified = true, // 2026-07-03 마젠타 실측
        },
        new GameProfile
        {
            Id = "vvprism", DisplayName = "Venus Vacation PRISM - DEAD OR ALIVE Xtreme -",
            SteamAppId = 3155730,
        },
        new GameProfile
        {
            Id = "ff2remake", DisplayName = "Fatal Frame II: Crimson Butterfly REMAKE",
            SteamAppId = 3920610,
        },
    };

    public static GameProfile? ByAppId(int appid) =>
        All.FirstOrDefault(p => p.SteamAppId == appid);
}
