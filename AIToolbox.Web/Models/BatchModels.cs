namespace AIToolbox.Web.Models;

public static class AiBatchStatus
{
    public const string Queued = "Queued";
    public const string Preparing = "Preparing";
    public const string Submitted = "Submitted";
    public const string WaitingOnProvider = "WaitingOnProvider";
    public const string RetrievingResults = "RetrievingResults";
    public const string Succeeded = "Succeeded";
    public const string PartiallySucceeded = "PartiallySucceeded";
    public const string Failed = "Failed";
    public const string CancelRequested = "CancelRequested";
    public const string Cancelling = "Cancelling";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
}

public sealed class ApiConsumer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? OwnerEmail { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public IReadOnlyList<ApiConsumerKey> Keys { get; set; } = [];
}

public sealed class ApiConsumerKey
{
    public int Id { get; set; }
    public int ApiConsumerId { get; set; }
    public string ConsumerName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string HashAlgorithm { get; set; } = "SHA256";
    public string ScopesJson { get; set; } = "[]";
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public bool IsActive => RevokedUtc is null && (ExpiresUtc is null || ExpiresUtc > DateTime.UtcNow);
}

public sealed class ApiConsumerPrincipal
{
    public required ApiConsumer Consumer { get; set; }
    public required ApiConsumerKey Key { get; set; }
    public IReadOnlyList<string> Scopes { get; set; } = [];
}

public sealed class AiBatchJob
{
    public int Id { get; set; }
    public string ToolKey { get; set; } = string.Empty;
    public int? ConsumerId { get; set; }
    public string? ConsumerName { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public int AiProviderId { get; set; }
    public string? AiProviderName { get; set; }
    public AiProviderType? ProviderType { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = AiBatchStatus.Queued;
    public string InputMode { get; set; } = "json";
    public string? RequestJson { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ProviderBatchId { get; set; }
    public string? ProviderInputFileId { get; set; }
    public string? ProviderOutputFileId { get; set; }
    public string? ProviderErrorFileId { get; set; }
    public string? ProviderResultsUrl { get; set; }
    public int TotalRequestCount { get; set; }
    public int SucceededRequestCount { get; set; }
    public int FailedRequestCount { get; set; }
    public int CanceledRequestCount { get; set; }
    public int ExpiredRequestCount { get; set; }
    public string? CallbackUrl { get; set; }
    public string? CallbackSecretCipherText { get; set; }
    public string? CallbackStatus { get; set; }
    public int CallbackAttemptCount { get; set; }
    public DateTime? LastCallbackAttemptUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }
    public DateTime? LastPolledUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
}

public sealed class AiBatchRequest
{
    public int Id { get; set; }
    public int AiBatchJobId { get; set; }
    public string CustomId { get; set; } = string.Empty;
    public int SequenceNumber { get; set; }
    public string? SourceObjectType { get; set; }
    public string? SourceObjectId { get; set; }
    public string Status { get; set; } = AiBatchStatus.Queued;
    public string RequestJson { get; set; } = "{}";
    public string? ResponseJson { get; set; }
    public string? ErrorJson { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string? ProviderRequestId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public sealed class AiBatchJobDetail
{
    public required AiBatchJob Job { get; set; }
    public IReadOnlyList<AiBatchRequest> Requests { get; set; } = [];
}
