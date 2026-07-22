using AIToolbox.Web.Data;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Api;

[ApiController]
[Authorize(Policy = "Tool:ProductParser")]
[Route("api/v1/product-parse/jobs")]
[Route("api/v1/pdf-parse/jobs")]
public sealed class ProductParseController : ControllerBase
{
    private readonly IProductParsingService _parser;
    private readonly IPlatformRepository _platform;

    public ProductParseController(
        IProductParsingService parser,
        IPlatformRepository platform)
    {
        _parser = parser;
        _platform = platform;
    }

    [HttpPost]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> Create(
        [FromForm] IFormFile? file,
        [FromForm] List<IFormFile>? files,
        [FromForm] string parseMode,
        [FromForm] string? userNotes,
        [FromForm] int? directiveId,
        [FromForm] bool isPublic,
        CancellationToken cancellationToken,
        [FromQuery(Name = "async")] bool submitAsync = false)
    {
        var uploadFiles = files is { Count: > 0 }
            ? files
            : file is null ? [] : new List<IFormFile> { file };

        if (uploadFiles.Count == 0)
        {
            return BadRequest(new { error = "Upload one or more PDF files using the 'file' or 'files' form fields." });
        }

        var email = User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized();
        }

        try
        {
            if (submitAsync)
            {
                var submission = await _parser.SubmitBatchAsync(
                    email,
                    User.GetTenantId(),
                    uploadFiles,
                    parseMode,
                    userNotes,
                    directiveId,
                    isPublic,
                    cancellationToken);

                return Accepted(new
                {
                    toolJobId = submission.ToolJobId,
                    isDuplicate = submission.IsDuplicate,
                    status = submission.Status,
                    message = submission.IsDuplicate
                        ? $"That same product parse is already {submission.Status.ToLowerInvariant()} as job #{submission.ToolJobId}."
                        : $"Job #{submission.ToolJobId} submitted. You can close the extension while it runs."
                });
            }

            var result = await _parser.ParseBatchAsync(
                email,
                User.GetTenantId(),
                uploadFiles,
                parseMode,
                userNotes,
                directiveId,
                isPublic,
                cancellationToken);

            if (result.Products.Count == 1
                && !string.Equals(result.ParseType, "rejectedCatalog", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(result.Products[0]);
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string scope = "all", CancellationToken cancellationToken = default)
    {
        var email = User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized();
        }

        return Ok(await _platform.GetProductParseJobsAsync(email, scope, cancellationToken));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized();
        }

        var job = await _platform.GetProductParseJobAsync(id, email, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }
}
