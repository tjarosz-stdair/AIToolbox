using System.Text.Json;
using AIToolbox.Web.Models;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Services;

/// <summary>
/// A local navigation-verb tool built from a P21 Toolbox UI capability manifest entry (directive
/// §5.4). A direct AIFunction subclass rather than AIFunctionFactory.Create: that factory infers
/// its JSON schema by reflecting over a .NET delegate's parameters and has no path to carry an
/// externally-supplied schema like a capability manifest's InputSchema. Sits in ChatOptions.Tools
/// next to McpClientTools exactly as any other AIFunction-derived type does.
///
/// Fire-and-forget by design (§5.6): this calls nothing else and never blocks. There is no
/// broker/parent-frame connection on the server to post an intent to — this only returns
/// {"status":"queued"} so the model can react, per §5.5. Wiring an actual client-side
/// aitoolbox:intent post (via the embed panel's already-built, but as-yet-uncalled,
/// window.AIToolboxEmbed.sendIntent) needs a corresponding NDJSON event added to
/// /api/picsearch/ask once P21 Toolbox has real capabilities to test against — not built yet,
/// since nothing exists on either side to exercise it end-to-end today.
/// </summary>
public sealed class UiIntentFunction : AIFunction
{
    private readonly UiCapability _capability;

    public UiIntentFunction(UiCapability capability)
    {
        _capability = capability;
    }

    public override string Name => _capability.Name;

    public override string Description => _capability.Description;

    public override JsonElement JsonSchema => _capability.InputSchema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        return new ValueTask<object?>(new Dictionary<string, object?> { ["status"] = "queued" });
    }
}
