namespace AIToolbox.Web.Models;

public static class EventLogTypes
{
    public const string UserLogin = "UserLogin";
    public const string AiSubmission = "AiSubmission";
}

public sealed class EventLogEntry
{
    public int Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string? UserEmail { get; set; }

    public string? DisplayName { get; set; }

    public string? TenantId { get; set; }

    public string? ToolKey { get; set; }

    public int? AiProviderId { get; set; }

    public string? AiProviderName { get; set; }

    public string? Operation { get; set; }

    public int? DurationMs { get; set; }

    public bool? Succeeded { get; set; }

    public bool IsNamedUser { get; set; }

    public bool IsAdmin { get; set; }

    public bool WasAllowed { get; set; }

    public string? FailureReason { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public string? MetadataJson { get; set; }

    public DateTime CreatedUtc { get; set; }
}

public sealed class UserLoginEventFilter
{
    public string UserType { get; set; } = "all";

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

public sealed class UserLoginEventResult
{
    public IReadOnlyList<EventLogEntry> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;

    public string UserType { get; set; } = "all";

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed class AiSubmissionEventFilter
{
    public string ToolKey { get; set; } = "all";

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

public sealed class AiSubmissionEventResult
{
    public IReadOnlyList<EventLogEntry> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;

    public string ToolKey { get; set; } = "all";

    public IReadOnlyList<string> ToolKeys { get; set; } = [];

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
