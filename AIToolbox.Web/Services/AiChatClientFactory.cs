using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Services;

public sealed class AiChatClientFactory : IAiChatClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ILoggerFactory _loggerFactory;

    public AiChatClientFactory(
        IHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _secretProtector = secretProtector;
        _loggerFactory = loggerFactory;
    }

    public IChatClient Create(AiProvider provider)
    {
        var baseClient = new AiChatClient(
            provider,
            _httpClientFactory,
            _secretProtector,
            _loggerFactory.CreateLogger<AiChatClient>());

        return new ChatClientBuilder(baseClient)
            .UseFunctionInvocation(_loggerFactory, configure: functionInvokingClient =>
            {
                // Explicit per Part 4.5.1: the model's own loop guard, separate from P21
                // Toolbox's server-side call budget (Part 5.1) which this has no visibility into.
                functionInvokingClient.MaximumIterationsPerRequest = 10;
                functionInvokingClient.AllowConcurrentInvocation = true;

                // Part 5.1's independent guard: a tool call that keeps failing (e.g. P21
                // Toolbox's own per-conversation budget-exhaustion JSON-RPC error, code -32000)
                // must not let the agent spin retrying it forever within one turn.
                functionInvokingClient.MaximumConsecutiveErrorsPerRequest = 3;
            })
            .Build();
    }
}
