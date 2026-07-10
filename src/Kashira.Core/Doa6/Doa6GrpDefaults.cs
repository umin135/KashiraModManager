namespace Kashira.Core.Doa6;

/// <summary>
/// DOA6LR grp parts_name_hash 공통값(실측 서베이: 2924 grp). parts_name_hash 는 g1m 에서 유도 불가하고
/// 유효값 재사용이 필수라, DOA6LR 전용 공통 hash 를 여기 기억해 grp 자동생성/검증에 쓴다.
/// 파츠 슬라이싱(set_count_t/T, count_t/T)은 g1m mesh entry 구조에서 계산하고, 파츠 이름 hash 만 여기서 배정.
/// 실측: 카스미 전 코스튬의 메인 바디 파츠는 항상 MainBody, 보조 파츠는 AuxA/AuxB.
/// </summary>
public static class Doa6GrpDefaults
{
    /// <summary>메인 바디 파츠 — 484 grp(전체 최다), 캐릭터 공통 기본 바디. 단일 파츠 grp 의 기본값.</summary>
    public const uint MainBody = 0x3057221F;

    /// <summary>보조 파츠 A — 36 grp.</summary>
    public const uint AuxA = 0x0017401F;

    /// <summary>보조 파츠 B — 13 grp.</summary>
    public const uint AuxB = 0x0017B47E;

    /// <summary>파츠 수만큼 앞에서부터 배정할 공통 hash 우선순위(자동생성용).</summary>
    public static readonly uint[] CommonByParts = { MainBody, AuxA, AuxB };
}
