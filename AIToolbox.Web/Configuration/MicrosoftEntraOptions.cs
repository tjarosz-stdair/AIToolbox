namespace AIToolbox.Web.Configuration;

public sealed class MicrosoftEntraOptions
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    public string TenantId { get; set; } = "common";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string CallbackPath { get; set; } = "/signin-oidc";

    public string ApiAudience { get; set; } = string.Empty;

    public string ApiScope { get; set; } = "AITools.Access";

    public List<string> AllowedTenantIds { get; set; } = [];

    public List<string> AllowedDomains { get; set; } = [];

    public bool EnableAutoProvisioning { get; set; } = true;

    public List<string> BootstrapAdminEmails { get; set; } = [];
}
