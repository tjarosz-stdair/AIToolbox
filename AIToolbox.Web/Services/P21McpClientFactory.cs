using System.Text.Json.Nodes;
using AIToolbox.Web.Data;
using ModelContextProtocol.Client;

namespace AIToolbox.Web.Services;

public sealed class P21McpClientFactory : IP21McpClientFactory
{
    private readonly IMcpClientCredentialRepository _credentials;
    private readonly IMcpAccessTokenProvider _tokenProvider;
    private readonly ILoggerFactory _loggerFactory;

    public P21McpClientFactory(
        IMcpClientCredentialRepository credentials,
        IMcpAccessTokenProvider tokenProvider,
        ILoggerFactory loggerFactory)
    {
        _credentials = credentials;
        _tokenProvider = tokenProvider;
        _loggerFactory = loggerFactory;
    }

    public async Task<McpClient> ConnectAsync(string surface, string? sessionToken, CancellationToken cancellationToken = default)
    {
        var credential = await _credentials.GetBySurfaceAsync(surface, cancellationToken)
            ?? throw new InvalidOperationException($"No enabled MCP client credential is configured for surface '{surface}'.");

        var accessToken = await _tokenProvider.GetAccessTokenAsync(surface, cancellationToken: cancellationToken);

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}"
        };
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            headers["X-P21-Session-Token"] = sessionToken;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(credential.McpEndpointUrl),
            AdditionalHeaders = headers
        });

        return await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<McpClientTool>> GetToolsAsync(
        McpClient client,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var meta = new JsonObject { ["sessionId"] = conversationId };
        return tools.Select(tool => tool.WithMeta(meta)).ToList();
    }
}
