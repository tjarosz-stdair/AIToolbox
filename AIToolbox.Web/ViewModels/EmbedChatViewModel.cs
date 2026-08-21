namespace AIToolbox.Web.ViewModels;

public sealed class EmbedChatViewModel
{
    public required string ExpectedParentOrigin { get; init; }

    /// <summary>Matches PicSearchAskRequest's [StringLength(4000)] on Message.</summary>
    public int MaxMessageLength { get; init; } = 4000;

    public int PreferredWidth { get; init; } = 380;
}
