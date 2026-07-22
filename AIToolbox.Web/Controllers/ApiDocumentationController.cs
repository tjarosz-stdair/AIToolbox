using System.Collections;
using System.Reflection;
using System.Text.Json;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/ApiDocumentation")]
public sealed class ApiDocumentationController : Controller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IApiDescriptionGroupCollectionProvider _apiDescriptions;

    public ApiDocumentationController(IApiDescriptionGroupCollectionProvider apiDescriptions)
    {
        _apiDescriptions = apiDescriptions;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        var endpoints = _apiDescriptions.ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Where(description => description.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true)
            .Select(ToEndpoint)
            .OrderBy(endpoint => endpoint.Path)
            .ThenBy(endpoint => endpoint.HttpMethod)
            .ToList();

        return View(new ApiDocumentationViewModel { Endpoints = endpoints });
    }

    private static ApiEndpointDocumentation ToEndpoint(ApiDescription description)
    {
        var bodyParameter = description.ParameterDescriptions
            .FirstOrDefault(parameter => string.Equals(parameter.Source.DisplayName, "Body", StringComparison.OrdinalIgnoreCase));
        var responseType = description.SupportedResponseTypes
            .Select(response => response.Type)
            .FirstOrDefault(type => type is not null && type != typeof(void));

        return new ApiEndpointDocumentation
        {
            AreaName = GetAreaName(description.RelativePath),
            HttpMethod = description.HttpMethod ?? "ANY",
            Path = "/" + (description.RelativePath ?? string.Empty),
            ActionName = description.ActionDescriptor.DisplayName ?? string.Empty,
            Parameters = description.ParameterDescriptions.Select(parameter => new ApiParameterDocumentation
            {
                Name = parameter.Name,
                Source = parameter.Source.DisplayName ?? "Unknown",
                TypeName = GetFriendlyTypeName(parameter.Type),
                AllowedValues = GetAllowedValues(parameter),
                IsRequired = parameter.IsRequired
            }).ToList(),
            RequestExampleJson = bodyParameter?.Type is null ? null : ToExampleJson(bodyParameter.Type),
            ResponseExampleJson = GetResponseExampleJson(description, responseType)
        };
    }

    private static string? GetResponseExampleJson(ApiDescription description, Type? responseType)
    {
        if (responseType is not null
            && responseType != typeof(IActionResult)
            && responseType != typeof(ActionResult)
            && responseType != typeof(void))
        {
            return ToExampleJson(responseType);
        }

        var method = description.HttpMethod ?? "GET";
        var path = "/" + (description.RelativePath ?? string.Empty);
        var normalizedPath = path.ToLowerInvariant();

        if (normalizedPath.StartsWith("/api/v1/batches", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                && normalizedPath.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase))
            {
                return ToExampleJson(new { accepted = true });
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return ToExampleJson(typeof(CreateAiBatchJobResult));
            }

            if (normalizedPath.EndsWith("/requests", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.EndsWith("/results", StringComparison.OrdinalIgnoreCase))
            {
                return ToExampleJson(typeof(AiBatchRequest[]));
            }

            return normalizedPath.Contains("{id:int}", StringComparison.OrdinalIgnoreCase)
                ? ToExampleJson(typeof(AiBatchJob))
                : ToExampleJson(typeof(AiBatchJob[]));
        }

        if (normalizedPath.StartsWith("/api/v1/me", StringComparison.OrdinalIgnoreCase))
        {
            return ToExampleJson(new
            {
                email = "user@example.com",
                tenant = "00000000-0000-0000-0000-000000000000",
                role = "User"
            });
        }

        if (normalizedPath.StartsWith("/api/v1/products", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                && !normalizedPath.Contains("{id:int}", StringComparison.OrdinalIgnoreCase)
                    ? ToExampleJson(typeof(ProductLibraryItem[]))
                    : ToExampleJson(typeof(ProductLibraryItem));
        }

        if (normalizedPath.StartsWith("/api/v1/product-parse", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith("/api/v1/pdf-parse", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return ToExampleJson(new
                {
                    parseType = "catalog",
                    products = new[]
                    {
                        new
                        {
                            id = 123,
                            productName = "Example Product",
                            productCode = "EXAMPLE-001",
                            productDataJson = "{ }"
                        }
                    }
                });
            }

            return normalizedPath.Contains("{id:int}", StringComparison.OrdinalIgnoreCase)
                ? ToExampleJson(typeof(ProductParseJob))
                : ToExampleJson(typeof(ProductParseJob[]));
        }

        return ToExampleJson(new { message = "Response shape depends on runtime result." });
    }

    private static string ToExampleJson(Type type)
    {
        return JsonSerializer.Serialize(CreateExample(type, 0), JsonOptions);
    }

    private static string ToExampleJson(object value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static object? CreateExample(Type type, int depth)
    {
        if (depth > 3)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return "string";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return 123;
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return 123.45;
        if (type == typeof(bool)) return true;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "2026-07-21T12:00:00Z";
        if (type == typeof(Guid)) return "00000000-0000-0000-0000-000000000000";
        if (type == typeof(JsonElement)) return new { key = "value" };
        if (type.IsEnum) return Enum.GetNames(type).FirstOrDefault() ?? "Value";
        if (type == typeof(IFormFile)) return "<file>";

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var itemType = type.IsArray
                ? type.GetElementType()
                : type.GetGenericArguments().FirstOrDefault();
            return new[] { CreateExample(itemType ?? typeof(object), depth + 1) };
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead)
            .Take(24)
            .ToDictionary(
                property => JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                property => CreateExample(property.PropertyType, depth + 1));
        return properties.Count == 0 ? new { } : properties;
    }

    private static string? GetAllowedValues(ApiParameterDescription parameter)
    {
        if (parameter.Type is null)
        {
            return GetKnownStringValues(parameter.Name);
        }

        var directValues = GetAllowedValues(parameter.Type, parameter.Name);
        if (!string.IsNullOrWhiteSpace(directValues))
        {
            return directValues;
        }

        var type = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
        if (type == typeof(string)
            || type.IsPrimitive
            || type.IsEnum
            || typeof(IEnumerable).IsAssignableFrom(type))
        {
            return null;
        }

        var propertyValues = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => new
            {
                Name = JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                Values = GetAllowedValues(property.PropertyType, property.Name)
            })
            .Where(property => !string.IsNullOrWhiteSpace(property.Values))
            .Select(property => $"{property.Name}: {property.Values}")
            .ToList();

        return propertyValues.Count == 0 ? null : string.Join("; ", propertyValues);
    }

    private static string? GetAllowedValues(Type type, string? name)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            return string.Join(", ", Enum.GetNames(type));
        }

        if (type == typeof(bool))
        {
            return "true, false";
        }

        if (type == typeof(string))
        {
            return GetKnownStringValues(name);
        }

        return null;
    }

    private static string? GetKnownStringValues(string? name)
    {
        return name?.Trim() switch
        {
            "scope" or "Scope" => "all, mine, public",
            "parseMode" or "ParseMode" => string.Join(", ", ProductParsingService.ParseModeSameProduct, ProductParsingService.ParseModeSeparateProducts, ProductParsingService.ParseModeCatalog),
            "viewMode" or "ViewMode" => "new, jobs, library",
            "jobSortBy" or "JobSortBy" => "date, user, product, fileCount, status",
            "jobSortDirection" or "JobSortDirection" or "sortDirection" or "SortDirection" => "asc, desc",
            "toolKey" or "ToolKey" => "ProductParser, OrderTracking, GenericBatch",
            "inputMode" or "InputMode" => "json",
            "status" or "Status" => string.Join(", ", GetBatchStatuses()),
            "scopes" or "Scopes" or "scopesJson" or "ScopesJson" => "batch:read, batch:write, batch:cancel, tool:ProductParser, tool:OrderTracking, tool:GenericBatch",
            _ => null
        };
    }

    private static IReadOnlyList<string> GetBatchStatuses()
    {
        return typeof(AiBatchStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static string GetFriendlyTypeName(Type? type)
    {
        if (type is null) return "unknown";
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!type.IsGenericType) return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName))}>";
    }

    private static string GetAreaName(string? relativePath)
    {
        var segments = (relativePath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var area = segments.Length >= 3 && string.Equals(segments[1], "v1", StringComparison.OrdinalIgnoreCase)
            ? segments[2]
            : segments.Length >= 2 ? segments[1] : "other";

        return string.Join(" ", area.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Replace("api", "API", StringComparison.OrdinalIgnoreCase)
            .ToTitleCaseInvariant();
    }
}

internal static class ApiDocumentationStringExtensions
{
    public static string ToTitleCaseInvariant(this string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Other";
        }

        return string.Join(
            " ",
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
