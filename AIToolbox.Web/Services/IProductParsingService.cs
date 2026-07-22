using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

public interface IProductParsingService
{
    Task<ParsedProduct> ParseAsync(
        string userEmail,
        string? tenantId,
        IFormFile file,
        bool isPublic,
        CancellationToken cancellationToken = default);

    Task<ProductParseBatchResult> ParseBatchAsync(
        string userEmail,
        string? tenantId,
        IReadOnlyList<IFormFile> files,
        string parseMode,
        string? userNotes,
        int? directiveId,
        bool isPublic,
        CancellationToken cancellationToken = default);

    Task<ProductParseSubmissionResult> SubmitBatchAsync(
        string userEmail,
        string? tenantId,
        IReadOnlyList<IFormFile> files,
        string parseMode,
        string? userNotes,
        int? directiveId,
        bool isPublic,
        CancellationToken cancellationToken = default);

    Task ExecuteQueuedJobAsync(int toolJobId, CancellationToken cancellationToken = default);

    Task<bool> RequestCancellationAsync(int toolJobId, string userEmail, CancellationToken cancellationToken = default);
}

public sealed class ProductParseBatchResult
{
    public string ParseType { get; set; } = "singleProduct";
    public string? RejectedReason { get; set; }
    public IReadOnlyList<ParsedProduct> Products { get; set; } = [];
    public IReadOnlyList<int> ProductLibraryItemIds { get; set; } = [];
}

public sealed class ProductParseSubmissionResult
{
    public int ToolJobId { get; set; }
    public bool IsDuplicate { get; set; }
    public string Status { get; set; } = "Queued";
}
