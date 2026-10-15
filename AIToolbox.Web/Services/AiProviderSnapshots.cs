using AIToolbox.Ductwork.Core.Data;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services;

/// <summary>
/// Describes a provider as it is right now, for provenance. Provider rows are mutable (a model name
/// edit rewrites history), so Ductwork stores this snapshot with every result instead. It carries no
/// secret: no key, no extra headers, only what identifies the model and the route.
/// </summary>
public static class AiProviderSnapshots
{
    public static ProviderSnapshotInfo ToSnapshot(this AiProvider provider)
    {
        if (!Uri.TryCreate(provider.EndpointUrl, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException($"Provider '{provider.DisplayName}' has an endpoint URL that is not absolute.");

        return new ProviderSnapshotInfo(
            ProviderType: (int)provider.ProviderType,
            EndpointHost: endpoint.Host,
            ModelName: provider.ModelName,
            ApiVersion: provider.ApiVersion,
            MaxOutputTokens: provider.MaxOutputTokens,
            SupportsVision: provider.SupportsVision,
            GovernanceRoute: provider.GovernanceRoute);
    }

    /// <summary>
    /// A provider may carry Ductwork work only when an administrator has allowed it, it is enabled,
    /// and, if the work needs image input, it is vision-enabled and on a governed route.
    /// </summary>
    public static string? WhyNotUsableForDuctwork(this AiProvider provider, bool needsVision)
    {
        if (!provider.IsEnabled) return "The provider is disabled.";
        if (!provider.AllowedForDuctwork) return "The provider has not been allowed for Ductwork.";
        if (needsVision && !provider.SupportsVision) return "The provider is not vision-enabled.";
        if (needsVision && string.IsNullOrWhiteSpace(provider.GovernanceRoute))
            return "Vision work requires a provider with a governance route (for example Microsoft Foundry).";
        return null;
    }
}
