using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "Tool:ProductParser")]
[Route("ProductParser")]
[Route("PdfProductParser")]
public sealed class ProductParserController : Controller
{
    private const string SelectedDirectiveCookieName = "AIToolbox.ProductParser.SelectedDirectiveId";

    private readonly IProductParsingService _parser;
    private readonly IPlatformRepository _platform;

    public ProductParserController(
        IProductParsingService parser,
        IPlatformRepository platform)
    {
        _parser = parser;
        _platform = platform;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string viewMode = "new",
        string scope = "all",
        string? search = null,
        string? supplier = null,
        string? manufacturer = null,
        string? category = null,
        decimal? minValue = null,
        decimal? maxValue = null,
        int jobPage = 1,
        string jobSortBy = "date",
        string jobSortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        return View(await BuildViewModelAsync(
            viewMode,
            scope,
            search,
            supplier,
            manufacturer,
            category,
            minValue,
            maxValue,
            jobPage,
            jobSortBy,
            jobSortDirection,
            cancellationToken));
    }

    [HttpPost("Parse")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> Parse(ProductParserViewModel model, CancellationToken cancellationToken)
    {
        if (model.Files.Count == 0)
        {
            ModelState.AddModelError(nameof(model.Files), "Choose at least one source file to parse.");
        }

        if (!ModelState.IsValid)
        {
            var invalidModel = await BuildViewModelAsync("new", model.Scope, null, null, null, null, null, null, 1, "date", "desc", cancellationToken);
            invalidModel.IsPublic = model.IsPublic;
            invalidModel.ParseMode = model.ParseMode;
            invalidModel.UserNotes = model.UserNotes;
            invalidModel.SelectedDirectiveId = model.SelectedDirectiveId;
            return View("Index", invalidModel);
        }

        var email = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        try
        {
            var submission = await _parser.SubmitBatchAsync(
                email,
                User.GetTenantId(),
                model.Files,
                model.ParseMode,
                model.UserNotes,
                model.SelectedDirectiveId,
                model.IsPublic,
                cancellationToken);

            PersistSelectedDirective(model.SelectedDirectiveId);

            TempData["StatusMessage"] = submission.IsDuplicate
                ? $"That same product parse is already {submission.Status.ToLowerInvariant()} as job #{submission.ToolJobId}."
                : $"Job #{submission.ToolJobId} submitted. You can leave this page while it runs.";

            return RedirectToAction(nameof(Index), new { viewMode = "new" });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            var errorModel = await BuildViewModelAsync("new", model.Scope, null, null, null, null, null, null, 1, "date", "desc", cancellationToken);
            errorModel.IsPublic = model.IsPublic;
            errorModel.ParseMode = model.ParseMode;
            errorModel.UserNotes = model.UserNotes;
            errorModel.SelectedDirectiveId = model.SelectedDirectiveId;
            return View("Index", errorModel);
        }
    }

    [HttpGet("Product/{id:int}")]
    public async Task<IActionResult> Product(int id, CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var detail = await _platform.GetParsedProductDetailAsync(id, email, cancellationToken);
        return detail is null
            ? NotFound()
            : View(new ParsedProductDetailViewModel
            {
                Product = detail.Product,
                Job = detail.Job,
                Versions = detail.Versions
            });
    }

    [HttpGet("Job/{id:int}")]
    public async Task<IActionResult> Job(int id, int? productId, CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var job = await _platform.GetVisibleToolJobAsync(id, email, cancellationToken);
        if (job is null || !string.Equals(job.ToolKey, ProductParsingService.ToolKey, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var files = await _platform.GetProductParseJobsForToolJobAsync(job.Id, email, cancellationToken);
        var products = await _platform.GetParsedProductsForToolJobAsync(job.Id, email, cancellationToken);
        var selectedProduct = products.FirstOrDefault(product => product.Id == productId) ?? products.FirstOrDefault();

        return View(new ProductParseJobDetailViewModel
        {
            Job = job,
            Files = files,
            Products = products,
            SelectedProductId = selectedProduct?.Id,
            SelectedProduct = selectedProduct
        });
    }

    [HttpPost("Job/{id:int}/Visibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateJobVisibility(
        int id,
        bool isPublic,
        string? scope,
        string? viewMode,
        int jobPage,
        string? jobSortBy,
        string? jobSortDirection,
        CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var updated = await _platform.UpdateProductParseJobVisibilityAsync(id, email, isPublic, cancellationToken);
        if (updated is null)
        {
            return Forbid();
        }

        TempData["StatusMessage"] = updated.IsPublic
            ? "Product parse job and linked products are now public."
            : "Product parse job and linked products are now private.";

        var normalizedViewMode = NormalizeViewMode(viewMode) == "jobs" ? "jobs" : "library";
        return RedirectToAction(nameof(Index), new
        {
            viewMode = normalizedViewMode,
            scope = NormalizeScope(scope),
            jobPage = normalizedViewMode == "jobs" ? Math.Max(jobPage, 1) : 1,
            jobSortBy = normalizedViewMode == "jobs" ? jobSortBy ?? "date" : "date",
            jobSortDirection = normalizedViewMode == "jobs" ? jobSortDirection ?? "desc" : "desc"
        });
    }

    private async Task<ProductParserViewModel> BuildViewModelAsync(
        string? viewMode,
        string? scope,
        string? search,
        string? supplier,
        string? manufacturer,
        string? category,
        decimal? minValue,
        decimal? maxValue,
        int jobPage,
        string? jobSortBy,
        string? jobSortDirection,
        CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress() ?? User.Identity?.Name ?? "unknown";
        var normalizedScope = NormalizeScope(scope);
        var normalizedViewMode = NormalizeViewMode(viewMode);
        var filter = new ProductLibraryFilter
        {
            Scope = normalizedScope,
            Search = search,
            Supplier = supplier,
            Manufacturer = manufacturer,
            Category = category,
            MinValue = minValue,
            MaxValue = maxValue
        };
        var jobHistory = normalizedViewMode == "jobs"
            ? await _platform.GetProductParseJobHistoryAsync(
                email,
                normalizedScope,
                jobPage,
                20,
                jobSortBy,
                jobSortDirection,
                cancellationToken)
            : new ProductParseJobHistoryResult();
        var directives = (await _platform.GetDirectivesAsync(cancellationToken))
            .Where(directive => directive.IsActive
                && string.Equals(directive.ToolKey, ProductParsingService.ToolKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(directive => directive.Name)
            .ThenBy(directive => directive.Version)
            .ToList();
        var activeDirective = await _platform.GetActiveDirectiveAsync(ProductParsingService.ToolKey, cancellationToken);
        var selectedDirectiveId = ResolveSelectedDirectiveId(directives, activeDirective?.Id);

        return new ProductParserViewModel
        {
            ViewMode = normalizedViewMode,
            Scope = normalizedScope,
            CurrentUserEmail = email,
            IsPublic = true,
            SelectedDirectiveId = selectedDirectiveId,
            AvailableDirectives = directives,
            JobPage = jobHistory.Page,
            JobPageSize = jobHistory.PageSize,
            JobSortBy = jobHistory.SortBy,
            JobSortDirection = jobHistory.SortDirection,
            Search = search,
            Supplier = supplier,
            Manufacturer = manufacturer,
            Category = category,
            MinValue = minValue,
            MaxValue = maxValue,
            JobHistory = jobHistory,
            Jobs = normalizedViewMode == "jobs" ? await _platform.GetProductParseJobsAsync(email, normalizedScope, cancellationToken) : [],
            Products = normalizedViewMode == "library" ? await _platform.GetParsedProductsAsync(email, search, cancellationToken) : [],
            LibraryGroups = normalizedViewMode == "library" ? await _platform.GetProductLibraryGroupsAsync(email, filter, cancellationToken) : [],
            SupplierOptions = await _platform.GetProductLibraryFilterValuesAsync(email, "Supplier", cancellationToken),
            ManufacturerOptions = await _platform.GetProductLibraryFilterValuesAsync(email, "Manufacturer", cancellationToken),
            CategoryOptions = await _platform.GetProductLibraryFilterValuesAsync(email, "Category", cancellationToken)
        };
    }

    private int? ResolveSelectedDirectiveId(IReadOnlyList<ToolDirective> directives, int? activeDirectiveId)
    {
        if (Request.Cookies.TryGetValue(SelectedDirectiveCookieName, out var cookieValue)
            && int.TryParse(cookieValue, out var cookieDirectiveId)
            && directives.Any(directive => directive.Id == cookieDirectiveId))
        {
            return cookieDirectiveId;
        }

        return activeDirectiveId is int id && directives.Any(directive => directive.Id == id)
            ? id
            : directives.FirstOrDefault()?.Id;
    }

    private void PersistSelectedDirective(int? directiveId)
    {
        if (directiveId is not int id)
        {
            Response.Cookies.Delete(SelectedDirectiveCookieName);
            return;
        }

        Response.Cookies.Append(
            SelectedDirectiveCookieName,
            id.ToString(),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(180),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps
            });
    }

    private static string NormalizeViewMode(string? viewMode)
    {
        return viewMode?.Trim().ToLowerInvariant() switch
        {
            "history" => "library",
            "library" => "library",
            "jobs" => "jobs",
            _ => "new"
        };
    }

    private static string NormalizeScope(string? scope)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            "mine" => "mine",
            "public" => "public",
            _ => "all"
        };
    }
}
