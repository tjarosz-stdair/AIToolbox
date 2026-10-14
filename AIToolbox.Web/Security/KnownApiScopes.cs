namespace AIToolbox.Web.Security;

/// <summary>
/// Single source of truth for the fixed (non-tool) API consumer scope strings. Every
/// [ApiController] scope check should reference these instead of a hand-typed literal --
/// a colon-vs-period typo in an admin-entered scope value once caused a hard-to-spot 403
/// (HasApiScope is an exact string match, no punctuation normalization). Tool scopes
/// ("tool:{ToolKey}") aren't listed here since they're data-driven from the tool registry (see
/// IPlatformRepository.GetToolsAsync) -- ApiConsumersController.Detail assembles those separately
/// for the admin scope picker.
/// </summary>
public static class KnownApiScopes
{
    public const string BatchRead = "batch:read";
    public const string BatchWrite = "batch:write";
    public const string BatchCancel = "batch:cancel";
    public const string CallPipelineRead = "callpipeline:read";
    public const string CallTranscriptsWrite = "calltranscripts:write";
    public const string CallEventsDisposition = "callevents:disposition";

    /// <summary>Value + a plain-English description of what it grants -- backs the admin scope
    /// picker's legend (ApiConsumersController.GetAvailableScopesAsync). Add a new fixed scope by
    /// adding both the constant above and an entry here; the legend and picker pick it up with no
    /// view changes needed.</summary>
    public static readonly IReadOnlyList<ScopeDefinition> All =
    [
        new(BatchRead, "List and view AI batch jobs and their requests/results."),
        new(BatchWrite, "Create a new AI batch job (also requires the specific tool:{ToolKey} scope for the job's tool)."),
        new(BatchCancel, "Cancel an in-progress AI batch job."),
        new(CallPipelineRead,
            "Read RingCX call-pipeline health status (GET /api/v1/call-pipeline/status). Used by the Hub's " +
            "eligibility poller to decide whether to spend Azure Speech budget on a call -- not a real agent action."),
        new(CallTranscriptsWrite,
            "Ingest RingCX call transcript segments and end-of-call signals " +
            "(POST /api/v1/call-transcripts/...). Required for the admin Live Call Monitor to receive anything."),
        new(CallEventsDisposition,
            "Report what happened to a detected-entity prompt card " +
            "(POST /api/v1/call-events/{id}/disposition), e.g. that an agent clicked an action.")
    ];
}

/// <summary>One scope string plus what it grants, for the admin key-creation scope picker/legend.</summary>
public sealed record ScopeDefinition(string Value, string Description);
