namespace AIToolbox.Web.Configuration;

public sealed class SpeechAudioStorageOptions
{
    public bool Enabled { get; set; } = true;

    public string BasePath { get; set; } = string.Empty;

    public int RetentionDays { get; set; } = 14;
}
