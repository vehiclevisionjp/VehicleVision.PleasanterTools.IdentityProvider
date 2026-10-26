using System.Net;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

public class IdpProcessReadinessTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/idp")]
    public async Task OIDCだけ準備できてもSAMLの署名証明書が出るまでは待つ(string pathBase)
    {
        var samlReady = false;
        using var handler = new ReadinessHandler(pathBase, path => path switch
        {
            "/ready" => Response(HttpStatusCode.OK, """{"status":"ready"}"""),
            "/oidc/jwks" => Response(HttpStatusCode.OK, """{"keys":[{"kid":"oidc-key"}]}"""),
            "/saml/metadata" => samlReady
                ? Response(HttpStatusCode.OK, Metadata)
                : Response(HttpStatusCode.InternalServerError, string.Empty),
            _ => throw new InvalidOperationException("想定外の経路: " + path),
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        Assert.False(await IdpProcess.IsReadyAsync(client, pathBase));

        samlReady = true;
        Assert.True(await IdpProcess.IsReadyAsync(client, pathBase));
    }

    [Theory]
    [InlineData("/ready", HttpStatusCode.ServiceUnavailable, "")]
    [InlineData("/oidc/jwks", HttpStatusCode.InternalServerError, "")]
    [InlineData("/oidc/jwks", HttpStatusCode.OK, """{"keys":[]}""")]
    [InlineData("/saml/metadata", HttpStatusCode.OK, "<EntityDescriptor />")]
    [InlineData("/saml/metadata", HttpStatusCode.OK,
        """<EntityDescriptor xmlns:ds="http://www.w3.org/2000/09/xmldsig#"><ds:X509Certificate /></EntityDescriptor>""")]
    public async Task DBと両用途の公開鍵が揃わなければ準備完了にしない(
        string unavailablePath, HttpStatusCode status, string body)
    {
        using var handler = new ReadinessHandler(string.Empty, path =>
            path == unavailablePath ? Response(status, body) : path switch
            {
                "/ready" => Response(HttpStatusCode.OK, """{"status":"ready"}"""),
                "/oidc/jwks" => Response(HttpStatusCode.OK, """{"keys":[{"kid":"oidc-key"}]}"""),
                "/saml/metadata" => Response(HttpStatusCode.OK, Metadata),
                _ => throw new InvalidOperationException("想定外の経路: " + path),
            });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        Assert.False(await IdpProcess.IsReadyAsync(client, string.Empty));
    }

    private const string Metadata = """
        <EntityDescriptor xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
            <ds:X509Certificate>certificate</ds:X509Certificate>
        </EntityDescriptor>
        """;

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class ReadinessHandler(string pathBase, Func<string, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path != "/ready")
            {
                Assert.StartsWith(pathBase + "/", path, StringComparison.Ordinal);
                path = path[pathBase.Length..];
            }

            return Task.FromResult(respond(path));
        }
    }
}
