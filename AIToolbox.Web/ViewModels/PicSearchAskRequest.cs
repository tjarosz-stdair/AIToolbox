using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace AIToolbox.Web.ViewModels;

public sealed class PicSearchAskRequest
{
    [Required]
    [StringLength(200)]
    public string ContextKey { get; set; } = string.Empty;

    [Required]
    [StringLength(4000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>The UI surfaces currently registered with the panel's broker (Part 3, directive
    /// §3) — a different "surface" concept from McpClientCredential.Surface. Drives which
    /// capability manifests get unioned into this turn's tool set. Never drives tool-call
    /// availability by itself alone spanning a context change.</summary>
    public string[]? UiSurfaces { get; set; }

    /// <summary>Opaque, broker-supplied page context (e.g. a title/label for the chip) — passed
    /// through to the system prompt, never persisted beyond what a turn needs.</summary>
    public JsonElement? PageContext { get; set; }

    /// <summary>Drives the "Auto-navigate PIC Search" checkbox behavior: when true, the system
    /// prompt instructs the model to proactively invoke ui_ navigation tools instead of asking
    /// the user's permission first. Defaults true so a caller that omits the field (or predates
    /// the checkbox) still gets the "standard practice" behavior the checkbox represents.</summary>
    public bool AutoNavigate { get; set; } = true;
}
