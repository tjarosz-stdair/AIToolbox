namespace AIToolbox.Web.Controllers.Ductwork;

// Request bodies. Enum-like values arrive as strings and are parsed with a clear error, so a typo is a 400 that
// lists the valid names instead of a silent default.

public sealed record CreateJobRequest(string? Name, string? CustomerName, string? ProjectName, string? SiteAddress, string? ReviewLevel);

public sealed record UpdateDetailsRequest(string? Name, string? CustomerName, string? ProjectName, string? SiteAddress, string[]? Clear, long ExpectedStateVersion);

public sealed record SetReviewLevelRequest(string? ReviewLevel);

public sealed record SetBaselineRequest(decimal? Hours);

public sealed record SetStepRequest(string? Status);

public sealed record ReasonRequest(string? Reason);

public sealed record AddMemberRequest(string? Email, string? Role);

public sealed record ChangeRoleRequest(string? Role);

public sealed record TagRequest(string? Tag);

public sealed record ActivityHeartbeatRequest(string? State, string? Step, string? Surface);

public sealed record CreateBuildingTypeRequest(string? Name, string? Kind, string? Notes);

public sealed record UpdateBuildingTypeRequest(string? Name, string? Kind, string? Notes);

public sealed record SetMultiplierRequest(int? Multiplier, string? Source, string? Evidence);

public sealed record EnqueueTaskRequest(string? TaskType, string? InputManifestRef, Guid? VisionApprovalId);

public sealed record TakeoverResponseRequest(bool HandOver);

public sealed record BeginUploadRequest(string? FileName, long TotalBytes, string? Sha256);

public sealed record BindTaskRequest(string? TaskType, long ProducerId, int EvalRunId, bool SendsDrawingContent, int MaxAttempts);

// ---- worker

public sealed record WorkerHandshakeRequest(string? WorkerName, string? ContractVersion, string[]? TaskTypes, string? WorkerVersion = null, string? HostName = null, int? MaxConcurrency = null);

public sealed record WorkerClaimRequest(string? WorkerName, string[]? TaskTypes);

public sealed record WorkerHeartbeatRequest(string? WorkerName, int Done, int? Total, string? Message);

public sealed record WorkerFailRequest(string? WorkerName, string? Error, bool Retryable);

// ---- review

public sealed record EditEntityRequest(int ExpectedRevision, string? Reason, string? Type, string? Category, string? Tag, string? Size, string? Manufacturer, string? Model,
    string? CustomerPartCode, decimal? Quantity, string? Unit, long? BuildingTypeId, bool ClearBuildingType, Guid? ViewId, bool ClearSheetView);

public sealed record SetReviewRequest(string? Status, int ExpectedRevision, string? Reason);

public sealed record SetDispositionRequest(string? Disposition, int ExpectedRevision, string? Reason);

public sealed record ReviewManyRequest(Guid[]? EntityIds, string? Status, string? Reason);

public sealed record AddEntityRequest(string? Type, string? Category, string? Tag, string? Size, string? Manufacturer, string? Model, string? CustomerPartCode, decimal? Quantity,
    string? Unit, long? BuildingTypeId, Guid? ViewId, string? Disposition, string? DispositionReason, double[]? Box, string? Evidence);

// ---- workers

public sealed record DrainWorkerRequest(bool Drain);

public sealed record CreateWorkerKeyRequest(string? WorkerName);

// ---- hosted vision

public sealed record VisionDisclosureRequest(Guid[]? SheetIds, int ProviderId);

public sealed record CreateVisionApprovalRequest(Guid[]? SheetIds, int ProviderId, string? Reason, int AcknowledgedNoticeVersion);

public sealed record RevokeVisionApprovalRequest(string? Reason);

public sealed record VisionPolicyBody(bool Enabled, int[]? AllowedProviderIds, string? ApproverRule, string? NoticeText, int MaxPagesPerApproval, int MaxImagesPerPage, bool RequireReason,
    int ApprovalLifetimeMinutes, string[]? StandingTaskTypes, int StandingMaxPagesPerJob);

public sealed record VisionImageBody(string? Label, string? MediaType, string? DataBase64);

public sealed record WorkerVisionRequest(string? WorkerName, string? Directive, Guid[]? SheetIds, VisionImageBody[]? Images);

// ---- rules and findings

public sealed record RuleBody(string? Name, string? Description, string? Scope, long? BuildingTypeId, Guid? SheetId,
    AIToolbox.Ductwork.Core.Rules.RuleMatch? Match, AIToolbox.Ductwork.Core.Rules.RuleAction? Action, Guid? FromFindingId);

public sealed record RetireRuleRequest(string? Reason);

public sealed record ResolveFindingRequest(string? Kind, Guid? RuleId, string? Note);

// ---- sheets and views

public sealed record SheetBody(int PageNo, double WidthPts, double HeightPts, int Rotation, string? SheetNumber, string? Title, string? Discipline, string? Kind,
    string? ContentHash, bool? HasTextLayer, ViewBody[]? Views = null);

/// <summary>A view a reader found on a page: its title (with its number if it has one), kind, printed scale as a ratio, and area.</summary>
public sealed record ViewBody(string? Title, string? Number, string? Type, double? ScaleRatio, double[][]? Polygon);

public sealed record RegisterSheetsRequest(SheetBody[]? Sheets);

public sealed record UpdateSheetRequest(string? SheetNumber, string? Title, string? Discipline, string? Kind, bool? InScope);

public sealed record CreateViewRequest(string? Title, string? Floor, string? Type, long? BuildingTypeId, double[][]? Polygon);

public sealed record UpdateViewRequest(string? Title, string? Floor, string? Type, long? BuildingTypeId, bool ClearBuildingType, double[][]? Polygon, bool ClearPolygon);

public sealed record SetScaleRequest(double Ratio, string? Method, System.Text.Json.JsonElement? Calibration);

public sealed record AddRegionRequest(string? Kind, string? Reason, double[][]? Polygon);

public sealed record WorkerSheetsRequest(string? WorkerName, Guid FileId, SheetBody[]? Sheets);

public sealed record ArchiveJobRequest(string? Kind, string? ConfirmName);

public sealed record RelinkArchiveRequest(string? StorageKey);

public sealed record GrantJobManagerRequest(string? Email);
