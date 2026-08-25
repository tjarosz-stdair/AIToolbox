namespace AIToolbox.Web.Configuration;

public sealed class FfmpegOptions
{
    public string ExecutablePath { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 15;
}
