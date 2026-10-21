using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>IdP 自身の DB の各ストアを、RDBMS ごとに実機へ当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **SQLite は常に。SQL Server / PostgreSQL / MySQL は <c>IDP_INTEGRATION=1</c> のときだけ。**
/// 設定していないと実機の分は何も検証せずに緑になる。CI は必ず設定する。
/// </para>
/// <para>
/// **並行する要求で 1 度しか成功しないこと**（認可コード・リフレッシュトークン・TOTP の枠・
/// 保留中の要求）を、実際に並列で当てて確かめる。ここが破れると、盗まれたコードの再利用が通る。
/// </para>
/// </remarks>
public class StoreTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> Providers() => TestDatabases.Providers();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task マイグレーションを当てると全ての表が出来て二度目は何も当たらない(DatabaseProvider provider)
    {
        var (_, connectionString) = await TestDatabases.FreshAsync(provider);

        Assert.Empty(IdentityProviderDatabase.Instance.Migrator.PendingMigrations(provider, connectionString));
        var result = await IdentityProviderDatabase.Instance.Migrator.MigrateUpWithLockAsync(provider, connectionString, TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.AppliedCount);
    }

    // ---- クライアント・SP ---------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OIDCクライアントを保存して読み戻せる(DatabaseProvider provider)
    {
        var store = new OidcClientStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var client = new OidcClient
        {
            ClientId = "app-1",
            DisplayName = "日本語の名前 🚀",
            ClientType = OidcClient.Confidential,
            RedirectUris = ["https://app.example.com/cb", "https://app.example.com/cb2"],
            PostLogoutRedirectUris = ["https://app.example.com/bye"],
            AllowedScopes = ["openid", "profile"],
            AllowedTenantIds = [1, 3],
            AccessRules = [new AccessRule(AccessRules.Include, AccessRules.Group, 5, "営業 🚀"), new AccessRule(AccessRules.Exclude, AccessRules.User, 9)],
            AccessTokenLifetimeSeconds = 900,
        };

        Assert.True(await store.CreateAsync(client, "hash-1", Now));
        Assert.False(await store.CreateAsync(client, "hash-2", Now));

        var read = await store.FindAsync("app-1");
        Assert.NotNull(read);
        Assert.Equal(client.DisplayName, read.DisplayName);
        Assert.Equal(client.RedirectUris, read.RedirectUris);
        Assert.Equal(client.AllowedTenantIds, read.AllowedTenantIds);
        Assert.Equal(client.AccessRules, read.AccessRules);
        Assert.Equal(900, read.AccessTokenLifetimeSeconds);
        Assert.Null(read.RefreshTokenLifetimeSeconds);
        Assert.True(read.HasSecret);
        Assert.True(read.Enabled);
        Assert.Equal(DateTimeKind.Utc, read.CreatedAt.Kind);
        Assert.Equal(Now, read.CreatedAt);
        Assert.Equal("hash-1", await store.GetSecretHashAsync("app-1"));

        Assert.True(await store.UpdateAsync(read with { DisplayName = "更新", Enabled = false }, Now.AddMinutes(5)));
        var updated = await store.FindAsync("app-1");
        Assert.Equal("更新", updated!.DisplayName);
        Assert.False(updated.Enabled);
        Assert.Equal(Now.AddMinutes(5), updated.UpdatedAt);

        Assert.True(await store.SetSecretHashAsync("app-1", "hash-3", Now));
        Assert.Equal("hash-3", await store.GetSecretHashAsync("app-1"));
        Assert.Single(await store.ListAsync());
        Assert.True(await store.DeleteAsync("app-1"));
        Assert.False(await store.DeleteAsync("app-1"));
        Assert.Null(await store.FindAsync("app-1"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 公開クライアントにはシークレットを設定できない(DatabaseProvider provider)
    {
        var store = new OidcClientStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        await store.CreateAsync(new OidcClient { ClientId = "pub", DisplayName = "p", ClientType = OidcClient.Public, RedirectUris = ["https://a.example.com/cb"] }, null, Now);

        Assert.False(await store.SetSecretHashAsync("pub", "x", Now));
        Assert.False((await store.FindAsync("pub"))!.HasSecret);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SAMLのSPを保存して読み戻せエンティティIDは一意(DatabaseProvider provider)
    {
        var store = new SamlProviderStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var first = new SamlProvider
        {
            ProviderId = Guid.NewGuid().ToString(),
            EntityId = "https://sp.example.com/metadata",
            DisplayName = "SP 日本語",
            AcsUrl = "https://sp.example.com/acs",
            SigningCertificate = "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----",
            AttributeMappings = [new SamlAttributeMapping("mail", "email"), new SamlAttributeMapping("grp", "groups")],
            AllowedTenantIds = [1],
        };

        Assert.True(await store.CreateAsync(first, Now));
        Assert.False(await store.CreateAsync(first with { ProviderId = Guid.NewGuid().ToString() }, Now));

        var read = await store.FindByEntityIdAsync(first.EntityId);
        Assert.NotNull(read);
        Assert.Equal(first.ProviderId, read.ProviderId);
        Assert.Equal(first.AttributeMappings, read.AttributeMappings);
        Assert.Equal(first.SigningCertificate, read.SigningCertificate);
        Assert.True(read.SignAssertion);
        Assert.True(read.SignResponse);
        Assert.False(read.RequireSignedRequests);
        Assert.Equal((await store.FindAsync(first.ProviderId))!.EntityId, first.EntityId);

        var second = first with { ProviderId = Guid.NewGuid().ToString(), EntityId = "https://other.example.com/metadata" };
        Assert.True(await store.CreateAsync(second, Now));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateAsync(second with { EntityId = first.EntityId }, Now));

        Assert.Equal(2, (await store.ListAsync()).Count);
        Assert.True(await store.DeleteAsync(first.ProviderId));
        Assert.False(await store.DeleteAsync("not-a-guid"));
        Assert.Null(await store.FindAsync("not-a-guid"));
    }

    // ---- 署名鍵 -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 新しい鍵を入れると同じ用途の旧鍵は退役し退役した鍵だけ消せる(DatabaseProvider provider)
    {
        var store = new SigningKeyStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        StoredSigningKey Key(string id, string purpose, DateTime created) => new(
            new SigningKey(id, purpose, "RS256", SigningKey.Active, "thumb-" + id, created.AddMinutes(-5), created.AddYears(10), created),
            "encrypted-" + id,
            "pem-" + id);

        await store.AddActiveAsync(Key("k1", SigningKey.Oidc, Now));
        await store.AddActiveAsync(Key("s1", SigningKey.Saml, Now));
        await store.AddActiveAsync(Key("k2", SigningKey.Oidc, Now.AddMinutes(1)));

        var oidc = await store.ListWithMaterialAsync(SigningKey.Oidc);
        Assert.Equal(["k2", "k1"], oidc.Select(key => key.Meta.KeyId).ToArray());
        Assert.Equal(SigningKey.Active, oidc[0].Meta.Status);
        Assert.Equal(SigningKey.Retired, oidc[1].Meta.Status);
        Assert.Equal("encrypted-k2", oidc[0].PrivateKeyEncrypted);
        Assert.Equal(Now.AddMinutes(1), oidc[0].Meta.CreatedAt);

        // 他の用途は巻き込まない
        Assert.Equal(SigningKey.Active, (await store.FindAsync("s1"))!.Meta.Status);

        Assert.Equal(SigningKeyDeleteResult.StillActive, await store.DeleteRetiredAsync("k2"));
        Assert.Equal(SigningKeyDeleteResult.Deleted, await store.DeleteRetiredAsync("k1"));
        Assert.Equal(SigningKeyDeleteResult.NotFound, await store.DeleteRetiredAsync("k1"));
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    // ---- セッション ---------------------------------------------------------

    private static SsoSessionEntry Session(Guid id, int userId = 7, string loginId = "taro", DateTime? now = null) => new()
    {
        SessionId = id,
        TenantId = 1,
        UserId = userId,
        LoginId = loginId,
        UserName = "山田 太郎",
        AuthMethods = ["pwd", "otp"],
        AuthTime = now ?? Now,
        LastSeenAt = now ?? Now,
        ExpiresAt = (now ?? Now).AddHours(8),
        IpAddress = "203.0.113.5",
        UserAgent = "test-agent",
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task セッションの有効期限とアイドルと失効を見分ける(DatabaseProvider provider)
    {
        var store = new SsoSessionStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var id = Guid.NewGuid();
        await store.CreateAsync(Session(id));
        var idle = TimeSpan.FromHours(2);

        var found = await store.FindActiveAsync(id, Now.AddMinutes(10), idle);
        Assert.NotNull(found);
        Assert.Equal(["pwd", "otp"], found.AuthMethods);
        Assert.Equal("山田 太郎", found.UserName);
        Assert.Equal(DateTimeKind.Utc, found.AuthTime.Kind);

        // アイドル超過（最終アクセスから 2 時間）
        Assert.Null(await store.FindActiveAsync(id, Now.AddHours(3), idle));

        // 触れば延びる
        await store.TouchAsync(id, Now.AddHours(1));
        Assert.NotNull(await store.FindActiveAsync(id, Now.AddHours(2.5), idle));

        // 絶対期限
        Assert.Null(await store.FindActiveAsync(id, Now.AddHours(9), TimeSpan.FromDays(1)));

        Assert.False(await store.IsRevokedAsync(id));
        Assert.True(await store.RevokeAsync(id, Now.AddHours(2)));
        Assert.False(await store.RevokeAsync(id, Now.AddHours(2)));
        Assert.True(await store.IsRevokedAsync(id));
        Assert.Null(await store.FindActiveAsync(id, Now.AddHours(2), idle));

        // 行が無いものは失効扱いにしない（掃除で消えた古い行）
        Assert.False(await store.IsRevokedAsync(Guid.NewGuid()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task セッションの一覧と利用者単位の失効とログインIDの絞り込み(DatabaseProvider provider)
    {
        var store = new SsoSessionStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        await store.CreateAsync(Session(a1, 1, "alice", Now));
        await store.CreateAsync(Session(a2, 1, "alice", Now.AddMinutes(1)));
        await store.CreateAsync(Session(b1, 2, "bob_100%", Now.AddMinutes(2)));
        var idle = TimeSpan.FromHours(2);

        var all = await store.ListActiveAsync(null, Now.AddMinutes(5), idle, 50, 0);
        Assert.Equal([b1, a2, a1], all.Select(session => session.SessionId).ToArray());

        Assert.Equal(2, (await store.ListActiveAsync("ALI", Now.AddMinutes(5), idle, 50, 0)).Count);

        // LIKE の記号は逃がす（% や _ が「何でも」にならない。ログイン ID に含まれる文字そのものとして探す）
        Assert.Single(await store.ListActiveAsync("100%", Now.AddMinutes(5), idle, 50, 0));
        Assert.Single(await store.ListActiveAsync("%", Now.AddMinutes(5), idle, 50, 0));
        Assert.Single(await store.ListActiveAsync("_", Now.AddMinutes(5), idle, 50, 0));
        Assert.Empty(await store.ListActiveAsync("a_i", Now.AddMinutes(5), idle, 50, 0));

        Assert.Single(await store.ListActiveAsync(null, Now.AddMinutes(5), idle, 1, 1));

        Assert.Equal(2, await store.RevokeAllForUserAsync(1, Now.AddMinutes(6)));
        Assert.Single(await store.ListActiveAsync(null, Now.AddMinutes(7), idle, 50, 0));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じRPへの発行を何度記録しても一行(DatabaseProvider provider)
    {
        var store = new SsoSessionStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var id = Guid.NewGuid();
        await store.CreateAsync(Session(id));

        await store.AddParticipantAsync(new SessionParticipant(id, SessionParticipant.Oidc, "client-1", null, null), Now);
        await store.AddParticipantAsync(new SessionParticipant(id, SessionParticipant.Oidc, "client-1", null, null), Now);
        await store.AddParticipantAsync(new SessionParticipant(id, SessionParticipant.Saml, "https://sp/metadata", "_idx", "a@example.com"), Now);

        var participants = await store.ListParticipantsAsync(id);
        Assert.Equal(2, participants.Count);
        Assert.Contains(participants, p => p.Kind == SessionParticipant.Saml && p.SessionIndex == "_idx" && p.NameId == "a@example.com");
    }

    // ---- 認可コード・リフレッシュトークン ------------------------------------

    private static AuthorizationCodeEntry Code(string hash, DateTime expires) => new()
    {
        CodeHash = hash,
        ClientId = "c",
        SessionId = Guid.NewGuid(),
        TenantId = 1,
        UserId = 7,
        RedirectUri = "https://app.example.com/cb",
        Scope = "openid profile",
        CodeChallenge = "challenge",
        Nonce = "n",
        AuthTime = Now,
        AuthMethods = "pwd,otp",
        ExpiresAt = expires,
        FamilyId = Guid.NewGuid(),
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 認可コードは一度だけ使え期限切れや再利用を見分ける(DatabaseProvider provider)
    {
        var store = new AuthorizationCodeStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        await store.CreateAsync(Code("h1", Now.AddMinutes(1)));
        await store.CreateAsync(Code("h2", Now.AddMinutes(-1)));

        var first = await store.ConsumeAsync("h1", Now);
        Assert.Equal(CodeConsumeStatus.Consumed, first.Status);
        Assert.Equal("pwd,otp", first.Entry!.AuthMethods);
        Assert.Equal("n", first.Entry.Nonce);
        Assert.Equal(Now, first.Entry.AuthTime);

        var again = await store.ConsumeAsync("h1", Now);
        Assert.Equal(CodeConsumeStatus.Replayed, again.Status);
        Assert.Equal(first.Entry.FamilyId, again.Entry!.FamilyId);

        Assert.Equal(CodeConsumeStatus.Expired, (await store.ConsumeAsync("h2", Now)).Status);
        Assert.Equal(CodeConsumeStatus.NotFound, (await store.ConsumeAsync("nope", Now)).Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 認可コードを同時に使おうとしても成功は一つだけ(DatabaseProvider provider)
    {
        var store = new AuthorizationCodeStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        await store.CreateAsync(Code("race", Now.AddMinutes(1)));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.ConsumeAsync("race", Now))));

        Assert.Equal(1, results.Count(result => result.Status == CodeConsumeStatus.Consumed));
        Assert.Equal(11, results.Count(result => result.Status == CodeConsumeStatus.Replayed));
    }

    private static RefreshTokenEntry Refresh(string hash, Guid family, Guid session, DateTime expires) => new()
    {
        TokenHash = hash,
        FamilyId = family,
        ClientId = "c",
        SessionId = session,
        TenantId = 1,
        UserId = 7,
        Scope = "openid offline_access",
        AuthTime = Now,
        AuthMethods = "pwd",
        ExpiresAt = expires,
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task リフレッシュトークンの再利用と系統の失効とセッション単位の失効(DatabaseProvider provider)
    {
        var store = new RefreshTokenStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var family = Guid.NewGuid();
        var session = Guid.NewGuid();
        await store.CreateAsync(Refresh("t1", family, session, Now.AddDays(1)), Now);
        await store.CreateAsync(Refresh("t2", family, session, Now.AddDays(1)), Now);
        await store.CreateAsync(Refresh("old", Guid.NewGuid(), session, Now.AddDays(-1)), Now.AddDays(-2));
        await store.CreateAsync(Refresh("other", Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(1)), Now);

        Assert.Equal(RefreshConsumeStatus.Consumed, (await store.ConsumeAsync("t1", Now)).Status);
        Assert.Equal(RefreshConsumeStatus.Replayed, (await store.ConsumeAsync("t1", Now)).Status);
        Assert.Equal(RefreshConsumeStatus.Expired, (await store.ConsumeAsync("old", Now)).Status);
        Assert.Equal(RefreshConsumeStatus.NotFound, (await store.ConsumeAsync("zzz", Now)).Status);

        // 系統ごと失効 → 同じ系統の未使用トークンも使えない
        await store.RevokeFamilyAsync(family, Now);
        Assert.Equal(RefreshConsumeStatus.Revoked, (await store.ConsumeAsync("t2", Now)).Status);

        // セッション単位
        await store.RevokeSessionAsync(session, Now);
        Assert.Equal(RefreshConsumeStatus.Consumed, (await store.ConsumeAsync("other", Now)).Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task リフレッシュトークンを同時に使おうとしても成功は一つだけ(DatabaseProvider provider)
    {
        var store = new RefreshTokenStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        await store.CreateAsync(Refresh("race", Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(1)), Now);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.ConsumeAsync("race", Now))));

        Assert.Equal(1, results.Count(result => result.Status == RefreshConsumeStatus.Consumed));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 失効の要求は自分のクライアントのトークンだけに効く(DatabaseProvider provider)
    {
        var store = new RefreshTokenStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        await store.CreateAsync(Refresh("mine", Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(1)), Now);

        Assert.False(await store.RevokeAsync("mine", "someone-else", Now));
        Assert.True(await store.RevokeAsync("mine", "c", Now));
        Assert.Equal(RefreshConsumeStatus.Revoked, (await store.ConsumeAsync("mine", Now)).Status);
    }

    // ---- ログイン失敗・TOTP ---------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ログイン失敗はしきい値で締め出し窓を過ぎると数え直す(DatabaseProvider provider)
    {
        var store = new LoginSecurityStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var window = TimeSpan.FromMinutes(15);
        var lockout = TimeSpan.FromMinutes(15);

        for (var i = 1; i <= 4; i++)
        {
            var state = await store.RecordFailureAsync("Taro ", Now.AddSeconds(i), 5, window, lockout);
            Assert.Equal(i, state.FailedCount);
            Assert.Null(state.LockedUntil);
        }

        var locked = await store.RecordFailureAsync("taro", Now.AddSeconds(5), 5, window, lockout);
        Assert.Equal(5, locked.FailedCount);
        Assert.Equal(Now.AddSeconds(5) + lockout, locked.LockedUntil);

        // 鍵は大文字小文字・前後の空白を区別しない
        Assert.NotNull(await store.GetLockedUntilAsync("TARO", Now.AddMinutes(1)));
        Assert.Null(await store.GetLockedUntilAsync("TARO", Now.AddMinutes(16)));
        Assert.Null(await store.GetLockedUntilAsync("someone-else", Now.AddMinutes(1)));

        // 窓を過ぎてからの失敗は 1 から数え直す
        var restart = await store.RecordFailureAsync("hanako", Now, 5, window, lockout);
        var later = await store.RecordFailureAsync("hanako", Now.AddMinutes(30), 5, window, lockout);
        Assert.Equal(1, restart.FailedCount);
        Assert.Equal(1, later.FailedCount);

        await store.ResetAsync("taro");
        Assert.Null(await store.GetLockedUntilAsync("taro", Now.AddMinutes(1)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 失敗が同時に来ても取りこぼさず数える(DatabaseProvider provider)
    {
        var store = new LoginSecurityStore(await TestDatabases.FreshIdpDatabaseAsync(provider));

        var states = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            store.RecordFailureAsync("racer", Now, 100, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)))));

        Assert.Equal(16, states.Max(state => state.FailedCount));
        Assert.Equal(16, (await store.RecordFailureAsync("racer", Now, 100, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15))).FailedCount - 1);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TOTPの枠は新しいものだけを受け付ける(DatabaseProvider provider)
    {
        var store = new LoginSecurityStore(await TestDatabases.FreshIdpDatabaseAsync(provider));

        Assert.True(await store.TryUseTotpStepAsync("1:7", 100, Now));
        Assert.False(await store.TryUseTotpStepAsync("1:7", 100, Now));
        Assert.False(await store.TryUseTotpStepAsync("1:7", 99, Now));
        Assert.True(await store.TryUseTotpStepAsync("1:7", 101, Now));
        Assert.True(await store.TryUseTotpStepAsync("1:8", 100, Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TOTPの同じ枠を同時に使おうとしても成功は一つだけ(DatabaseProvider provider)
    {
        var store = new LoginSecurityStore(await TestDatabases.FreshIdpDatabaseAsync(provider));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.TryUseTotpStepAsync("1:9", 555, Now))));

        Assert.Equal(1, results.Count(result => result));
    }

    // ---- 保留中の要求・監査・掃除 ---------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 保留中の要求は一度だけ取り出せ期限切れは取れない(DatabaseProvider provider)
    {
        var store = new PendingRequestStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var id = await store.SaveAsync("saml", "{\"a\":1}", Now.AddMinutes(30));
        var expired = await store.SaveAsync("saml", "{}", Now.AddMinutes(-1));
        var wrongKind = await store.SaveAsync("saml", "{}", Now.AddMinutes(30));

        Assert.Null(await store.TakeAsync(wrongKind, "other", Now));
        Assert.Equal("{\"a\":1}", await store.TakeAsync(id, "saml", Now));
        Assert.Null(await store.TakeAsync(id, "saml", Now));
        Assert.Null(await store.TakeAsync(expired, "saml", Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 保留中の要求を同時に取り出しても受け取れるのは一つだけ(DatabaseProvider provider)
    {
        var store = new PendingRequestStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var id = await store.SaveAsync("saml", "payload", Now.AddMinutes(30));

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.TakeAsync(id, "saml", Now))));

        Assert.Equal(1, results.Count(result => result is not null));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限切れの行だけを掃除する(DatabaseProvider provider)
    {
        var factory = await TestDatabases.FreshIdpDatabaseAsync(provider);
        var retention = TimeSpan.FromDays(2);
        var codes = new AuthorizationCodeStore(factory);
        var tokens = new RefreshTokenStore(factory);
        var sessions = new SsoSessionStore(factory);
        var pending = new PendingRequestStore(factory);
        var security = new LoginSecurityStore(factory);
        var cleaner = new ExpiredDataStore(factory);

        await codes.CreateAsync(Code("old", Now.AddDays(-3)));
        await codes.CreateAsync(Code("new", Now.AddMinutes(1)));
        await tokens.CreateAsync(Refresh("old", Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-3)), Now.AddDays(-30));
        await tokens.CreateAsync(Refresh("new", Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(10)), Now);
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        await sessions.CreateAsync(Session(oldSession, now: Now.AddDays(-10)));
        await sessions.CreateAsync(Session(newSession));
        await sessions.AddParticipantAsync(new SessionParticipant(oldSession, SessionParticipant.Oidc, "c", null, null), Now);
        await sessions.AddParticipantAsync(new SessionParticipant(newSession, SessionParticipant.Oidc, "c", null, null), Now);
        await pending.SaveAsync("saml", "old", Now.AddMinutes(-1));
        var keep = await pending.SaveAsync("saml", "new", Now.AddMinutes(10));
        await security.RecordFailureAsync("stale", Now.AddDays(-10), 5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        await security.RecordFailureAsync("fresh", Now, 5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

        var deleted = await cleaner.DeleteExpiredAsync(Now, retention);

        Assert.Equal(1, deleted["AuthorizationCodes"]);
        Assert.Equal(1, deleted["RefreshTokens"]);
        Assert.Equal(1, deleted["SsoSessions"]);
        Assert.Equal(1, deleted["SessionParticipants"]);
        Assert.Equal(1, deleted["PendingRequests"]);
        Assert.Equal(1, deleted["LoginFailures"]);
        Assert.Equal(CodeConsumeStatus.Consumed, (await codes.ConsumeAsync("new", Now)).Status);
        Assert.NotNull(await sessions.FindActiveAsync(newSession, Now, TimeSpan.FromHours(2)));
        Assert.NotNull(await pending.TakeAsync(keep, "saml", Now));
    }

    // ---- 時刻 -------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 時刻はUTCのまま往復しサーバの時間帯に左右されない(DatabaseProvider provider)
    {
        // PostgreSQL（Npgsql が Kind を見て時間帯変換する）と MySQL（秒未満を丸める）で実際に踏む事故
        var store = new SsoSessionStore(await TestDatabases.FreshIdpDatabaseAsync(provider));
        var id = Guid.NewGuid();
        var utcNow = DateTime.UtcNow;
        await store.CreateAsync(Session(id, now: utcNow));

        var read = (await store.FindActiveAsync(id, utcNow.AddSeconds(1), TimeSpan.FromHours(1)))!;

        Assert.Equal(DbTime.Truncate(utcNow), read.AuthTime);
        Assert.Equal(DateTimeKind.Utc, read.AuthTime.Kind);
        Assert.True(read.AuthTime <= utcNow);
    }
}
