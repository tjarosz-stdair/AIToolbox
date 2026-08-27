using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using Microsoft.Extensions.AI;
using ModelContextProtocol;

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

    private const string UiIntentNamePrefix = "ui_";
    private const int MaxUiIntentsPerTurn = 2;

    public IChatClient Create(AiProvider provider, Func<ToolStepEvent, CancellationToken, Task>? onToolStep = null)
    {
        var baseClient = new AiChatClient(
            provider,
            _httpClientFactory,
            _secretProtector,
            _loggerFactory.CreateLogger<AiChatClient>());

        // A fresh IChatClient (and so a fresh closure below) is built per turn — SubmitTurnAsync
        // calls Create(...) once per SubmitTurnAsync call — so this counter naturally resets
        // per turn with no explicit reset logic needed.
        var uiIntentCallCount = 0;

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

                // Directive §5.6: at most 2 UI-navigation intents per assistant turn, enforced
                // here rather than relying on a broker-side backstop — without a cap the model
                // could thrash the screen mid-answer. MCP data-tool calls are unaffected; only
                // ui_-prefixed local functions are counted and capped.
                functionInvokingClient.FunctionInvoker = async (context, cancellationToken) =>
                {
                    var isUiIntent = context.Function.Name.StartsWith(UiIntentNamePrefix, StringComparison.Ordinal);
                    if (isUiIntent && ++uiIntentCallCount > MaxUiIntentsPerTurn)
                    {
                        return new Dictionary<string, object?>
                        {
                            ["status"] = "rejected",
                            ["reason"] = $"At most {MaxUiIntentsPerTurn} navigation requests are allowed per turn."
                        };
                    }

                    if (onToolStep is not null)
                    {
                        await onToolStep(
                            new ToolStepEvent(context.Function.Name, SummarizeDescription(context.Function.Name, context.Function.Description)),
                            cancellationToken);
                    }

                    try
                    {
                        return await context.Function.InvokeAsync(context.Arguments, cancellationToken);
                    }
                    catch (McpProtocolException ex) when ((int)ex.ErrorCode is <= -32000 and >= -32099)
                    {
                        // P21 Toolbox's MCP façade deliberately shapes a per-conversation policy
                        // rejection (tool-call budget exhausted, insufficient scope, etc.) as a real
                        // JSON-RPC error in this reserved implementation-defined range, not as
                        // isError:true inside a normal tool result — see McpToolInvoker/
                        // JsonRpcErrorCodes on that side. That means the MCP client SDK throws here
                        // instead of returning content, and left alone this would count against
                        // MaximumConsecutiveErrorsPerRequest below and eventually blow up the whole
                        // turn with an aggregated exception the model never gets to explain. The
                        // façade wrote ex.Message specifically to be read and relayed by the model
                        // (e.g. "tell the user you have run out of lookups"), so hand it back as a
                        // normal function result instead of rethrowing: no consecutive-error charge,
                        // and the model gets a real turn to tell the user what happened in its own
                        // words. McpProtocolException.Message is "Request failed (remote): " +
                        // the façade's own message — strip that prefix so the model sees just the
                        // façade's intended guidance.
                        const string RemotePrefix = "Request failed (remote): ";
                        var message = ex.Message.StartsWith(RemotePrefix, StringComparison.Ordinal)
                            ? ex.Message[RemotePrefix.Length..]
                            : ex.Message;
                        return new Dictionary<string, object?>
                        {
                            ["status"] = "policy_rejected",
                            ["message"] = message
                        };
                    }
                };
            })
            .Build();
    }

    // A tool's Description is written for the model, not the end user (e.g. "Shows a part on
    // screen. Returns no product data — use get_product_specs for that.") — the first sentence
    // alone reads fine as a live status line, so this trims to that rather than showing the whole
    // thing. Falls back to a humanized function name for the rare tool with no description at all.
    private static string SummarizeDescription(string functionName, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            var sentenceEnd = description.IndexOfAny(['.', '!', '?']);
            var sentence = sentenceEnd > 0 ? description[..sentenceEnd] : description;
            sentence = sentence.Trim();
            if (sentence.Length > 80)
            {
                sentence = sentence[..80].TrimEnd() + "…";
            }
            if (sentence.Length > 0)
            {
                return sentence;
            }
        }

        return functionName.Replace(UiIntentNamePrefix, "", StringComparison.Ordinal).Replace('_', ' ');
    }
}
