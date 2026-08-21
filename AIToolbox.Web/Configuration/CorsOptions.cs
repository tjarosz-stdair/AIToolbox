namespace AIToolbox.Web.Configuration;

public sealed class CorsOptions
{
    public List<string> ChromeExtensionOrigins { get; set; } = [];

    /// <summary>
    /// Explicit allowlist for the picsearch chat endpoint — never a wildcard (Part 4.2). This is
    /// this tenant's own P21 Toolbox origin, e.g. "https://app.p21toolbox.com".
    /// </summary>
    public List<string> PicSearchOrigins { get; set; } = [];
}
