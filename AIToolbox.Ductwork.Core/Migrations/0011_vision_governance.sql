-- Hosted vision as an explicitly approved fallback (plan section 6.12). Sending drawing content to a hosted model is a
-- data-governance event, so it is off by default, approved per run for listed sheets only, expires, and is logged.
-- Additive: new tables, and a foreign key on a column that already existed and was never filled without an approval.

-- The administrator's settings, one row per change. The newest row is in force; nothing is edited in place. No row at all
-- means the defaults in code, which are "off".
CREATE TABLE ductwork.VisionPolicy
(
    VisionPolicyId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VisionPolicy PRIMARY KEY,
    Enabled                 BIT NOT NULL CONSTRAINT DF_VisionPolicy_Enabled DEFAULT (0),
    -- Ids of AIToolbox providers (dbo.AiProviders) that may receive drawings. Empty means none.
    AllowedProviderIdsJson  NVARCHAR(MAX) NOT NULL CONSTRAINT DF_VisionPolicy_Providers DEFAULT ('[]'),
    ApproverRule            VARCHAR(14) NOT NULL CONSTRAINT DF_VisionPolicy_Approver DEFAULT ('EditPermission'),
    NoticeText              NVARCHAR(4000) NOT NULL,
    NoticeVersion           INT NOT NULL,
    MaxPagesPerApproval     INT NOT NULL CONSTRAINT DF_VisionPolicy_MaxPages DEFAULT (10),
    MaxImagesPerPage        INT NOT NULL CONSTRAINT DF_VisionPolicy_ImagesPerPage DEFAULT (6),
    RequireReason           BIT NOT NULL CONSTRAINT DF_VisionPolicy_RequireReason DEFAULT (1),
    ApprovalLifetimeMinutes INT NOT NULL CONSTRAINT DF_VisionPolicy_Lifetime DEFAULT (60),
    -- Task types an administrator has approved in advance, up to a page limit per job. Empty by default.
    StandingTaskTypesJson   NVARCHAR(MAX) NOT NULL CONSTRAINT DF_VisionPolicy_Standing DEFAULT ('[]'),
    StandingMaxPagesPerJob  INT NOT NULL CONSTRAINT DF_VisionPolicy_StandingPages DEFAULT (0),
    ChangedBy               NVARCHAR(256) NOT NULL,
    ChangedUtc              DATETIME2(7) NOT NULL CONSTRAINT DF_VisionPolicy_ChangedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_VisionPolicy_Approver CHECK (ApproverRule IN ('EditPermission', 'JobManager', 'Admin')),
    CONSTRAINT CK_VisionPolicy_Limits CHECK (MaxPagesPerApproval >= 1 AND MaxImagesPerPage >= 1 AND ApprovalLifetimeMinutes >= 1 AND StandingMaxPagesPerJob >= 0),
    CONSTRAINT CK_VisionPolicy_Notice CHECK (LEN(LTRIM(RTRIM(NoticeText))) > 0 AND NoticeVersion >= 1)
);
GO

CREATE TRIGGER ductwork.TR_VisionPolicy_AppendOnly ON ductwork.VisionPolicy
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51016, 'The vision policy history is append-only. Change the policy by adding a new version.', 1;
END;
GO

