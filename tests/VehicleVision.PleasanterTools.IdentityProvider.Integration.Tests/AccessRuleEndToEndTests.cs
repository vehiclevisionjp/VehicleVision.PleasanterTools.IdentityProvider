using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>ユーザー・組織・グループの Include / Exclude を、実際の IdP の画面と API で確かめる。</summary>
public class AccessRuleEndToEndTests(IdpProcess idp) : IClassFixture<IdpProcess>
{
    private static int nextId = 7000;

    private static int NewId() => Interlocked.Increment(ref nextId);

    private async Task<(string LoginId, int UserId, int GroupId, int DeptId)> UserInGroupAsync()
    {
        var groupId = NewId();
        var deptId = NewId();
        await idp.Pleasanter.AddDeptAsync(1, deptId, "部署" + deptId);
        await idp.Pleasanter.AddGroupAsync(1, groupId, "グループ" + groupId);
        var loginId = E2E.Unique("rule");
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, deptId: deptId, mail: loginId + "@example.com");
        await idp.Pleasanter.AddGroupMemberAsync(groupId, userId: userId);
        return (loginId, userId, groupId, deptId);
    }

    private async Task<bool> OidcIssuesAsync(string clientId, string loginId)
    {
        using var browser = idp.NewBrowser();
        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, E2E.NewPkce().Challenge), loginId);
        var query = QueryHelpers.ParseQuery(redirect.Query);
        if (query.ContainsKey("error"))
        {
            Assert.Equal("access_denied", query["error"].ToString());
            return false;
        }

        return query.ContainsKey("code");
    }

    private static async Task<JsonNode> JsonOfAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task グループのIncludeに当たる人だけにOIDCを発行する()
    {
        var member = await UserInGroupAsync();
        var outsider = await UserInGroupAsync();
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin, accessRules: [E2E.Rule("include", "group", member.GroupId)]);

        Assert.True(await OidcIssuesAsync(clientId, member.LoginId));
        Assert.False(await OidcIssuesAsync(clientId, outsider.LoginId));
    }

    [Fact]
    public async Task 組織とユーザーのExcludeはIncludeより強い()
    {
        var blockedByDept = await UserInGroupAsync();
        var blockedByUser = await UserInGroupAsync();
        var free = await UserInGroupAsync();
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin, accessRules:
        [
            E2E.Rule("exclude", "dept", blockedByDept.DeptId),
            E2E.Rule("exclude", "user", blockedByUser.UserId),
        ]);

        Assert.False(await OidcIssuesAsync(clientId, blockedByDept.LoginId));
        Assert.False(await OidcIssuesAsync(clientId, blockedByUser.LoginId));
        Assert.True(await OidcIssuesAsync(clientId, free.LoginId));

        // Include に入れても、Exclude が勝つ
        var (strict, _) = await E2E.CreateClientAsync(admin, accessRules:
        [
            E2E.Rule("include", "group", blockedByDept.GroupId),
            E2E.Rule("exclude", "dept", blockedByDept.DeptId),
        ]);
        Assert.False(await OidcIssuesAsync(strict, blockedByDept.LoginId));
    }

    [Fact]
    public async Task 組織がメンバーのグループにも当たる()
    {
        var groupId = NewId();
        var deptId = NewId();
        await idp.Pleasanter.AddDeptAsync(1, deptId, "営業");
        await idp.Pleasanter.AddGroupAsync(1, groupId, "営業グループ");
        await idp.Pleasanter.AddGroupMemberAsync(groupId, deptId: deptId);
        var loginId = E2E.Unique("deptmember");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, deptId: deptId);
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin, accessRules: [E2E.Rule("include", "group", groupId)]);

        Assert.True(await OidcIssuesAsync(clientId, loginId));
    }

    [Fact]
    public async Task 発行済みのリフレッシュトークンもルールの変更に従う()
    {
        var member = await UserInGroupAsync();
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();
        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, "openid offline_access"), member.LoginId);
        using var exchange = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), verifier);
        var refreshToken = (await E2E.ReadAsync(exchange))["refresh_token"]!.GetValue<string>();

        // 後からルールを足して、この人を締め出す
        using var update = await E2E.AdminSendAsync(admin, HttpMethod.Put, "/api/admin/oidc-clients/" + clientId, new
        {
            displayName = "テストアプリ",
            clientType = "confidential",
            redirectUris = new[] { E2E.RedirectUri },
            allowedScopes = new[] { "openid", "offline_access" },
            accessRules = new[] { E2E.Rule("exclude", "user", member.UserId) },
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var refresh = await browser.PostFormAsync("/oidc/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = secret!,
        });
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
    }

    [Fact]
    public async Task SAMLもルールに従う()
    {
        var member = await UserInGroupAsync();
        var outsider = await UserInGroupAsync();
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, accessRules: [E2E.Rule("include", "group", member.GroupId)]);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        using var allowed = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, member.LoginId);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("SAMLResponse", await allowed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var otherBrowser = idp.NewBrowser();
        using var denied = await SamlSp.SsoAsync(otherBrowser, sp.RedirectAuthnRequest(metadata).PathAndQuery, outsider.LoginId);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("SAMLResponse", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 管理APIでルールを保存して読み戻せ_不正な値は断る()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin, accessRules:
        [
            E2E.Rule("Include", " Group ", 5),
            E2E.Rule("include", "group", 5),
            E2E.Rule("exclude", "user", 8),
        ]);

        using var read = await admin.GetAsync("/api/admin/oidc-clients/" + clientId);
        var rules = (await JsonOfAsync(read))["accessRules"]!.AsArray();
        Assert.Equal(2, rules.Count);
        Assert.Equal("include", rules[0]!["effect"]!.GetValue<string>());
        Assert.Equal("group", rules[0]!["kind"]!.GetValue<string>());

        foreach (var bad in new[] { E2E.Rule("allow", "user", 1), E2E.Rule("include", "tenant", 1), E2E.Rule("include", "user", 0) })
        {
            using var response = await E2E.AdminPostAsync(admin, "/api/admin/oidc-clients", new
            {
                displayName = "不正",
                clientType = "confidential",
                redirectUris = new[] { E2E.RedirectUri },
                allowedScopes = new[] { "openid" },
                accessRules = new[] { bad },
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task 対象を名前で探せる()
    {
        var groupId = NewId();
        var tag = "検索" + groupId;
        await idp.Pleasanter.AddGroupAsync(1, groupId, tag + "グループ");
        await idp.Pleasanter.AddGroupAsync(1, NewId(), tag + "無効", disabled: true);
        await idp.Pleasanter.AddGroupAsync(2, NewId(), tag + "他テナント");
        var loginId = E2E.Unique("findme");
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var admin = await E2E.AdminAsync(idp);

        using var groupResponse = await admin.GetAsync("/api/admin/pleasanter/directory?kind=group&q=" + Uri.EscapeDataString(tag));
        var groups = (await JsonOfAsync(groupResponse)).AsArray();
        Assert.Single(groups);
        Assert.Equal(groupId, groups[0]!["id"]!.GetValue<int>());

        using var userResponse = await admin.GetAsync("/api/admin/pleasanter/directory?kind=user&q=" + loginId.ToUpperInvariant());
        var users = (await JsonOfAsync(userResponse)).AsArray();
        Assert.Contains(users, user => user!["id"]!.GetValue<int>() == userId);

        using var bad = await admin.GetAsync("/api/admin/pleasanter/directory?kind=tenant&q=x");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

/// <summary>管理画面へ入れる人への Include / Exclude（<c>IDP_ADMIN_INCLUDE</c> / <c>IDP_ADMIN_EXCLUDE</c>）。</summary>
public class AdminAccessRuleEndToEndTests(AdminAccessRuleProcess idp) : IClassFixture<AdminAccessRuleProcess>
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool groupCreated;

    private async Task<HttpStatusCode> EnterAsync(bool inGroup, int deptId = 0, bool tenantManager = true)
    {
        await EnsureGroupAsync();
        if (deptId > 0)
        {
            await EnsureDeptAsync(deptId);
        }

        var loginId = E2E.Unique("adminrule");
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, tenantManager: tenantManager, deptId: deptId);
        if (inGroup)
        {
            await idp.Pleasanter.AddGroupMemberAsync(AdminAccessRuleProcess.AllowedGroup, userId: userId);
        }

        using var browser = idp.NewBrowser();
        await browser.LoginAsync(loginId, E2E.Password);
        using var response = await browser.GetAsync("/api/admin/me");
        return response.StatusCode;
    }

    [Fact]
    public async Task テナント管理者でもIncludeのグループに入っていなければ入れない() =>
        Assert.Equal(HttpStatusCode.Forbidden, await EnterAsync(inGroup: false));

    [Fact]
    public async Task Includeのグループに入っているテナント管理者は入れる() =>
        Assert.Equal(HttpStatusCode.OK, await EnterAsync(inGroup: true));

    [Fact]
    public async Task Excludeの組織に属する人はグループに入っていても入れない() =>
        Assert.Equal(HttpStatusCode.Forbidden, await EnterAsync(inGroup: true, deptId: AdminAccessRuleProcess.BlockedDept));

    [Fact]
    public async Task グループに入っていてもテナント管理者でなければ入れない() =>
        Assert.Equal(HttpStatusCode.Forbidden, await EnterAsync(inGroup: true, tenantManager: false));

    private async Task EnsureGroupAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!groupCreated)
            {
                await idp.Pleasanter.AddGroupAsync(1, AdminAccessRuleProcess.AllowedGroup, "管理グループ");
                await idp.Pleasanter.AddDeptAsync(1, AdminAccessRuleProcess.BlockedDept, "締め出す部署");
                groupCreated = true;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Task EnsureDeptAsync(int deptId) =>
        deptId == AdminAccessRuleProcess.BlockedDept ? Task.CompletedTask : throw new ArgumentOutOfRangeException(nameof(deptId));
}
