using Dapper;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>Pleasanter の DB（Users など）を読む実装を、RDBMS ごとに確かめる。</summary>
/// <remarks>
/// <para>
/// 表と列の名前は <c>_reference/Implem.Pleasanter</c> の定義と、実機の Pleasanter（SQL Server）で確認した。
/// ここでは同じ列名・同じ型の表を各 RDBMS に作り、**引用符・真偽値・日時・予約語（MySQL の <c>Groups</c>）**
/// の違いを実機へ当てる。
/// </para>
/// <para>
/// **SQLite は常に。SQL Server / PostgreSQL / MySQL は <c>IDP_INTEGRATION=1</c> のときだけ。**
/// </para>
/// </remarks>
public class PleasanterUserStoreTests
{
    public static IEnumerable<object[]> Providers() => TestDatabases.Providers();

    private static async Task<(PleasanterUserStore Store, PleasanterSeeder Seeder)> CreateAsync(DatabaseProvider provider)
    {
        // SQLite は呼ぶたびに新しいファイルになるので、1 度だけ決めて両方で使う
        var connectionString = TestDatabases.ConnectionString(provider);
        var factory = new DbConnectionFactory(provider, connectionString);
        await PleasanterSchema.CreateAsync(factory);
        return (new PleasanterUserStore(new PleasanterConnectionFactory(provider, connectionString)), new PleasanterSeeder(factory));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ログインIDで引ける大文字小文字は区別せず列の値が読める(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        var expiration = new DateTime(2030, 1, 2, 3, 4, 5);
        var userId = await seeder.AddUserAsync(
            "Taro.Yamada", "secret", name: "山田 太郎", tenantId: 3, tenantManager: true, serviceManager: false,
            secretKey: "JBSWY3DPEHPK3PXP", enableSecretKey: true, enableSecondaryAuthentication: true,
            passwordExpirationTime: expiration, deptId: 9);

        var user = await store.FindByLoginIdAsync("  taro.YAMADA ");

        Assert.NotNull(user);
        Assert.Equal(userId, user.UserId);
        Assert.Equal(3, user.TenantId);
        Assert.Equal("Taro.Yamada", user.LoginId);
        Assert.Equal("山田 太郎", user.Name);
        Assert.Equal("ja", user.Language);
        Assert.Equal(9, user.DeptId);
        Assert.True(user.TenantManager);
        Assert.False(user.ServiceManager);
        Assert.False(user.Disabled);
        Assert.False(user.Lockout);
        Assert.Equal(PleasanterPassword.Hash("secret"), user.PasswordHash);
        Assert.Equal("JBSWY3DPEHPK3PXP", user.SecretKey);
        Assert.True(user.EnableSecretKey);
        Assert.True(user.HasTotp);
        Assert.True(user.EnableSecondaryAuthentication);
        Assert.False(user.DisableSecondaryAuthentication);
        Assert.Equal(expiration, user.PasswordExpirationTime);
        Assert.Null(user.LastLoginTime);
        Assert.Equal(0, user.LoginExpirationPeriod);
        Assert.True(PleasanterPassword.Verify("secret", user.PasswordHash));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 存在しない人や空のログインIDはnull(DatabaseProvider provider)
    {
        var (store, _) = await CreateAsync(provider);

        Assert.Null(await store.FindByLoginIdAsync("nobody"));
        Assert.Null(await store.FindByLoginIdAsync(""));
        Assert.Null(await store.FindByLoginIdAsync("   "));
        Assert.Null(await store.FindByUserIdAsync(99999));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ログインIDの記号はSQLの文字として扱われない(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        await seeder.AddUserAsync("alice", "x");

        // 引数で渡しているので、SQL としては解釈されない（0 件になるだけ）
        Assert.Null(await store.FindByLoginIdAsync("alice' OR '1'='1"));
        Assert.Null(await store.FindByLoginIdAsync("al%"));
        Assert.Null(await store.FindByLoginIdAsync("a_ice"));
        Assert.Equal(1, await store.CountUsersAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 無効とロックアウトとTOTP無効の真偽値が読める(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        await seeder.AddUserAsync("flags", "x", disabled: true, lockout: true, serviceManager: true, disableSecondaryAuthentication: true);

        var user = (await store.FindByLoginIdAsync("flags"))!;

        Assert.True(user.Disabled);
        Assert.True(user.Lockout);
        Assert.True(user.ServiceManager);
        Assert.True(user.DisableSecondaryAuthentication);
        Assert.False(user.HasTotp);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じログインIDが複数のテナントにあればテナント番号の若い方(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        await seeder.AddUserAsync("dup", "x", tenantId: 5);
        await seeder.AddUserAsync("dup", "x", tenantId: 2);

        Assert.Equal(2, (await store.FindByLoginIdAsync("dup"))!.TenantId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task プロファイルにメールと部署とグループが載り無効なものは載らない(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        await seeder.AddDeptAsync(1, 7, "営業部");
        await seeder.AddDeptAsync(1, 8, "無効な部署", disabled: true);
        await seeder.AddGroupAsync(1, 70, "直接のグループ");
        await seeder.AddGroupAsync(1, 71, "部署経由のグループ");
        await seeder.AddGroupAsync(1, 72, "無効なグループ", disabled: true);
        await seeder.AddGroupAsync(1, 73, "他人のグループ");
        await seeder.AddGroupAsync(2, 74, "別テナントのグループ");
        var userId = await seeder.AddUserAsync("member", "x", deptId: 7, mail: "first@example.com");
        var otherUserId = await seeder.AddUserAsync("other", "x", deptId: 8);
        await using (var connection = seeder.Factory.Create())
        {
            await connection.OpenAsync();
            await seeder.AddMailAsync(connection, userId, "second@example.com");
            await seeder.AddMailAsync(connection, otherUserId, "other@example.com");

            // 同じ番号のグループ宛てのメールアドレス（OwnerType が違う）は、ユーザのものではない
            await connection.ExecuteAsync(SqlDialect.Format(provider,
                "INSERT INTO [MailAddresses] ([MailAddressId], [OwnerId], [OwnerType], [MailAddress]) VALUES (@Id, @OwnerId, 'Groups', 'group@example.com')"),
                new { Id = 99999L, OwnerId = (long)userId });
        }

        await seeder.AddGroupMemberAsync(70, userId: userId);
        await seeder.AddGroupMemberAsync(71, deptId: 7);
        await seeder.AddGroupMemberAsync(72, userId: userId);
        await seeder.AddGroupMemberAsync(73, userId: otherUserId);
        await seeder.AddGroupMemberAsync(74, userId: userId);

        var profile = await store.LoadProfileAsync((await store.FindByUserIdAsync(userId))!);

        Assert.Equal(["first@example.com", "second@example.com"], profile.MailAddresses);
        Assert.Equal("first@example.com", profile.PrimaryMail);
        Assert.Equal("営業部", profile.DeptName);
        Assert.Equal(new HashSet<string> { "直接のグループ", "部署経由のグループ" }, profile.Groups.ToHashSet());

        // 無効な部署は名前を出さない。他人のグループも出ない
        var other = await store.LoadProfileAsync((await store.FindByUserIdAsync(otherUserId))!);
        Assert.Null(other.DeptName);
        Assert.Equal(["other@example.com"], other.MailAddresses);
        Assert.Equal(["他人のグループ"], other.Groups);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 部署もグループも無い人でも読める(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        var userId = await seeder.AddUserAsync("plain", "x");

        var profile = await store.LoadProfileAsync((await store.FindByUserIdAsync(userId))!);

        Assert.Empty(profile.MailAddresses);
        Assert.Empty(profile.Groups);
        Assert.Null(profile.DeptName);
        Assert.Null(profile.PrimaryMail);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ユーザ数を数えられる(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        Assert.Equal(0, await store.CountUsersAsync());

        await seeder.AddUserAsync("u1", "x");
        await seeder.AddUserAsync("u2", "x");

        Assert.Equal(2, await store.CountUsersAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 所属するグループのIDが読め_対象を名前で探せる(DatabaseProvider provider)
    {
        var (store, seeder) = await CreateAsync(provider);
        await seeder.AddDeptAsync(1, 20, "開発部");
        await seeder.AddGroupAsync(1, 30, "管理者グループ");
        await seeder.AddGroupAsync(1, 31, "部署グループ");
        await seeder.AddGroupAsync(1, 32, "無関係");
        await seeder.AddGroupAsync(1, 33, "無効グループ", disabled: true);
        var userId = await seeder.AddUserAsync("Taro", "x", deptId: 20);
        await seeder.AddGroupMemberAsync(30, userId: userId);
        await seeder.AddGroupMemberAsync(31, deptId: 20);
        await seeder.AddGroupMemberAsync(33, userId: userId);

        var profile = await store.LoadProfileAsync((await store.FindByLoginIdAsync("taro"))!);

        Assert.Equal([30, 31], profile.GroupIds.OrderBy(id => id));
        Assert.Equal(2, profile.Groups.Count);

        var groups = await store.SearchDirectoryAsync("group", 1, "グループ", 10);
        Assert.Equal([30, 31], groups.Select(entry => entry.Id).OrderBy(id => id));
        Assert.Empty(await store.SearchDirectoryAsync("group", 2, "グループ", 10));
        Assert.Equal(20, Assert.Single(await store.SearchDirectoryAsync("dept", 1, "開発", 10)).Id);
        var users = await store.SearchDirectoryAsync("user", 1, "TARO", 10);
        Assert.Equal("Taro", Assert.Single(users).Detail);
        Assert.Empty(await store.SearchDirectoryAsync("user", 1, "100%", 10));
    }
}
