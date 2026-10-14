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

    // OrderNumber/PurchaseOrderNumber/JobName are keyword-triggered only, not shape-matched --
    // unlike a digit-by-digit spoken identifier, "order 4521" or "job Smith Retrofit" doesn't fit
    // IsValidRun/IdentifierShapePattern at all (too few tokens, non-numeric words). Lower
    // confidence than the shape match since there's no structural signal backing it up, only a
    // keyword. First-pass keyword list/capture width -- no real call transcripts to validate
    // phrasing against yet; expect to tune both once this is live-tested. Ordered most-specific
    // first (e.g. "purchase order" before bare "order") so a more specific trigger claims a span
    // before a broader one gets a chance at it -- see ExtractKeywordTriggeredCandidates.
    private const double KeywordMatchConfidence = 0.5;
    private const int MaxLinkingWordsToSkip = 3;

    // RequireNumericLeadValue: Order/PO numbers are expected to actually be numbers, so the value
    // capture stops at the first token that doesn't look numeric -- this both trims trailing
    // words a naive "grab N tokens" would sweep in ("order number 45210 has shipped" -> "45210",
    // not "45210 has") and rejects the common false-positive "in order to ..." (the token right
    // after "order" isn't numeric, so nothing is captured and the candidate is dropped). Job names
    // are ordinary words, so they don't get this filter.
    private static readonly (string[] KeywordTokens, IdentifierTypeGuess Guess, int TokenCaptureCount, bool RequireNumericLeadValue)[] KeywordTriggerGroups =
    [
        (["purchase", "order"], IdentifierTypeGuess.PurchaseOrderNumber, 3, true),
        (["po", "#"], IdentifierTypeGuess.PurchaseOrderNumber, 3, true),
        (["po"], IdentifierTypeGuess.PurchaseOrderNumber, 3, true),
        (["order", "number"], IdentifierTypeGuess.OrderNumber, 3, true),
        (["order"], IdentifierTypeGuess.OrderNumber, 3, true),
        (["job", "name"], IdentifierTypeGuess.JobName, 4, false),
        (["job", "number"], IdentifierTypeGuess.JobName, 3, true),
        (["job"], IdentifierTypeGuess.JobName, 4, false),
    ];

    // Linking words skipped between a keyword and its value ("order NUMBER IS 4521", "purchase
    // order NUMBER 8834521") -- "number" is only a linking word here, distinct from its use as
    // part of the "order number"/"job number" keyword phrases themselves (which are matched as a
    // whole before this skip ever runs).
    private static readonly HashSet<string> LinkingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "was", "are", "number", "of", "#", ":", "-", "the"
    };

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
                : IdentifierTypeGuess.ModelOrPart;

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

        results.AddRange(ExtractKeywordTriggeredCandidates(windowText, tokens));

        return results;
    }

    /// <summary>
    /// Independent pass for OrderNumber/PurchaseOrderNumber/JobName -- these don't fit the
    /// digit-run shape the main loop above looks for, so this scans the same token stream for a
    /// trigger phrase and captures the tokens immediately following it, verbatim (joined with
    /// single spaces, not the digit-by-digit concatenation trick). A token span claimed by one
    /// trigger group can't also be claimed by a later, less-specific one (see KeywordTriggerGroups'
    /// ordering).
    /// </summary>
    private static List<IdentifierCandidate> ExtractKeywordTriggeredCandidates(string windowText, List<Token> tokens)
    {
        var results = new List<IdentifierCandidate>();
        var claimed = new bool[tokens.Count];

        foreach (var (keywordTokens, guess, tokenCaptureCount, requireNumericLeadValue) in KeywordTriggerGroups)
        {
            for (var position = 0; position <= tokens.Count - keywordTokens.Length; position++)
            {
                if (claimed[position] || !TokensMatchAt(tokens, position, keywordTokens))
                {
                    continue;
                }

                var valueStart = position + keywordTokens.Length;
                var skipped = 0;
                while (valueStart < tokens.Count && skipped < MaxLinkingWordsToSkip
                    && LinkingWords.Contains(tokens[valueStart].Text))
                {
                    valueStart++;
                    skipped++;
                }

                var valueTokens = new List<Token>();
                for (var cursor = valueStart; cursor < tokens.Count && valueTokens.Count < tokenCaptureCount; cursor++)
                {
                    if (requireNumericLeadValue && !NumericLeadPattern().IsMatch(tokens[cursor].Text))
                    {
                        break;
                    }

                    valueTokens.Add(tokens[cursor]);
                }

                if (valueTokens.Count == 0)
                {
                    continue;
                }

                var canonicalValue = string.Join(' ', valueTokens.Select(t => t.Text)).Trim();
                if (canonicalValue.Length < 2)
                {
                    continue;
                }

                var firstToken = tokens[position];
                var lastToken = valueTokens[^1];
                var rawSpokenText = windowText[firstToken.Index..(lastToken.Index + lastToken.Length)];
                var (contextSnippet, contextOffsetStart) = ExtractContext(windowText, firstToken.Index, rawSpokenText.Length);

                results.Add(new IdentifierCandidate
                {
                    CanonicalValue = canonicalValue,
                    RawSpokenText = rawSpokenText,
                    TypeGuess = guess,
                    ContextSnippet = contextSnippet,
                    Confidence = KeywordMatchConfidence,
                    ContextOffsetStart = contextOffsetStart,
                    ContextOffsetLength = rawSpokenText.Length
                });

                var claimEnd = valueStart + valueTokens.Count;
                for (var i = position; i < claimEnd; i++)
                {
                    claimed[i] = true;
                }
            }
        }

        return results;
    }

    private static bool TokensMatchAt(List<Token> tokens, int position, string[] keywordTokens)
    {
        for (var i = 0; i < keywordTokens.Length; i++)
        {
            if (!string.Equals(tokens[position + i].Text, keywordTokens[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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

    // A token that looks like (part of) a number -- starts with a digit, otherwise alnum/hyphen.
    // Used to bound keyword-triggered Order/PO value capture to the actual number.
    [GeneratedRegex(@"^[0-9][0-9A-Za-z\-]*$")]
    private static partial Regex NumericLeadPattern();
}
