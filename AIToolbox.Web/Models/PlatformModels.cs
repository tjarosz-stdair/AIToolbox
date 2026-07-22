namespace AIToolbox.Web.Models;

public sealed class ToolDefinition
{
    public int Id { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public ToolAccessMode AccessMode { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int? ActiveProviderId { get; set; }
    public string? ActiveProviderName { get; set; }
}

public sealed class ToolDirective
{
    public int Id { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string TargetSchemaJson { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ToolDirectiveSnapshot
{
    public int Id { get; set; }
    public int ToolDirectiveId { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string TargetSchemaJson { get; set; } = string.Empty;
    public string? UserAdditiveDirective { get; set; }
    public int Version { get; set; } = 1;
    public string ContentHash { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class ToolUserDirectiveSnapshot
{
    public int Id { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string UserAdditiveDirective { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class ToolUserAllowListEntry
{
    public int Id { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public string? GrantedByEmail { get; set; }
}

public sealed class ProductParseJob
{
    public int Id { get; set; }
    public int ToolJobId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public bool IsPublic { get; set; }
    public int ToolDirectiveId { get; set; }
    public int? ToolDirectiveSnapshotId { get; set; }
    public int? ToolUserDirectiveSnapshotId { get; set; }
    public int AiProviderId { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? DurationMs { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? AiProviderName { get; set; }
    public string? AiProviderModelName { get; set; }
    public string? DirectiveName { get; set; }
    public int? DirectiveVersion { get; set; }
    public string? DirectiveSnapshotHash { get; set; }
    public int ParsedProductCount { get; set; }
    public string? ParsedProductNames { get; set; }
}

public sealed class ParsedProduct
{
    public int Id { get; set; }
    public int ProductParseJobId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string ProductDataJson { get; set; } = "{}";
    public string? ProductName { get; set; }
    public string? ProductCode { get; set; }
    public bool IsSentToZoho { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ProductLibraryItem
{
    public int Id { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public int? PrimaryParsedProductId { get; set; }
    public string ProductDataJson { get; set; } = "{}";
    public string? ProductName { get; set; }
    public string? ProductCode { get; set; }
    public string? Supplier { get; set; }
    public string? Manufacturer { get; set; }
    public string? Category { get; set; }
    public decimal? ProductValue { get; set; }
    public string? Currency { get; set; }
    public string? SourceSummary { get; set; }
    public bool IsSentToZoho { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ProductLibraryRun
{
    public int ProductLibraryItemId { get; set; }
    public int? ParsedProductId { get; set; }
    public int? ToolJobId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? Status { get; set; }
    public string? OriginalFileName { get; set; }
    public string? DirectiveName { get; set; }
    public int? DirectiveVersion { get; set; }
}

public sealed class ProductLibraryGroup
{
    public string? ProductName { get; set; }
    public string? ProductCode { get; set; }
    public string? Supplier { get; set; }
    public string? Manufacturer { get; set; }
    public string? Category { get; set; }
    public decimal? ProductValue { get; set; }
    public string? Currency { get; set; }
    public int RunCount { get; set; }
    public DateTime LastRunUtc { get; set; }
    public int? LatestParsedProductId { get; set; }
    public IReadOnlyList<ProductLibraryRun> Runs { get; set; } = [];
}

public sealed class ProductLibraryDocument
{
    public int Id { get; set; }
    public int ProductLibraryItemId { get; set; }
    public int ProductParseJobId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class ProductLibraryFilter
{
    public string Scope { get; set; } = "all";
    public string? Search { get; set; }
    public string? Supplier { get; set; }
    public string? Manufacturer { get; set; }
    public string? Category { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
}

public sealed class ProductParseJobHistoryItem
{
    public int ToolJobId { get; set; }
    public int? FirstProductParseJobId { get; set; }
    public int? FirstParsedProductId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public int FileCount { get; set; }
    public int ProductCount { get; set; }
    public string? ProductNames { get; set; }
    public string? OriginalFileNames { get; set; }
    public string? DirectiveName { get; set; }
    public int? DirectiveVersion { get; set; }
    public string? AiProviderName { get; set; }
    public string? AiProviderModelName { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class ProductParseJobHistoryResult
{
    public IReadOnlyList<ProductParseJobHistoryItem> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string SortBy { get; set; } = "date";
    public string SortDirection { get; set; } = "desc";
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed class ParsedProductDetail
{
    public required ParsedProduct Product { get; set; }
    public ProductParseJob? Job { get; set; }
    public IReadOnlyList<ParsedProduct> Versions { get; set; } = [];
}

public sealed class ToolUsageLog
{
    public int Id { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public int AiProviderId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public int DurationMs { get; set; }
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class ModelPricing
{
    public int Id { get; set; }
    public AiProviderType ProviderType { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public decimal InputPricePer1kUsd { get; set; }
    public decimal OutputPricePer1kUsd { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public bool IsActive { get; set; } = true;
}
