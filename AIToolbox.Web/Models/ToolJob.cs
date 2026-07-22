namespace AIToolbox.Web.Models;

public sealed class ToolJob
{
    public int Id { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public bool IsPublic { get; set; }
    public int? ToolDirectiveSnapshotId { get; set; }
    public int? ToolUserDirectiveSnapshotId { get; set; }
    public string Status { get; set; } = "Pending";
    public string? RequestJson { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
