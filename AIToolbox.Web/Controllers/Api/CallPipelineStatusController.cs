using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services.CallEntityExtraction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Api;

/// <summary>
/// Reports whether the RingCX call-entity-extraction pipeline can actually produce a result right
/// now. Polled by RingCXAgentHubAPI's AiToolboxHealthPoller before the Listener spends Azure
/// Speech transcription budget on a call. p21ValidationHealthy is the field that actually gates
/// spend -- P21 validation is mandatory for every notification this pipeline can ever emit (no
/// code path produces one without a successful P21 MCP round-trip), so an unhealthy circuit here
/// means no prompt could be produced regardless of anything else being up.
/// </summary>
[ApiController]
[Authorize(Policy = "ApiConsumer")]
[Route("api/v1/call-pipeline")]
public sealed class CallPipelineStatusController : ControllerBase
{
    private readonly IP21IdentifierValidationClient _p21Client;
    private readonly IExternalResourceRepository _resources;

    public CallPipelineStatusController(
        IP21IdentifierValidationClient p21Client,
        IExternalResourceRepository resources)
    {
        _p21Client = p21Client;
        _resources = resources;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        if (!User.IsApiConsumer() || !User.HasApiScope("callpipeline:read"))
        {
            return Forbid();
        }

        var p21ValidationHealthy = _p21Client.IsHealthy;
        var azureSpeechConfigured = await _resources.GetEnabledByTypeAsync(ExternalResourceType.AzureSpeech, cancellationToken) is not null;
        var callDataApiConfigured = await _resources.GetEnabledByTypeAsync(ExternalResourceType.CallDataApi, cancellationToken) is not null;

        return Ok(new
        {
            // The gating field -- see class doc. azureSpeechConfigured/callDataApiConfigured are
            // informational only, not part of the health decision.
            healthy = p21ValidationHealthy,
            p21ValidationHealthy,
            azureSpeechConfigured,
            callDataApiConfigured,
            checkedAtUtc = DateTime.UtcNow
        });
    }
}
