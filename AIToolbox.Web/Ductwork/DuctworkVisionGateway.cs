using System.Security.Cryptography;
using AIToolbox.Ductwork.Core.Jobs;
using AIToolbox.Ductwork.Core.Llm;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services;

namespace AIToolbox.Web.Ductwork;

public sealed record VisionImageInput(string? Label, string MediaType, string DataBase64);

public sealed record VisionSendInput(string Directive, IReadOnlyList<Guid> SheetIds, IReadOnlyList<VisionImageInput> Images);

public sealed record VisionSendOutput(string? Text, string? FinishReason, int? InputTokens, int? OutputTokens, long TransmissionId, int ImagesSent, string Provider, string Model);

/// <summary>The approved model was reached but could not answer. The images may have been disclosed, so they stay counted against the approval.</summary>
public sealed class VisionProviderException(string message) : Exception(message);

/// <summary>
/// The one place drawing content leaves for a hosted model (plan section 6.12). The worker holds no keys and cannot call a
/// provider: it asks here, and a send happens only after <see cref="VisionRepository"/> has checked the policy, the approval,
/// the task, the sheets and the budget, and has reserved the images. The provider used is the one the approval was given for,
/// found by its snapshot: if the provider was edited since (a different model or route), nothing is sent.
/// </summary>
public sealed class DuctworkVisionGateway(VisionRepository vision, IAiProviderRepository providers, IAiProviderClient client)
{
    private const int MaxDirectiveChars = 20_000;

    public async Task<VisionSendOutput> SendAsync(long taskId, AuditContext worker, VisionSendInput input, CancellationToken cancellationToken)
    {
        // What can be checked without touching the approval is checked first, so a bad request reserves nothing.
        if (string.IsNullOrWhiteSpace(input.Directive) || input.Directive.Length > MaxDirectiveChars)
            throw new VisionRefusedException("bad-request", $"The instruction must be 1 to {MaxDirectiveChars:N0} characters.");
        if (input.Images.Count is 0 or > ImagePromptLimits.MaxImagesPerRequest)
            throw new VisionRefusedException("bad-request", $"Send between 1 and {ImagePromptLimits.MaxImagesPerRequest} images at a time.");

        var images = new List<ImagePromptImage>();
        long total = 0;
        foreach (var image in input.Images)
        {
            if (!ImagePromptLimits.AllowedMediaTypes.Contains(image.MediaType ?? ""))
                throw new VisionRefusedException("bad-request", $"'{image.MediaType}' is not an image type that can be sent.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(image.DataBase64 ?? ""); }
            catch (FormatException) { throw new VisionRefusedException("bad-request", "An image is not valid base64."); }
            if (bytes.Length is 0 or > ImagePromptLimits.MaxImageBytes)
                throw new VisionRefusedException("bad-request", $"Each image must be between 1 byte and {ImagePromptLimits.MaxImageBytes / 1048576} MB.");
            total += bytes.Length;
            images.Add(new ImagePromptImage { Bytes = bytes, MediaType = image.MediaType!, Label = image.Label });
        }

        var grant = await vision.AuthorizeSendAsync(taskId, worker, input.SheetIds, images.Count, total, input.Directive, cancellationToken);

        AiProvider? provider = null;
        foreach (var candidate in await providers.GetAllAsync(includeDisabled: false, cancellationToken))
        {
            if (!grant.AllowedProviderIds.Contains(candidate.Id) || candidate.WhyNotUsableForDuctwork(needsVision: true) is not null) continue;
            if (candidate.ToSnapshot().ComputeHash() == grant.ProviderSnapshotHash) { provider = candidate; break; }
        }

        if (provider is null)
        {
            await vision.CompleteSendAsync(grant.TransmissionId, sent: false, leftTheServer: false, "No allowed provider matches the approved model.", worker, CancellationToken.None);
            throw new VisionRefusedException("provider", "The model this approval was given for is no longer available or allowed, so nothing was sent.");
        }

        DocumentPromptResult result;
        try
        {
            result = await client.SendImagePromptAsync(new ImagePromptRequest { Provider = provider, Images = images, Directive = input.Directive }, cancellationToken);
        }
        catch (ImagePromptRejectedException ex)
        {
            // Refused before any network call, so nothing was disclosed and the images go back to the budget.
            await vision.CompleteSendAsync(grant.TransmissionId, sent: false, leftTheServer: false, ex.Message, worker, CancellationToken.None);
            throw new VisionRefusedException("image-rejected", ex.Message);
        }
        catch (Exception ex)
        {
            // It may have reached the provider before failing, so the images stay counted.
            await vision.CompleteSendAsync(grant.TransmissionId, sent: false, leftTheServer: true, ex is OperationCanceledException ? "Cancelled." : ex.Message, worker, CancellationToken.None);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            await vision.CompleteSendAsync(grant.TransmissionId, sent: false, leftTheServer: true, result.ErrorMessage, worker, CancellationToken.None);
            throw new VisionProviderException(result.ErrorMessage);
        }

        await vision.CompleteSendAsync(grant.TransmissionId, sent: true, leftTheServer: true, null, worker, CancellationToken.None);
        return new VisionSendOutput(result.ResponseText, result.FinishReason, result.InputTokens, result.OutputTokens, grant.TransmissionId, images.Count, provider.DisplayName, provider.ModelName);
    }
}
