namespace AIToolbox.Web.Configuration;

public sealed class AIToolsLoggingOptions
{
    public string BasePath { get; set; } = @"C:\inetpub\logs\SiteLogs\stdair.com_aitools";

    public int RetainedFileCountLimit { get; set; } = 31;

    public long FileSizeLimitBytes { get; set; } = 50 * 1024 * 1024;
}
