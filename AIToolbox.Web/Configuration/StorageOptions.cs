namespace AIToolbox.Web.Configuration;

public sealed class StorageOptions
{
    public string BasePath { get; set; } = string.Empty;

    public long MaxUploadBytes { get; set; } = 15 * 1024 * 1024;

    public Dictionary<string, string> ToolSubpaths { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProductParser"] = "ProductParser"
    };
}
