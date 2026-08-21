namespace AIToolbox.Web.Configuration;

public sealed class EmbedOptions
{
    /// <summary>
    /// Origins allowed to frame /embed/chat. Deliberately separate from Cors:PicSearchOrigins —
    /// "who may call the API" and "who may frame the page" are different questions with
    /// different answers (the eventual Chrome-extension-broker case applies to one and not the
    /// other), and collapsing them means a future CORS change silently widens the framing policy.
    /// </summary>
    public List<string> ParentOrigins { get; set; } = [];
}
