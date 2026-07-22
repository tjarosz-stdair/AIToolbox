namespace AIToolbox.Web.Configuration;

public sealed class AIToolsDataProtectionOptions
{
    public string ApplicationName { get; set; } = "AIToolsCompare";

    public string? KeysPath { get; set; }
}
