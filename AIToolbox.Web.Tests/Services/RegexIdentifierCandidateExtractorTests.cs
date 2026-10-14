using AIToolbox.Web.Models;
using AIToolbox.Web.Services.CallEntityExtraction;

namespace AIToolbox.Web.Tests.Services;

public sealed class RegexIdentifierCandidateExtractorTests
{
    private readonly RegexIdentifierCandidateExtractor _extractor = new();

    [Fact]
    public void ExtractCandidates_CollapsesPauseInsertedSpaces()
    {
        var candidates = _extractor.ExtractCandidates("the model number is 24 ABB 324 ABN 3 if that helps");

        var candidate = Assert.Single(candidates);
        Assert.Equal("24ABB324ABN3", candidate.CanonicalValue);
        Assert.Equal("24 ABB 324 ABN 3", candidate.RawSpokenText);
    }

    [Fact]
    public void ExtractCandidates_DefaultsToModelOrPartWithoutSerialContext()
    {
        var candidates = _extractor.ExtractCandidates("the model number is 24 ABB 324 ABN 3 if that helps");

        var candidate = Assert.Single(candidates);
        Assert.Equal(IdentifierTypeGuess.ModelOrPart, candidate.TypeGuess);
    }

    [Fact]
    public void ExtractCandidates_GuessesSerialWhenContextMentionsSerial()
    {
        var candidates = _extractor.ExtractCandidates("the serial number on the unit is 24 ABB 324 ABN 3 let me read it");

        var candidate = Assert.Single(candidates);
        Assert.Equal(IdentifierTypeGuess.Serial, candidate.TypeGuess);
    }

    [Fact]
    public void ExtractCandidates_RejectsPlainWordsAndPlainNumbers()
    {
        var candidates = _extractor.ExtractCandidates("we spoke for about twenty five minutes total today");

        Assert.Empty(candidates);
    }

    [Fact]
    public void ExtractCandidates_IgnoresBlankInput()
    {
        Assert.Empty(_extractor.ExtractCandidates(string.Empty));
        Assert.Empty(_extractor.ExtractCandidates("   "));
    }

    [Fact]
    public void ExtractCandidates_DetectsOrderNumberFromKeyword()
    {
        var candidates = _extractor.ExtractCandidates("your order number 45210 has shipped");

        var candidate = Assert.Single(candidates, c => c.TypeGuess == IdentifierTypeGuess.OrderNumber);
        Assert.Equal("45210", candidate.CanonicalValue);
    }

    [Fact]
    public void ExtractCandidates_DetectsJobNameFromKeyword()
    {
        var candidates = _extractor.ExtractCandidates("this is for the job Smith Retrofit");

        var candidate = Assert.Single(candidates, c => c.TypeGuess == IdentifierTypeGuess.JobName);
        Assert.Equal("Smith Retrofit", candidate.CanonicalValue);
    }

    [Fact]
    public void ExtractCandidates_PrefersPurchaseOrderOverBareOrderKeyword()
    {
        var candidates = _extractor.ExtractCandidates("the purchase order number is 4521 for this shipment");

        var candidate = Assert.Single(candidates, c => c.TypeGuess == IdentifierTypeGuess.PurchaseOrderNumber);
        Assert.Equal("4521", candidate.CanonicalValue);
        Assert.DoesNotContain(candidates, c => c.TypeGuess == IdentifierTypeGuess.OrderNumber);
    }

    [Fact]
    public void ExtractCandidates_RejectsBareOrderKeywordWithoutNumericValue()
    {
        var candidates = _extractor.ExtractCandidates("I'll hold off on it in order to double check with the tech");

        Assert.DoesNotContain(candidates, c => c.TypeGuess == IdentifierTypeGuess.OrderNumber);
    }
}
