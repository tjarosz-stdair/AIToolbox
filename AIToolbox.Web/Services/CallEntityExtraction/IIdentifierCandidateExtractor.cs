using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

public interface IIdentifierCandidateExtractor
{
    IReadOnlyList<IdentifierCandidate> ExtractCandidates(string windowText);
}
