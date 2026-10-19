using System.Text.Json;
using System.Text.Json.Serialization;
using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Worker;

/// <summary>How the worker reads and writes the server's JSON: camelCase, enums as strings, unknown members ignored so a newer server does not break an older worker.</summary>
public static class WorkerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record HandshakeInfo(string ServerContractVersion, int LeaseSeconds, int HeartbeatSeconds, int LockHeartbeatSeconds, long MaxResultBytes);

public sealed record ClaimedTask(
    long TaskId, Guid TaskPublicId, long JobId, string TaskType, int Attempt, int MaxAttempts, string? InputManifestRef, string ContractVersion,
    DateTime LeaseExpiresUtc, ProducerRef Producer);

public sealed record HeartbeatReply(bool Continue, bool CancelRequested);

public sealed record InputFile(Guid FileId, string FileName, long Bytes, string Sha256, int Revision, Guid? DuplicateOf);

public sealed record InputView(Guid ViewId, string Title, long? BuildingTypeId, double? ScaleRatio);

public sealed record InputSheet(
    Guid SheetId, Guid FileId, int PageNo, string? SheetNumber, string? Title, string Kind, bool InScope, double WidthPts, double HeightPts, int Rotation,
    Guid? DuplicateOf, IReadOnlyList<InputView> Views, string? Discipline = null);

public sealed record InputBuildingType(long BuildingTypeId, string Name);

/// <summary>What a task is about. The files are the job's own; the worker is told nothing about any other job.</summary>
public sealed record TaskInput(
    long TaskId, long JobId, string TaskType, string? InputManifestRef, IReadOnlyList<InputFile> Files, IReadOnlyList<InputSheet> Sheets,
    IReadOnlyList<InputBuildingType> BuildingTypes);

public sealed record SubmitReply(bool Accepted, bool Cancelled, int Entities, int Findings, int BuildingTypesCreated, int RaisedAsFindings, int Batched);

/// <summary>One page found in a drawing file, as the server records it.</summary>
public sealed record PageReport(
    int PageNo, double WidthPts, double HeightPts, int Rotation = 0, string? SheetNumber = null, string? Title = null, string? Discipline = null,
    string? Kind = null, string? ContentHash = null, bool HasTextLayer = true);

public sealed record RegisterSheetsReply(int Recorded, int Duplicates);

/// <summary>An image a handler asks the server to show to the hosted model its task was approved for.</summary>
public sealed record VisionImage(string? Label, string MediaType, byte[] Bytes);

public sealed record VisionReply(string? Text, string? FinishReason, int? InputTokens, int? OutputTokens, long TransmissionId, int ImagesSent, string Provider, string Model);

/// <summary>The server answered with an error the worker should understand rather than retry blindly.</summary>
public class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    /// <summary>True for answers that may succeed if asked again: the server was busy or unreachable, not unwilling.</summary>
    public bool IsTransient => Status is 408 or 429 or 500 or 502 or 503 or 504;
}

/// <summary>The server and this worker do not speak compatible contract versions. Nothing should be claimed until one of them is upgraded.</summary>
public sealed class ContractMismatchException(string serverVersion, string message) : ApiException(409, "contract-version", message)
{
    public string ServerVersion { get; } = serverVersion;
}

/// <summary>The server refused a result. Sending it again will not help; the reasons say what to fix.</summary>
public sealed class ResultRejectedApiException(IReadOnlyList<string> reasons, string message) : ApiException(422, "result-rejected", message)
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>
/// The server refused to send images to a hosted model: it is switched off, the approval is missing, expired or revoked, the sheets or the
/// budget are not covered, or the model changed. The reason is a short code. Asking again will not help until a person acts.
/// </summary>
public sealed class VisionRefusedApiException(string reason, string message) : ApiException(403, "vision-refused", message)
{
    public string Reason { get; } = reason;
}
