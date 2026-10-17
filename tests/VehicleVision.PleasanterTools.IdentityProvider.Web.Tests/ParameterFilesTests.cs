using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Web;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Tests;

/// <summary>設定ファイルの読み方（話題ごとのファイル・階層・配列・.local.json の優先）。</summary>
public sealed class ParameterFilesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "idp-params-" + Guid.NewGuid().ToString("N"));

    public ParameterFilesTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(directory, name), json);

    private IdpOptions Load() =>
        IdpOptions.FromConfiguration(new ConfigurationBuilder().AddParameterFiles(directory).Build());

    [Fact]
    public void 話題ごとのファイルの値を読む()
    {
        Write("Session.json", """{ "//LifetimeMinutes": "説明", "LifetimeMinutes": 60, "IdleMinutes": 30 }""");
        Write("Oidc.json", """{ "AccessTokenLifetimeSeconds": 900 }""");
        Write("Saml.json", """{ "AssertionLifetimeSeconds": 120 }""");
        Write("Security.json", """{ "LoginMaxFailures": 3, "SecondaryAuthentication": { "Mode": "DefaultEnable", "CountTolerances": 2 } }""");

        var options = Load();

        Assert.Equal(TimeSpan.FromMinutes(60), options.SessionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(30), options.SessionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(900), options.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromSeconds(120), options.SamlAssertionLifetime);
        Assert.Equal(3, options.LoginMaxFailures);
        Assert.Equal(2, options.Totp.CountTolerances);
    }

    [Fact]
    public void 管理画面のIncludeとExcludeを階層と配列で読む()
    {
        Write("Admin.json", """
            {
              "Role": "ServiceManager",
              "LoginIds": ["Root", "Ops"],
              "Include": { "Users": [12], "Depts": [], "Groups": [5, 6] },
              "Exclude": { "Depts": [3] }
            }
            """);

        var options = Load();

        Assert.Equal(AdminRole.ServiceManager, options.AdminRole);
        Assert.Equal(["root", "ops"], options.AdminLoginIds);
        Assert.Equal(
            [
                new AccessRule("include", "user", 12),
                new AccessRule("include", "group", 5),
                new AccessRule("include", "group", 6),
                new AccessRule("exclude", "dept", 3),
            ],
            options.AdminAccessRules);
    }

    [Fact]
    public void ローカルのファイルが基のファイルを上書きし_階層の別の項目は残る()
    {
        Write("Admin.json", """{ "Role": "TenantManager", "Exclude": { "Users": [1], "Depts": [2] } }""");
        Write("Admin.local.json", """{ "Role": "ServiceManager", "Exclude": { "Users": [9] } }""");

        var options = Load();

        Assert.Equal(AdminRole.ServiceManager, options.AdminRole);
        Assert.Equal(
            [new AccessRule("exclude", "user", 9), new AccessRule("exclude", "dept", 2)],
            options.AdminAccessRules);
    }

    [Fact]
    public void 数字でないIDは起動で止める()
    {
        Write("Admin.json", """{ "Include": { "Groups": ["abc"] } }""");

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void ファイルが無くても既定で動く()
    {
        var options = Load();

        Assert.Equal(AdminRole.TenantManager, options.AdminRole);
        Assert.Empty(options.AdminAccessRules);
    }

    private IConfiguration LoadConfiguration() => new ConfigurationBuilder().AddParameterFiles(directory).Build();

    [Fact]
    public void 環境変数と同じ設定をファイルでも指定できる()
    {
        Write("Security.json", """{ "SecretKey": "k", "SecretKeyAutoGenerate": false }""");
        Write("Service.json", """
            {
              "AllowInsecure": true, "PathBase": "/idp",
              "ForwardedNetworks": ["10.0.0.0/8", "192.168.0.0/16"], "HealthNetworks": []
            }
            """);
        Write("Rds.json", """
            {
              "Pleasanter": { "Dbms": "PostgreSql", "ConnectionString": "Host=a", "AllowInsecure": true },
              "Idp": { "Dbms": "Sqlite", "ConnectionString": "Data Source=x.db", "AutoMigrate": false,
                       "MigrationLockTimeoutSeconds": 60, "SkipMigrationCheck": true }
            }
            """);
        Write("Audit.json", """{ "RetentionDays": 30 }""");
        Write("Admin.json", """{ "Path": "/manage" }""");

        var configuration = LoadConfiguration();

        Assert.Equal("k", configuration["IDP_SECRET_KEY"]);
        Assert.Equal("False", configuration["IDP_SECRET_KEY_AUTOGENERATE"]);
        Assert.Equal("True", configuration["IDP_ALLOW_INSECURE"]);
        Assert.Equal("/idp", configuration["IDP_PATH_BASE"]);
        Assert.Equal("10.0.0.0/8,192.168.0.0/16", configuration["IDP_FORWARDED_NETWORKS"]);
        Assert.Null(configuration["IDP_HEALTH_NETWORKS"]);
        Assert.Equal("PostgreSql", configuration["IDP_PLEASANTER_DB_PROVIDER"]);
        Assert.Equal("Host=a", configuration["IDP_PLEASANTER_DB_CONNECTIONSTRING"]);
        Assert.Equal("True", configuration["IDP_PLEASANTER_DB_ALLOW_INSECURE"]);
        Assert.Equal("Sqlite", configuration["IDP_DB_PROVIDER"]);
        Assert.Equal("Data Source=x.db", configuration["IDP_DB_CONNECTIONSTRING"]);
        Assert.Equal("False", configuration["IDP_DB_AUTO_MIGRATE"]);
        Assert.Equal("60", configuration["IDP_DB_MIGRATION_LOCK_TIMEOUT_SECONDS"]);
        Assert.Equal("True", configuration["IDP_DB_SKIP_MIGRATION_CHECK"]);
        Assert.Equal("30", configuration["IDP_AUDIT_RETENTION_DAYS"]);
        Assert.Equal("/manage", configuration["IDP_ADMIN_PATH"]);
    }

    [Fact]
    public void ローカルのファイルの接続文字列が基のファイルより強い()
    {
        Write("Rds.json", """{ "Idp": { "ConnectionString": "base" } }""");
        Write("Rds.local.json", """{ "Idp": { "ConnectionString": "local" } }""");

        Assert.Equal("local", LoadConfiguration()["IDP_DB_CONNECTIONSTRING"]);
    }

    [Fact]
    public void 同梱の既定のファイルは既定の動作と食い違わない()
    {
        var shipped = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "App_Data", "Parameters"));
        var configuration = new ConfigurationBuilder().AddParameterFiles(shipped).Build();

        Assert.Equal("True", configuration["IDP_DB_AUTO_MIGRATE"]);
        Assert.Equal("/admin", configuration["IDP_ADMIN_PATH"]);
        Assert.Equal("365", configuration["IDP_AUDIT_RETENTION_DAYS"]);
        Assert.Equal("300", configuration["IDP_DB_MIGRATION_LOCK_TIMEOUT_SECONDS"]);
        Assert.Equal("TenantManager", configuration["IDP_ADMIN_ROLE"]);
    }

    private string SecretFile(string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "secret-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    private static Dictionary<string, string?> Env(params (string Name, string Value)[] values) =>
        values.ToDictionary(pair => pair.Name, pair => (string?)pair.Value, StringComparer.Ordinal);

    [Fact]
    public void FILEの環境変数はファイルの中身を値にし_末尾の改行だけ落とす()
    {
        var values = ParameterFiles.ValuesFromFiles(Env(
            ("IDP_SECRET_KEY_FILE", SecretFile("abc==\n")),
            ("IDP_DB_CONNECTIONSTRING_FILE", SecretFile(" Server=a;PWD=p w \r\n")),
            ("OTHER_FILE", SecretFile("ignored"))));

        Assert.Equal("abc==", values["IDP_SECRET_KEY"]);
        Assert.Equal(" Server=a;PWD=p w ", values["IDP_DB_CONNECTIONSTRING"]);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void FILEが空欄や名前だけなら無視する()
    {
        Assert.Empty(ParameterFiles.ValuesFromFiles(Env(("IDP_X_FILE", ""), ("IDP__FILE", "x"), ("IDP_FILE", "x"), ("OTHER_X_FILE", "x"))));
    }

    [Fact]
    public void 同じ設定を直接とFILEの両方で与えたら止める()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ParameterFiles.ValuesFromFiles(Env(
            ("IDP_SECRET_KEY", "direct"), ("IDP_SECRET_KEY_FILE", SecretFile("file")))));

        Assert.Contains("IDP_SECRET_KEY", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("direct", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ファイルが無い_読めない_空なら止め_値は出さない()
    {
        Assert.Throws<InvalidOperationException>(() => ParameterFiles.ValuesFromFiles(Env(
            ("IDP_SECRET_KEY_FILE", Path.Combine(directory, "missing")))));
        Assert.Throws<InvalidOperationException>(() => ParameterFiles.ValuesFromFiles(Env(
            ("IDP_SECRET_KEY_FILE", SecretFile("\n")))));
        Directory.CreateDirectory(directory);
        Assert.Throws<InvalidOperationException>(() => ParameterFiles.ValuesFromFiles(Env(
            ("IDP_SECRET_KEY_FILE", directory))));
    }

    [Theory]
    [InlineData("v"+NL+"", "v")]
    [InlineData("v"+CRLF, "v")]
    [InlineData("v"+NL+NL, "v"+NL)]
    [InlineData("a"+NL+"b"+NL, "a"+NL+"b")]
    public void 末尾の改行は1つだけ落とす_複数行はそのまま(string content, string expected) =>
        Assert.Equal(expected, ParameterFiles.ValuesFromFiles(Env(("IDP_X_FILE", SecretFile(content))))["IDP_X"]);

    private const string NL = "\n";
    private const string CRLF = "\r\n";

    [Fact]
    public void 環境変数の名前は大文字小文字を区別しない()
    {
        var values = ParameterFiles.ValuesFromFiles(Env(("idp_x_file", SecretFile("v"))));

        Assert.Equal("v", values["IDP_X"]);
    }

    [Fact]
    public void 相対パスと大きすぎるファイルは止める()
    {
        Assert.Throws<InvalidOperationException>(() => ParameterFiles.ValuesFromFiles(Env(("IDP_X_FILE", "relative/path"))));
        var big = SecretFile(new string('a', ParameterFiles.MaximumFileSizeBytes + 1));
        Assert.Throws<InvalidOperationException>(() => ParameterFiles.ValuesFromFiles(Env(("IDP_X_FILE", big))));
        Assert.Equal(ParameterFiles.MaximumFileSizeBytes, ParameterFiles.ValuesFromFiles(
            Env(("IDP_X_FILE", SecretFile(new string('a', ParameterFiles.MaximumFileSizeBytes)))))["IDP_X"]!.Length);
    }

    [Fact]
    public void 証明書のような複数行も読める()
    {
        var pem = "-----BEGIN CERTIFICATE-----"+NL+"AAA"+NL+"-----END CERTIFICATE-----"+NL;

        Assert.Equal(pem.TrimEnd((char)10), ParameterFiles.ValuesFromFiles(Env(("IDP_X_FILE", SecretFile(pem))))["IDP_X"]);
    }

    [Fact]
    public void 実際の積み方では環境変数と同じ位置でlocal_jsonより弱い()
    {
        const string name = "IDP_ISSUER";
        Write("Service.json", """{ "Issuer": "https://file.test" }""");
        Environment.SetEnvironmentVariable(name + "_FILE", SecretFile("https://secret.test"));
        try
        {
            Assert.Equal("https://secret.test", new ConfigurationBuilder().AddParameterFiles(directory).Build()[name]);

            Write("Service.local.json", """{ "Issuer": "https://local.test" }""");
            Assert.Equal("https://local.test", new ConfigurationBuilder().AddParameterFiles(directory).Build()[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name + "_FILE", null);
        }
    }
}
