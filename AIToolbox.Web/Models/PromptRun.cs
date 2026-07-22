namespace AIToolbox.Web.Models;

public sealed class PromptRun
{
    public int Id { get; set; }

    public string UserEmail { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public bool IsPublic { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<PromptResponse> Responses { get; set; } = [];
}
