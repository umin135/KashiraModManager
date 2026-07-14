using Kashira.Core.Formats;

namespace Kashira.Core.Doa6;

/// <summary>
/// 셰이더 오버라이드 → 설치 계획(순수 로직). 에디터가 메시별로 셰이더 타입(matB)을 지정하면:
///   1) 각 메시에 <b>fresh 해시</b> 할당(전역 공유 해시 격리 — pristine sid 미등록·미충돌).
///   2) g1m 메시그룹 해시를 fresh 로 재작성할 <b>rename 맵</b>.
///   3) fresh 해시를 셰이더 타입의 <b>도너로 sid 등록</b>(도너 자식 통째 복사 = matB 셰이더 + 소프트/리지드).
/// 텍스처는 그대로(메시의 g1m 재질 유지) — 셰이더만 바뀐다.
///
/// 멱등: pristine sid 는 항상 동일하므로, 같은 오버라이드 집합이면 같은 할당(정렬 결정적).
/// 참조: [[editor-3layer-role-split]], _docs/_plans/material_physics_role_split.md §4
/// </summary>
public static class ShaderOverridePlan
{
    public sealed record Result(
        IReadOnlyDictionary<uint, uint> RenameMap,            // g1m: oldMeshHash → freshHash
        IReadOnlyList<SidInstaller.Registration> SidRegs);    // sid: freshHash ← 도너 셰이더

    /// <param name="overrides">메시 이름해시 → 지정 셰이더(matB). 카탈로그에서 도너 해석.</param>
    /// <param name="catalog">셰이더 타입 → 도너 메시.</param>
    /// <param name="sid">pristine Character.sid — fresh 해시 할당(미등록 확인) + 도너 유효성.</param>
    /// <param name="allocBase">fresh 해시 할당 시작값(전역 상수 범위).</param>
    public static Result Build(IReadOnlyDictionary<uint, uint> overrides, ShaderCatalog catalog,
                              CharacterSid sid, uint allocBase = 0x0FA10000)
    {
        var rename = new Dictionary<uint, uint>();
        var regs = new List<SidInstaller.Registration>();
        var used = new HashSet<uint>();
        uint next = allocBase;

        // 정렬 → 결정적 할당(멱등).
        foreach (var mesh in overrides.Keys.OrderBy(h => h))
        {
            uint matB = overrides[mesh];
            var entry = catalog.Get(matB);
            if (entry?.DonorMeshHash is not { } donor) continue;   // 카탈로그/도너 없음 → skip
            if (!sid.IsRegistered(donor)) continue;                // 도너 미등록 → skip(방어)

            uint fresh = AllocHash(ref next, sid, used);
            rename[mesh] = fresh;
            regs.Add(new SidInstaller.Registration(fresh, donor));
        }

        return new Result(rename, regs);
    }

    /// <summary>pristine sid 미등록 + 미할당인 다음 해시.</summary>
    private static uint AllocHash(ref uint next, CharacterSid sid, HashSet<uint> used)
    {
        while (sid.IsRegistered(next) || used.Contains(next)) next++;
        used.Add(next);
        return next++;
    }
}
