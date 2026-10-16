using System.Data.Common;
using Dapper;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>Pleasanter の DB へ繋ぐ（IdP 自身の DB とは別）。</summary>
public interface IPleasanterConnectionFactory : IDbConnectionFactory;

/// <summary><see cref="DbConnectionFactory"/> を、Pleasanter 向けとして区別できる型に包む。</summary>
public sealed class PleasanterConnectionFactory : IPleasanterConnectionFactory
{
    private readonly DbConnectionFactory inner;

    public PleasanterConnectionFactory(DatabaseProvider provider, string connectionString)
    {
        inner = new DbConnectionFactory(provider, connectionString);
    }

    public DatabaseProvider Provider => inner.Provider;

    public DbConnection Create() => inner.Create();
}

/// <summary>Pleasanter の <c>Users</c> などを読む。</summary>
public interface IPleasanterUserStore
{
    /// <summary>ログイン ID（大文字小文字を区別しない）で探す。</summary>
    Task<PleasanterUser?> FindByLoginIdAsync(string loginId, CancellationToken cancellationToken = default);

    Task<PleasanterUser?> FindByUserIdAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>メールアドレス・所属・グループを読む。</summary>
    Task<PleasanterUserProfile> LoadProfileAsync(
        PleasanterUser user,
        CancellationToken cancellationToken = default);

    /// <summary>接続の確認を兼ねたユーザ数。</summary>
    Task<long> CountUsersAsync(CancellationToken cancellationToken = default);
}

/// <summary>Pleasanter の DB を<b>読み取り専用</b>で使う実装。</summary>
/// <remarks>
/// <para>
/// **この型が発行する SQL は <c>SELECT</c> だけ。** Pleasanter の DB へは書かない
/// （連続失敗の回数や TOTP の使用済みは、IdP 自身の DB に持つ）。
/// 運用では <b>読み取り専用の DB ユーザ</b>を割り当てること（<c>_documents/設定パラメータ一覧.md</c>）。
/// </para>
/// <para>
/// 表と列の名前は <c>_reference/Implem.Pleasanter</c> の
/// <c>App_Data/Definitions/Definition_Column</c> で確認している
/// （<c>Users</c> / <c>MailAddresses</c> / <c>Depts</c> / <c>Groups</c> / <c>GroupMembers</c>）。
/// </para>
/// <para>
/// グループは、本人が直接のメンバー、または所属部署がメンバーのもの。
/// グループの入れ子（<c>ChildGroup</c>）は辿らない。
/// </para>
/// </remarks>
public sealed class PleasanterUserStore(IPleasanterConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), IPleasanterUserStore
{
    /// <summary><c>MailAddresses.OwnerType</c> のユーザを表す値（<c>UserModel.cs</c> の <c>OwnerType("Users")</c>）。</summary>
    private const string UserOwnerType = "Users";

    private const string UserColumns =
        "[TenantId], [UserId], [LoginId], [GlobalId], [Name], [UserCode], [Language], [TimeZone], "
        + "[DeptId], [Disabled], [Lockout], [TenantManager], [ServiceManager], "
        + "[Password] AS [PasswordHash], [PasswordExpirationTime], [LoginExpirationLimit], "
        + "[LoginExpirationPeriod], [LastLoginTime], [SecretKey], [EnableSecretKey], "
        + "[EnableSecondaryAuthentication], [DisableSecondaryAuthentication]";

    public async Task<PleasanterUser?> FindByLoginIdAsync(
        string loginId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(loginId))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var trimmed = loginId.Trim();

        // **まず、入力のまま比べる。** 索引が使える。大文字小文字の扱いは DB の照合順序に従う
        // （SQL Server は既定で区別せず、PostgreSQL は区別する。Pleasanter 本体のログインと同じ）。
        // **見つからなければ、小文字にして比べ直す。** どの RDBMS でも大文字小文字を区別せずに入れるようにするため。
        // 後者は表の走査になるが、実在しない ID の試みは IP ごとのレート制限とログイン ID ごとの締め出しで絞られる。
        // **同じログイン ID が複数あれば、テナント ID の若い方。** Pleasanter も先頭を採る
        var exact = await connection.QueryAsync<PleasanterUser>(Sql(
            "SELECT " + UserColumns + " FROM [Users] WHERE [LoginId] = @LoginId ORDER BY [TenantId], [UserId]",
            new { LoginId = trimmed },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exact.FirstOrDefault() is { } found)
        {
            return found;
        }

        var folded = await connection.QueryAsync<PleasanterUser>(Sql(
            "SELECT " + UserColumns + " FROM [Users] WHERE LOWER([LoginId]) = @LoginId ORDER BY [TenantId], [UserId]",
            new { LoginId = trimmed.ToLowerInvariant() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return folded.FirstOrDefault();
    }

    public async Task<PleasanterUser?> FindByUserIdAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<PleasanterUser>(Sql(
            "SELECT " + UserColumns + " FROM [Users] WHERE [UserId] = @UserId",
            new { UserId = userId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PleasanterUserProfile> LoadProfileAsync(
        PleasanterUser user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var mails = (await connection.QueryAsync<string>(Sql(
            "SELECT [MailAddress] FROM [MailAddresses] "
            + "WHERE [OwnerType] = @OwnerType AND [OwnerId] = @UserId ORDER BY [MailAddressId]",
            new { OwnerType = UserOwnerType, user.UserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Where(mail => !string.IsNullOrWhiteSpace(mail))
            .ToList();

        string? deptName = null;
        if (user.DeptId > 0)
        {
            deptName = await connection.QueryFirstOrDefaultAsync<string>(Sql(
                "SELECT [DeptName] FROM [Depts] "
                + "WHERE [TenantId] = @TenantId AND [DeptId] = @DeptId AND [Disabled] = @Disabled",
                new { user.TenantId, user.DeptId, Disabled = false },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // 本人が直接のメンバー、または所属部署がメンバー。
        // GroupMembers の UserId / DeptId は「該当なし」を 0 で表す
        var membership = user.DeptId > 0
            ? "(m.[UserId] = @UserId OR m.[DeptId] = @DeptId)"
            : "m.[UserId] = @UserId";

        var groups = (await connection.QueryAsync<string>(Sql(
            "SELECT DISTINCT g.[GroupName] FROM [Groups] g "
            + "INNER JOIN [GroupMembers] m ON m.[GroupId] = g.[GroupId] "
            + "WHERE g.[TenantId] = @TenantId AND g.[Disabled] = @Disabled AND " + membership + " "
            + "ORDER BY g.[GroupName]",
            new { user.TenantId, user.UserId, user.DeptId, Disabled = false },
            cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return new PleasanterUserProfile(user, mails, groups, deptName);
    }

    public async Task<long> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(Sql(
            "SELECT COUNT(*) FROM [Users]",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
