using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Api;

[ApiController]
[Authorize(Policy = "Tool:ProductParser")]
[Route("api/v1/products")]
public sealed class ProductsController : ControllerBase
{
    private readonly IPlatformRepository _platform;

    public ProductsController(IPlatformRepository platform)
    {
        _platform = platform;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string scope = "all",
        [FromQuery] string? supplier = null,
        [FromQuery] string? manufacturer = null,
        [FromQuery] string? category = null,
        [FromQuery] decimal? minValue = null,
        [FromQuery] decimal? maxValue = null,
        CancellationToken cancellationToken = default)
    {
        var email = User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized();
        }

        return Ok(await _platform.GetProductLibraryItemsAsync(email, new ProductLibraryFilter
        {
            Scope = scope,
            Search = search,
            Supplier = supplier,
            Manufacturer = manufacturer,
            Category = category,
            MinValue = minValue,
            MaxValue = maxValue
        }, cancellationToken));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized();
        }

        var libraryItem = await _platform.GetProductLibraryItemAsync(id, email, cancellationToken);
        if (libraryItem is not null)
        {
            return Ok(libraryItem);
        }

        var product = await _platform.GetParsedProductAsync(id, email, cancellationToken);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] JsonElement productData, CancellationToken cancellationToken)
    {
        var email = User.GetEmailAddress();
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized();
        }

        var existing = await _platform.GetParsedProductAsync(id, email, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        if (!string.Equals(existing.UserEmail, email, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var json = JsonSerializer.Serialize(productData, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var updated = new ParsedProduct
        {
            Id = id,
            ProductParseJobId = existing.ProductParseJobId,
            UserEmail = email,
            ProductDataJson = json,
            ProductName = TryReadString(productData, "ProductName"),
            ProductCode = TryReadString(productData, "ProductCode")
        };

        if (!await _platform.UpdateParsedProductAsync(updated, cancellationToken))
        {
            return NotFound();
        }

        return Ok(await _platform.GetParsedProductAsync(id, email, cancellationToken));
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }
}
