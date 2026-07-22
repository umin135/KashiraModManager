namespace Kashira.Core.Patching;

/// <summary>
/// 패치 진행 상황 스냅샷. Total==0 이면 불확정(indeterminate) 단계(예: 모드 로딩).
/// Total&gt;0 이면 Done/Total 로 확정 진행률. Message 는 사용자에게 보일 짧은 상태문.
/// </summary>
public sealed record PatchProgress(int Done, int Total, string Message)
{
    /// <summary>확정 단계면 0..1 비율, 불확정이면 null.</summary>
    public double? Fraction => Total > 0 ? (double)Done / Total : null;
}
