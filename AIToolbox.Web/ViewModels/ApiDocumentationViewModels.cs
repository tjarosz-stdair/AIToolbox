namespace AIToolbox.Web.ViewModels;

public sealed class ApiDocumentationViewModel
{
    public IReadOnlyList<ApiEndpointDocumentation> Endpoints { get; set; } = [];
}

public sealed class ApiEndpointDocumentation
{
    public string AreaName { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public IReadOnlyList<ApiParameterDocumentation> Parameters { get; set; } = [];
    public string? RequestExampleJson { get; set; }
    public string? ResponseExampleJson { get; set; }
}

public sealed class ApiParameterDocumentation
{
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? AllowedValues { get; set; }
    public bool IsRequired { get; set; }
}
