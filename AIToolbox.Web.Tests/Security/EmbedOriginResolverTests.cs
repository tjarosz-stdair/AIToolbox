using AIToolbox.Web.Configuration;
using AIToolbox.Web.Security;

namespace AIToolbox.Web.Tests.Security;

public sealed class EmbedOriginResolverTests
{
    [Fact]
    public void ExactMatch_ReturnsCanonicalConfiguredValue()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("https://app.p21toolbox.com");

        Assert.Equal("https://app.p21toolbox.com", result);
    }

    [Fact]
    public void TrailingSlash_StillMatches()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("https://app.p21toolbox.com/");

        Assert.Equal("https://app.p21toolbox.com", result);
    }

    [Fact]
    public void CaseDifference_StillMatches()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("HTTPS://APP.P21TOOLBOX.COM");

        Assert.Equal("https://app.p21toolbox.com", result);
    }

    [Fact]
    public void ExplicitDefaultPort_StillMatchesImplicitPort()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("https://app.p21toolbox.com:443");

        Assert.Equal("https://app.p21toolbox.com", result);
    }

    [Fact]
    public void DifferentPort_DoesNotMatch()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("https://app.p21toolbox.com:8443");

        Assert.Null(result);
    }

    [Fact]
    public void ChromeExtensionScheme_DoesNotMatchAnHttpsAllowlist()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("chrome-extension://abcdefghijklmnop");

        Assert.Null(result);
    }

    [Fact]
    public void ChromeExtensionOrigin_MatchesWhenConfigured()
    {
        // The Chrome-extension-broker case (side panel hosting /embed/chat): Uri.TryCreate must
        // still populate Host/Port for this non-http(s) scheme via .NET's generic URI syntax
        // fallback for Normalize() to work at all here -- unverified until now.
        var resolver = CreateResolver(["chrome-extension://lemdkjldlbphmfjbgbeolbpkmkeclenc"]);

        var result = resolver.Resolve("chrome-extension://lemdkjldlbphmfjbgbeolbpkmkeclenc");

        Assert.Equal("chrome-extension://lemdkjldlbphmfjbgbeolbpkmkeclenc", result);
    }

    [Fact]
    public void EmptyAllowlist_NeverMatchesEvenAPlausibleOrigin()
    {
        var resolver = CreateResolver([]);

        var result = resolver.Resolve("https://app.p21toolbox.com");

        Assert.Null(result);
    }

    [Fact]
    public void PathBearingCandidate_IsRejected()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("https://app.p21toolbox.com/some/path");

        Assert.Null(result);
    }

    [Fact]
    public void QueryBearingCandidate_IsRejected()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("https://app.p21toolbox.com/?inject=1");

        Assert.Null(result);
    }

    [Fact]
    public void MissingCandidate_ResolvesToTheSingleConfiguredOrigin()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve(null);

        Assert.Equal("https://app.p21toolbox.com", result);
    }

    [Fact]
    public void MissingCandidate_DoesNotResolveWhenMultipleOriginsAreConfigured()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com", "https://stdair.ai.p21toolbox.com"]);

        var result = resolver.Resolve(null);

        Assert.Null(result);
    }

    [Fact]
    public void UnrelatedOrigin_DoesNotMatch()
    {
        var resolver = CreateResolver(["https://app.p21toolbox.com"]);

        var result = resolver.Resolve("https://evil.example.com");

        Assert.Null(result);
    }

    private static EmbedOriginResolver CreateResolver(List<string> parentOrigins)
    {
        var options = new EmbedOptions { ParentOrigins = parentOrigins };
        return new EmbedOriginResolver(new StaticOptionsMonitor<EmbedOptions>(options));
    }
}
