using System.Net;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToolbox.Web.Tests.Services;

/// <summary>
/// Directive §5.4's manifest validation rules — the ones with explicit test requirements in §8:
/// an unprefixed name is refused, and a mutation-kind capability is never registered.
/// </summary>
public sealed class UiCapabilityProviderTests
{
    private static readonly McpClientCredential Credential = new()
    {
        Id = 1,
        Surface = "picsearch",
        ClientId = "test-client",
        TokenEndpointUrl = "https://api.p21toolbox.com/Token",
        McpEndpointUrl = "https://api.p21toolbox.com/api/mcp",
        IsEnabled = true
    };

    [Fact]
    public async Task ValidNavigationCapability_IsRegistered()
    {
        var manifest = """
            [
              {
                "name": "ui_show_model_search",
                "description": "Shows the model search screen. Returns no product data — use find_spec or get_product_specs for that.",
                "kind": "navigation",
                "inputSchema": { "type": "object", "properties": { "modelNumber": { "type": "string" } } }
              }
            ]
            """;

        var tools = await GetToolsAsync(manifest);

        var tool = Assert.Single(tools);
        Assert.Equal("ui_show_model_search", tool.Name);
    }

    [Fact]
    public async Task UnprefixedName_IsRefused()
    {
        var manifest = """
            [
              {
                "name": "show_model_search",
                "description": "Shows the model search screen. Returns no product data.",
                "kind": "navigation",
                "inputSchema": { "type": "object", "properties": {} }
              }
            ]
            """;

        var tools = await GetToolsAsync(manifest);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task MutationKind_IsNeverRegistered()
    {
        var manifest = """
            [
              {
                "name": "ui_update_order_line",
                "description": "Updates an order line. Returns no product data.",
                "kind": "mutation",
                "inputSchema": { "type": "object", "properties": {} }
              }
            ]
            """;

        var tools = await GetToolsAsync(manifest);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task MissingDescription_IsRegisteredAnyway()
    {
        var manifest = """
            [
              {
                "name": "ui_show_model_search",
                "kind": "navigation",
                "inputSchema": { "type": "object", "properties": {} }
              }
            ]
            """;

        var tools = await GetToolsAsync(manifest);

        Assert.Single(tools);
    }

    [Fact]
    public async Task MixedManifest_OnlyValidNavigationEntriesSurvive()
    {
        var manifest = """
            [
              { "name": "ui_show_model_search", "description": "Returns no product data.", "kind": "navigation", "inputSchema": {} },
              { "name": "show_unprefixed", "description": "Returns no product data.", "kind": "navigation", "inputSchema": {} },
              { "name": "ui_delete_order", "description": "Returns no product data.", "kind": "mutation", "inputSchema": {} }
            ]
            """;

        var tools = await GetToolsAsync(manifest);

        var tool = Assert.Single(tools);
        Assert.Equal("ui_show_model_search", tool.Name);
    }

    [Fact]
    public async Task FetchFailure_DegradesToEmptyWithoutThrowing()
    {
        var provider = CreateProvider(StubHttpClientFactory.ReturningJson(HttpStatusCode.InternalServerError, "oops"));

        var tools = await provider.GetToolsAsync("picsearch", ["picsearch"]);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task NoCredentialConfigured_DegradesToEmptyWithoutThrowing()
    {
        var provider = new UiCapabilityProvider(
            new FakeMcpClientCredentialRepository(null),
            new FakeMcpAccessTokenProvider(),
            StubHttpClientFactory.ReturningJson(HttpStatusCode.OK, "[]"),
            NullLogger<UiCapabilityProvider>.Instance);

        var tools = await provider.GetToolsAsync("picsearch", ["picsearch"]);

        Assert.Empty(tools);
    }

    private static async Task<IReadOnlyList<Microsoft.Extensions.AI.AITool>> GetToolsAsync(string manifestJson)
    {
        var provider = CreateProvider(StubHttpClientFactory.ReturningJson(HttpStatusCode.OK, manifestJson));
        return await provider.GetToolsAsync("picsearch", ["picsearch"]);
    }

    private static UiCapabilityProvider CreateProvider(StubHttpClientFactory httpClientFactory)
    {
        return new UiCapabilityProvider(
            new FakeMcpClientCredentialRepository(Credential),
            new FakeMcpAccessTokenProvider(),
            httpClientFactory,
            NullLogger<UiCapabilityProvider>.Instance);
    }
}
