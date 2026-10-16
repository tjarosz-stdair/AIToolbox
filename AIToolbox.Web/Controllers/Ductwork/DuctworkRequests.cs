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

public sealed record EnqueueTaskRequest(string? TaskType, string? InputManifestRef, long? VisionApprovalId);

public sealed record TakeoverResponseRequest(bool HandOver);

public sealed record BeginUploadRequest(string? FileName, long TotalBytes, string? Sha256);

public sealed record BindTaskRequest(string? TaskType, long ProducerId, int EvalRunId, bool SendsDrawingContent, int MaxAttempts);

// ---- worker

public sealed record WorkerHandshakeRequest(string? WorkerName, string? ContractVersion, string[]? TaskTypes);

public sealed record WorkerClaimRequest(string? WorkerName, string[]? TaskTypes);

public sealed record WorkerHeartbeatRequest(string? WorkerName, int Done, int? Total, string? Message);

public sealed record WorkerFailRequest(string? WorkerName, string? Error, bool Retryable);

// ---- review

public sealed record EditEntityRequest(int ExpectedRevision, string? Reason, string? Type, string? Category, string? Tag, string? Size, string? Manufacturer, string? Model,
    string? CustomerPartCode, decimal? Quantity, string? Unit, long? BuildingTypeId, bool ClearBuildingType, long? SheetViewId, bool ClearSheetView);

public sealed record SetReviewRequest(string? Status, int ExpectedRevision, string? Reason);

public sealed record SetDispositionRequest(string? Disposition, int ExpectedRevision, string? Reason);

public sealed record ReviewManyRequest(Guid[]? EntityIds, string? Status, string? Reason);

public sealed record AddEntityRequest(string? Type, string? Category, string? Tag, string? Size, string? Manufacturer, string? Model, string? CustomerPartCode, decimal? Quantity,
    string? Unit, long? BuildingTypeId, long? SheetViewId, string? Disposition, string? DispositionReason, double[]? Box, string? Evidence);

// ---- sheets and views

public sealed record SheetBody(int PageNo, double WidthPts, double HeightPts, int Rotation, string? SheetNumber, string? Title, string? Discipline, string? Kind,
    string? ContentHash, bool? HasTextLayer);

public sealed record RegisterSheetsRequest(SheetBody[]? Sheets);

public sealed record UpdateSheetRequest(string? SheetNumber, string? Title, string? Discipline, string? Kind, bool? InScope);

public sealed record CreateViewRequest(string? Title, string? Floor, string? Type, long? BuildingTypeId, double[][]? Polygon);

public sealed record UpdateViewRequest(string? Title, string? Floor, string? Type, long? BuildingTypeId, bool ClearBuildingType, double[][]? Polygon, bool ClearPolygon);

public sealed record SetScaleRequest(double Ratio, string? Method, System.Text.Json.JsonElement? Calibration);

public sealed record AddRegionRequest(string? Kind, string? Reason, double[][]? Polygon);

public sealed record WorkerSheetsRequest(string? WorkerName, Guid FileId, SheetBody[]? Sheets);
