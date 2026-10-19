namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;

/// <summary>連携先（またはこの管理画面）へ入れる人を、ユーザー・組織・グループで絞るルール 1 件。</summary>
/// <param name="Effect"><see cref="AccessRules.Include"/> か <see cref="AccessRules.Exclude"/>。</param>
/// <param name="Kind"><see cref="AccessRules.User"/> / <see cref="AccessRules.Dept"/> / <see cref="AccessRules.Group"/>。</param>
/// <param name="TargetId">Pleasanter の <c>UserId</c> / <c>DeptId</c> / <c>GroupId</c>。</param>
/// <param name="Label">画面に出す名前（登録時のもの。判定には使わない）。</param>
public sealed record AccessRule(string Effect, string Kind, int TargetId, string? Label = null);

/// <summary>ユーザーの所属（判定に使う ID だけ）。</summary>
public sealed record AccessSubject(int UserId, int DeptId, IReadOnlyCollection<int> GroupIds);

/// <summary>Include / Exclude の判定。</summary>
/// <remarks>
/// <list type="number">
/// <item><b>Exclude に 1 件でも当たれば拒否。</b>Include より強い</item>
/// <item>Include が 1 件でもあれば、<b>どれかに当たる人だけ</b>許可</item>
/// <item>ルールが無い（または Exclude だけで当たらない）なら許可</item>
/// </list>
/// 組織は <c>Users.DeptId</c>、グループは直接のメンバーか所属組織がメンバーのもの
/// （グループの入れ子は辿らない。<c>PleasanterUserStore</c> と同じ）。
/// </remarks>
public static class AccessRules
{
    public const string Include = "include";

    public const string Exclude = "exclude";

    public const string User = "user";

    public const string Dept = "dept";

    public const string Group = "group";

    /// <summary>1 連携先に付けられるルールの上限。</summary>
    public const int MaxRules = 200;

    public static bool Allows(IReadOnlyCollection<AccessRule> rules, AccessSubject subject)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(subject);

        var hasInclude = false;
        var included = false;
        foreach (var rule in rules)
        {
            var matches = Matches(rule, subject);
            if (rule.Effect == Exclude)
            {
                if (matches)
                {
                    return false;
                }
            }
            else
            {
                hasInclude = true;
                included |= matches;
            }
        }

        return !hasInclude || included;
    }

    public static bool Matches(AccessRule rule, AccessSubject subject) => rule.Kind switch
    {
        User => rule.TargetId == subject.UserId,
        Dept => subject.DeptId > 0 && rule.TargetId == subject.DeptId,
        Group => subject.GroupIds.Contains(rule.TargetId),
        _ => false,
    };

    public static IReadOnlyList<string> Validate(IReadOnlyCollection<AccessRule> rules)
    {
        var errors = new List<string>();
        if (rules.Count > MaxRules)
        {
            errors.Add($"アクセスのルールは {MaxRules} 件まで");
        }

        foreach (var rule in rules)
        {
            if (rule.Effect is not (Include or Exclude))
            {
                errors.Add("アクセスのルールの種別は include か exclude");
            }

            if (rule.Kind is not (User or Dept or Group))
            {
                errors.Add("アクセスのルールの対象は user・dept・group のどれか");
            }

            if (rule.TargetId <= 0)
            {
                errors.Add("アクセスのルールの ID は 1 以上の整数");
            }

            if (rule.Label is { Length: > 200 })
            {
                errors.Add("アクセスのルールの名前は 200 文字まで");
            }
        }

        // 同じ対象を include と exclude の両方に入れるのは矛盾（exclude が勝つが、設定の誤りの可能性が高い）
        var conflict = rules
            .GroupBy(rule => (rule.Kind, rule.TargetId))
            .Any(group => group.Select(rule => rule.Effect).Distinct().Count() > 1);
        if (conflict)
        {
            errors.Add("同じ対象を include と exclude の両方へ入れられない");
        }

        return [.. errors.Distinct()];
    }

    /// <summary>設定値（<c>user:12,dept:3,group:5</c>）を読む。</summary>
    /// <exception cref="FormatException">形が違うとき（起動時に気付けるよう、黙って捨てない）。</exception>
    public static IReadOnlyList<AccessRule> ParseList(string? text, string effect)
    {
        var rules = new List<AccessRule>();
        foreach (var token in (text ?? string.Empty).Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', 2);
            if (parts.Length != 2
                || parts[0].ToLowerInvariant() is not (User or Dept or Group)
                || !int.TryParse(parts[1], out var id)
                || id <= 0)
            {
                throw new FormatException($"'{token}' は user:ID / dept:ID / group:ID の形で書く。");
            }

            rules.Add(new AccessRule(effect, parts[0].ToLowerInvariant(), id));
        }

        return rules;
    }
}
