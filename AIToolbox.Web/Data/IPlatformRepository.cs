using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IPlatformRepository
{
    Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default);
    Task<ToolDefinition?> GetToolAsync(string toolKey, CancellationToken cancellationToken = default);
    Task<ToolDefinition?> GetToolByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<int> SaveToolAsync(ToolDefinition tool, int? activeProviderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolUserAllowListEntry>> GetAllowListAsync(string toolKey, CancellationToken cancellationToken = default);
    Task<bool> IsUserAllowedForToolAsync(string toolKey, string userEmail, CancellationToken cancellationToken = default);
    Task AddAllowListEntryAsync(string toolKey, string userEmail, string? grantedByEmail, CancellationToken cancellationToken = default);
    Task SetAllowListEntryEnabledAsync(int id, bool isEnabled, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolDirective>> GetDirectivesAsync(CancellationToken cancellationToken = default);
    Task<ToolDirective?> GetDirectiveAsync(int id, CancellationToken cancellationToken = default);
    Task<ToolDirective?> GetActiveDirectiveAsync(string toolKey, CancellationToken cancellationToken = default);
    Task<int> SaveDirectiveAsync(ToolDirective directive, CancellationToken cancellationToken = default);
    Task<ToolDirectiveSnapshot> GetOrCreateDirectiveSnapshotAsync(ToolDirectiveSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<ToolDirectiveSnapshot?> GetDirectiveSnapshotAsync(int id, CancellationToken cancellationToken = default);
    Task<ToolUserDirectiveSnapshot?> GetOrCreateUserDirectiveSnapshotAsync(ToolUserDirectiveSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<ToolUserDirectiveSnapshot?> GetUserDirectiveSnapshotAsync(int id, CancellationToken cancellationToken = default);

    Task<int> CreateToolJobAsync(ToolJob job, CancellationToken cancellationToken = default);
    Task UpdateToolJobAsync(ToolJob job, CancellationToken cancellationToken = default);
    Task<ToolJob?> GetToolJobByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<ToolJob?> GetToolJobAsync(int id, string userEmail, CancellationToken cancellationToken = default);
    Task<ToolJob?> GetVisibleToolJobAsync(int id, string userEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolJob>> GetActiveToolJobsAsync(string userEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolJob>> GetActiveToolJobsForToolAsync(string toolKey, CancellationToken cancellationToken = default);
    Task<ToolJob?> GetActiveToolJobByRequestFingerprintAsync(string userEmail, string toolKey, string requestFingerprint, CancellationToken cancellationToken = default);

    Task<int> CreateProductParseJobAsync(ProductParseJob job, CancellationToken cancellationToken = default);
    Task UpdateProductParseJobAsync(ProductParseJob job, CancellationToken cancellationToken = default);
    Task<ProductParseJob?> UpdateProductParseJobVisibilityAsync(int id, string userEmail, bool isPublic, CancellationToken cancellationToken = default);
    Task<ProductParseJob?> GetProductParseJobAsync(int id, string userEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductParseJob>> GetProductParseJobsForToolJobAsync(int toolJobId, string userEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductParseJob>> GetProductParseJobsAsync(string userEmail, string scope, CancellationToken cancellationToken = default);
    Task<ProductParseJobHistoryResult> GetProductParseJobHistoryAsync(
        string userEmail,
        string scope,
        int page,
        int pageSize,
        string? sortBy,
        string? sortDirection,
        CancellationToken cancellationToken = default);

    Task<int> CreateParsedProductAsync(ParsedProduct product, CancellationToken cancellationToken = default);
    Task<ParsedProduct?> GetParsedProductAsync(int id, string userEmail, CancellationToken cancellationToken = default);
    Task<ParsedProductDetail?> GetParsedProductDetailAsync(int id, string userEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParsedProduct>> GetParsedProductsAsync(string userEmail, string? search, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParsedProduct>> GetParsedProductsForToolJobAsync(int toolJobId, string userEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParsedProduct>> GetParsedProductVersionsAsync(int parsedProductId, string userEmail, CancellationToken cancellationToken = default);
    Task<bool> UpdateParsedProductAsync(ParsedProduct product, CancellationToken cancellationToken = default);

    Task<int> CreateProductLibraryItemAsync(ProductLibraryItem item, IReadOnlyList<int> productParseJobIds, CancellationToken cancellationToken = default);
    Task<ProductLibraryItem?> GetProductLibraryItemAsync(int id, string userEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductLibraryItem>> GetProductLibraryItemsAsync(string userEmail, ProductLibraryFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductLibraryGroup>> GetProductLibraryGroupsAsync(string userEmail, ProductLibraryFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetProductLibraryFilterValuesAsync(string userEmail, string fieldName, CancellationToken cancellationToken = default);

    Task LogUsageAsync(ToolUsageLog usage, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolUsageLog>> GetUsageAsync(string? userEmail, string? toolKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelPricing>> GetPricingAsync(CancellationToken cancellationToken = default);
    Task AddPricingAsync(ModelPricing pricing, CancellationToken cancellationToken = default);
    Task<decimal?> EstimateCostAsync(AiProvider provider, int? inputTokens, int? outputTokens, CancellationToken cancellationToken = default);
}
