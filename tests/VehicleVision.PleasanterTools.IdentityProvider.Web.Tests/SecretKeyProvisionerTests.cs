using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Tests;

public sealed class SecretKeyProvisionerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "idp-secret-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IConfiguration Config(string? key = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(key is null ? [] : [new KeyValuePair<string, string?>(SecretKeyProvisioner.Setting, key)])
            .Build();

    [Fact]
    public void 与えられていればそのまま使い_何も書かない()
    {
        var given = SecretProtector.GenerateKey();

        var key = SecretKeyProvisioner.Resolve(Config(given), directory, out var generatedAt);

        Assert.Equal(given, key);
        Assert.Null(generatedAt);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void 無ければ作ってローカルのファイルへ残し_次の起動で読める()
    {
        var key = SecretKeyProvisioner.Resolve(Config(), directory, out var generatedAt);

        Assert.NotNull(generatedAt);
        Assert.Equal(32, SecretProtector.DecodeKey(key).Length);
        Assert.Equal(key, (string?)JsonNode.Parse(File.ReadAllText(generatedAt))!["SecretKey"]);

        // 次の起動: 設定ファイルから読める（Service.local.json）
        var next = new ConfigurationBuilder().AddParameterFiles(directory).Build();
        Assert.Equal(key, next[SecretKeyProvisioner.Setting]);
        Assert.Equal(key, SecretKeyProvisioner.Resolve(next, directory, out var again));
        Assert.Null(again);
    }

    [Fact]
    public void 同じファイルの他の設定は残る()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SecretKeyProvisioner.FileName), """{ "Issuer": "https://idp.example.com" }""");

        SecretKeyProvisioner.Resolve(Config(), directory, out var generatedAt);

        var json = JsonNode.Parse(File.ReadAllText(generatedAt!))!;
        Assert.Equal("https://idp.example.com", (string?)json["Issuer"]);
        Assert.NotNull(json["SecretKey"]);
    }

    [Fact]
    public async Task 捨てると鍵だけが消える()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SecretKeyProvisioner.FileName), """{ "Issuer": "https://idp.example.com" }""");
        SecretKeyProvisioner.Resolve(Config(), directory, out var generatedAt);

        await SecretKeyProvisioner.DiscardAsync(generatedAt!);

        var json = JsonNode.Parse(File.ReadAllText(generatedAt!))!;
        Assert.Null(json["SecretKey"]);
        Assert.Equal("https://idp.example.com", (string?)json["Issuer"]);
    }

    [Fact]
    public void 書けなければ分かる言葉で止める()
    {
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "blocker");
        File.WriteAllText(blocker, "x");

        // 置き場がファイルの下（作れない）
        var error = Assert.Throws<InvalidOperationException>(
            () => SecretKeyProvisioner.Resolve(Config(), Path.Combine(blocker, "Parameters"), out _));

        Assert.Contains("IDP_SECRET_KEY", error.Message, StringComparison.Ordinal);
    }
}
