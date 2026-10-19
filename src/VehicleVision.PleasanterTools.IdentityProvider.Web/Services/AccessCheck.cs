using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>アクセスのルール（Include / Exclude）をユーザーへ当てる。</summary>
public static class AccessCheck
{
    /// <summary>ルールが空なら Pleasanter を引かずに通す（所属の読み込みは、ルールがあるときだけ）。</summary>
    public static async Task<bool> AllowsAsync(
        IPleasanterUserStore users,
        PleasanterUser user,
        IReadOnlyList<AccessRule> rules,
        CancellationToken cancellationToken)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var profile = await users.LoadProfileAsync(user, cancellationToken).ConfigureAwait(false);
        return AccessRules.Allows(rules, new AccessSubject(user.UserId, user.DeptId, profile.GroupIds));
    }
}
