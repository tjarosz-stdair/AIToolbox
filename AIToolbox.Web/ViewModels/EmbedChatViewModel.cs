namespace AIToolbox.Web.ViewModels;

public sealed class EmbedChatViewModel
{
    public required string ExpectedParentOrigin { get; init; }

    /// <summary>Matches PicSearchAskRequest's [StringLength(4000)] on Message.</summary>
    public int MaxMessageLength { get; init; } = 4000;

    public int PreferredWidth { get; init; } = 380;

    /// <summary>Whether an enabled Azure Speech external resource is configured — decided
    /// server-side at render time so the mic button never flashes into view before being hidden
    /// (checking client-side after a failed request would do exactly that).</summary>
    public bool SpeechEnabled { get; init; }
}
