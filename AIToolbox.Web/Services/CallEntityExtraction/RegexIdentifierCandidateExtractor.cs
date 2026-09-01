using System.Text.RegularExpressions;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Generalizes the space-stripping trick already proven in Carrier.Tasks/CarrierProductTasks.cs
/// (.Trim().Replace(" ", string.Empty) on an isolated identifier parameter) to free text: Azure
/// STT inserts spaces at pause points when a caller reads an identifier aloud digit-by-digit
/// (e.g. "24 ABB 324 ABN 3" -> "24ABB324ABN3"). Token-shape alone (short alnum runs) isn't a
/// strong enough filter -- ordinary short English words ("is", "if", "on", "that") satisfy the
/// same shape, so a plain sentence produces spurious matches. Two extra signals narrow this down:
///   1. no token in a candidate run may be a common short English word (stoplist below);
///   2. a candidate run must contain at least 2 purely-numeric tokens -- a real spoken identifier
///      read out this way is digit-heavy, ordinary short-word runs essentially never are.
/// This is a heuristic pre-filter, not the correctness backstop -- P21 validation is. False
/// negatives here just mean a candidate skips straight to being noise the fuzzy-match/validation
/// layers never see; false positives cost one wasted validation call, not a wrong notification.
/// </summary>
public sealed partial class RegexIdentifierCandidateExtractor : IIdentifierCandidateExtractor
{
    private const double ShapeMatchConfidence = 0.75;
    private const int ContextRadiusChars = 60;
    private const int MinRunLength = 2;
    private const int MaxRunLength = 6;
    private const int MinDigitOnlyTokens = 2;

    private static readonly string[] SerialContextWords = ["serial"];

    // Common short (<=4 char) English words that would otherwise satisfy the token-shape check.
    // Not exhaustive by design -- it only needs to cover words likely to sit next to a spoken
    // identifier in ordinary call speech, not be a complete stopword corpus.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "it", "if", "in", "on", "at", "to", "of", "or", "and", "but", "so",
        "we", "he", "she", "you", "your", "his", "her", "its", "our", "their", "my", "me", "us",
        "was", "are", "has", "had", "for", "as", "by", "up", "out", "do", "did", "no", "not",
        "that", "this", "with", "let", "can", "got", "get", "just", "ok", "okay", "yes", "yep",
        "one", "two", "all", "any", "how", "who", "why", "now", "here", "there", "then", "than",
        "be", "am", "will", "would", "could", "should", "have", "been", "from", "into", "over"
    };

    public IReadOnlyList<IdentifierCandidate> ExtractCandidates(string windowText)
    {
        if (string.IsNullOrWhiteSpace(windowText))
        {
            return [];
        }

        var tokens = WordPattern().Matches(windowText)
            .Select(match => new Token(match.Value, match.Index, match.Length))
            .ToList();

        var results = new List<IdentifierCandidate>();
        var position = 0;
        while (position < tokens.Count)
        {
            var run = FindLongestValidRunAt(tokens, position);
            if (run is null)
            {
                position++;
                continue;
            }

            var (runTokens, canonicalValue) = run.Value;
            var firstToken = runTokens[0];
            var lastToken = runTokens[^1];
            var rawSpokenText = windowText[firstToken.Index..(lastToken.Index + lastToken.Length)];
            var (contextSnippet, contextOffsetStart) = ExtractContext(windowText, firstToken.Index, rawSpokenText.Length);
            var typeGuess = LooksLikeSerialContext(contextSnippet)
                ? IdentifierTypeGuess.Serial
                : IdentifierTypeGuess.ModelOrItem;

            results.Add(new IdentifierCandidate
            {
                CanonicalValue = canonicalValue,
                RawSpokenText = rawSpokenText,
                TypeGuess = typeGuess,
                ContextSnippet = contextSnippet,
                Confidence = ShapeMatchConfidence,
                ContextOffsetStart = contextOffsetStart,
                ContextOffsetLength = rawSpokenText.Length
            });

            position += runTokens.Count;
        }

        return results;
    }

    private static (List<Token> RunTokens, string CanonicalValue)? FindLongestValidRunAt(List<Token> tokens, int start)
    {
        var maxLength = Math.Min(MaxRunLength, tokens.Count - start);
        for (var length = maxLength; length >= MinRunLength; length--)
        {
            var run = tokens.GetRange(start, length);
            if (!IsValidRun(run))
            {
                continue;
            }

            var canonicalValue = string.Concat(run.Select(token => token.Text));
            if (IdentifierShapePattern().IsMatch(canonicalValue))
            {
                return (run, canonicalValue);
            }
        }

        return null;
    }

    private static bool IsValidRun(List<Token> run)
    {
        var digitOnlyCount = 0;
        foreach (var token in run)
        {
            if (!TokenPrimitivePattern().IsMatch(token.Text) || StopWords.Contains(token.Text))
            {
                return false;
            }

            if (DigitOnlyPattern().IsMatch(token.Text))
            {
                digitOnlyCount++;
            }
        }

        return digitOnlyCount >= MinDigitOnlyTokens;
    }

    private static (string Snippet, int MatchOffsetInSnippet) ExtractContext(string windowText, int matchIndex, int matchLength)
    {
        var start = Math.Max(0, matchIndex - ContextRadiusChars);
        var end = Math.Min(windowText.Length, matchIndex + matchLength + ContextRadiusChars);
        return (windowText[start..end], matchIndex - start);
    }

    private static bool LooksLikeSerialContext(string contextSnippet)
    {
        return SerialContextWords.Any(word =>
            contextSnippet.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct Token(string Text, int Index, int Length);

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordPattern();

    // A single spoken-out token: short, alnum only.
    [GeneratedRegex(@"^[A-Za-z0-9]{1,4}$")]
    private static partial Regex TokenPrimitivePattern();

    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex DigitOnlyPattern();

    // Collapsed candidate must mix letters and digits and be a plausible identifier length.
    [GeneratedRegex(@"^(?=.*[A-Za-z])(?=.*[0-9])[A-Za-z0-9]{5,20}$")]
    private static partial Regex IdentifierShapePattern();
}