-- One person's permission to send listed pages to one model for one run. It expires, covers only the sheets named, and is
-- used by one task. Standing approvals are issued by the policy itself for task types an administrator listed.
CREATE TABLE ductwork.VisionApproval
(
    VisionApprovalId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VisionApproval PRIMARY KEY,
    PublicId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_VisionApproval_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_VisionApproval_PublicId UNIQUE,
    JobId              BIGINT NOT NULL CONSTRAINT FK_VisionApproval_Job REFERENCES ductwork.Job (JobId),
    Kind               VARCHAR(8) NOT NULL CONSTRAINT DF_VisionApproval_Kind DEFAULT ('PerRun'),
    ApprovedBy         NVARCHAR(256) NOT NULL,
    ApprovedUtc        DATETIME2(7) NOT NULL CONSTRAINT DF_VisionApproval_ApprovedUtc DEFAULT (SYSUTCDATETIME()),
    ExpiresUtc         DATETIME2(7) NOT NULL,
    -- The public ids of the sheets this approval covers, as a JSON array of strings.
    SheetIdsJson       NVARCHAR(MAX) NOT NULL,
    PageCount          INT NOT NULL,
    -- Pages and the crops taken from them: the most images that may be sent under this approval.
    ImageBudget        INT NOT NULL,
    ImagesSent         INT NOT NULL CONSTRAINT DF_VisionApproval_ImagesSent DEFAULT (0),
    ProviderSnapshotId INT NOT NULL CONSTRAINT FK_VisionApproval_ProviderSnapshot REFERENCES ductwork.ProviderSnapshot (ProviderSnapshotId),
    NoticeVersion      INT NOT NULL,
    Reason             NVARCHAR(400) NULL,
    Status             VARCHAR(8) NOT NULL CONSTRAINT DF_VisionApproval_Status DEFAULT ('Active'),
    UsedByTaskId       BIGINT NULL CONSTRAINT FK_VisionApproval_Task REFERENCES ductwork.ProcessingTask (ProcessingTaskId),
    RevokedBy          NVARCHAR(256) NULL,
    RevokedUtc         DATETIME2(7) NULL,
    RevokeReason       NVARCHAR(400) NULL,
    CONSTRAINT CK_VisionApproval_Kind CHECK (Kind IN ('PerRun', 'Standing')),
    CONSTRAINT CK_VisionApproval_Status CHECK (Status IN ('Active', 'Used', 'Expired', 'Revoked')),
    CONSTRAINT CK_VisionApproval_Counts CHECK (PageCount >= 1 AND ImageBudget >= PageCount AND ImagesSent >= 0 AND ImagesSent <= ImageBudget),
    CONSTRAINT CK_VisionApproval_Expiry CHECK (ExpiresUtc > ApprovedUtc),
    -- A used approval says which task used it, and only an approval that was used has one.
    CONSTRAINT CK_VisionApproval_Used CHECK ((Status = 'Used' AND UsedByTaskId IS NOT NULL) OR (Status <> 'Used' AND (UsedByTaskId IS NULL OR Status IN ('Expired', 'Revoked')))),
    CONSTRAINT CK_VisionApproval_Revoked CHECK ((Status = 'Revoked' AND RevokedBy IS NOT NULL AND RevokedUtc IS NOT NULL) OR (Status <> 'Revoked' AND RevokedBy IS NULL))
);
CREATE INDEX IX_VisionApproval_Job ON ductwork.VisionApproval (JobId, Status);
GO

-- A task that sends drawings can now only point at a real approval.
ALTER TABLE ductwork.ProcessingTask ADD CONSTRAINT FK_ProcessingTask_VisionApproval FOREIGN KEY (VisionApprovalId) REFERENCES ductwork.VisionApproval (VisionApprovalId);
GO

-- Every time drawing content was, or was about to be, sent to a hosted model. Holds counts and hashes, never the images
-- or the prompt text. A row is created before the call (reserving the budget) and finished after it.
CREATE TABLE ductwork.VisionTransmission
(
    VisionTransmissionId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_VisionTransmission PRIMARY KEY,
    JobId                BIGINT NOT NULL CONSTRAINT FK_VisionTransmission_Job REFERENCES ductwork.Job (JobId),
    VisionApprovalId     BIGINT NOT NULL CONSTRAINT FK_VisionTransmission_Approval REFERENCES ductwork.VisionApproval (VisionApprovalId),
    ProcessingTaskId     BIGINT NOT NULL CONSTRAINT FK_VisionTransmission_Task REFERENCES ductwork.ProcessingTask (ProcessingTaskId),
    ProviderSnapshotId   INT NOT NULL CONSTRAINT FK_VisionTransmission_ProviderSnapshot REFERENCES ductwork.ProviderSnapshot (ProviderSnapshotId),
    SheetIdsJson         NVARCHAR(MAX) NOT NULL,
    ImageCount           INT NOT NULL,
    TotalBytes           BIGINT NOT NULL,
    DirectiveSha256      CHAR(64) NOT NULL,
    RequestedBy          NVARCHAR(256) NOT NULL,
    RequestedUtc         DATETIME2(7) NOT NULL CONSTRAINT DF_VisionTransmission_RequestedUtc DEFAULT (SYSUTCDATETIME()),
    Outcome              VARCHAR(8) NOT NULL CONSTRAINT DF_VisionTransmission_Outcome DEFAULT ('Reserved'),
    CompletedUtc         DATETIME2(7) NULL,
    ErrorMessage         NVARCHAR(1000) NULL,
    CONSTRAINT CK_VisionTransmission_Outcome CHECK (Outcome IN ('Reserved', 'Sent', 'Failed')),
    CONSTRAINT CK_VisionTransmission_Counts CHECK (ImageCount >= 1 AND TotalBytes >= 0),
    CONSTRAINT CK_VisionTransmission_Completed CHECK ((Outcome = 'Reserved' AND CompletedUtc IS NULL) OR (Outcome <> 'Reserved' AND CompletedUtc IS NOT NULL))
);
CREATE INDEX IX_VisionTransmission_Job ON ductwork.VisionTransmission (JobId, VisionTransmissionId);
GO

CREATE TRIGGER ductwork.TR_VisionTransmission_NoDelete ON ductwork.VisionTransmission
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51017, 'The record of what was sent to a hosted model cannot be deleted.', 1;
END;
GO
