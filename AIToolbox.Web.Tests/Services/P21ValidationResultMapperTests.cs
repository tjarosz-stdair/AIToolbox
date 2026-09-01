using System.Text.Json;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services.CallEntityExtraction;

namespace AIToolbox.Web.Tests.Services;

public sealed class P21ValidationResultMapperTests
{
    [Fact]
    public void MapModelResolution_Ok_ReturnsValidatedWithPimCode()
    {
        var structured = Parse("""{"status":"ok","resolved":{"pimCode":"59MN7C080C171214","primarySalesPackageId":"SP-123"}}""");

        var result = P21ValidationResultMapper.MapModelResolution(structured, "59MN7C080C171214");

        Assert.NotNull(result);
        Assert.Equal(ValidationStatus.Validated, result!.Status);
        Assert.Equal(["59MN7C080C171214"], result.CandidateValues);
        Assert.Equal("p21_mcp:resolve_model", result.Source);
    }

    [Fact]
    public void MapModelResolution_Ok_FallsBackToSalesPackageIdWhenNoPimCode()
    {
        var structured = Parse("""{"status":"ok","resolved":{"pimCode":null,"primarySalesPackageId":"SP-123"}}""");

        var result = P21ValidationResultMapper.MapModelResolution(structured, "queried-value");

        Assert.Equal(["SP-123"], result!.CandidateValues);
    }

    [Fact]
    public void MapModelResolution_Ambiguous_ReturnsAllCandidateValues()
    {
        var structured = Parse("""
            {"status":"ambiguous","candidates":[
                {"pimCode":"MODEL-A"},
                {"pimCode":null,"primarySalesPackageId":"SP-B"}
            ]}
            """);

        var result = P21ValidationResultMapper.MapModelResolution(structured, "irrelevant");

        Assert.Equal(ValidationStatus.Ambiguous, result!.Status);
        Assert.Equal(["MODEL-A", "SP-B"], result.CandidateValues);
    }

    [Fact]
    public void MapModelResolution_Ambiguous_ReturnsNullWhenNoUsableCandidates()
    {
        var structured = Parse("""{"status":"ambiguous","candidates":[{"pimCode":null,"primarySalesPackageId":null}]}""");

        var result = P21ValidationResultMapper.MapModelResolution(structured, "irrelevant");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("notFound")]
    [InlineData("unavailable")]
    public void MapModelResolution_NotFoundOrUnavailable_SuppressesNotification(string status)
    {
        var structured = Parse($$"""{"status":"{{status}}"}""");

        var result = P21ValidationResultMapper.MapModelResolution(structured, "irrelevant");

        Assert.Null(result);
    }

    [Fact]
    public void MapWarrantyResult_Ok_ReturnsValidated()
    {
        var structured = Parse("""{"status":"ok","serialNumber":"1234567890"}""");

        var result = P21ValidationResultMapper.MapWarrantyResult(structured, "1234567890");

        Assert.Equal(ValidationStatus.Validated, result!.Status);
        Assert.Equal(["1234567890"], result.CandidateValues);
        Assert.Equal("p21_mcp:get_warranty_by_serial", result.Source);
    }

    [Fact]
    public void MapWarrantyResult_NotFound_ReturnsNotFoundInWindow_NotSuppressed()
    {
        var structured = Parse("""{"status":"notFound"}""");

        var result = P21ValidationResultMapper.MapWarrantyResult(structured, "OLD1970SERIAL");

        Assert.NotNull(result);
        Assert.Equal(ValidationStatus.NotFoundInWindow, result!.Status);
        Assert.Equal(["OLD1970SERIAL"], result.CandidateValues);
    }

    [Fact]
    public void MapWarrantyResult_Unavailable_SuppressesNotification()
    {
        var structured = Parse("""{"status":"unavailable"}""");

        var result = P21ValidationResultMapper.MapWarrantyResult(structured, "irrelevant");

        Assert.Null(result);
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }
}
