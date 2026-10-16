-- Interpretation rules, what they changed, and how findings are resolved (plan section 6.11). The user is the arbiter:
-- a finding is closed by an approved rule, by an item override, or by a waiver with a reason, and nothing is silent.
-- Additive: new tables and columns. One CHECK constraint on EntityRevision is replaced to allow a new action.

-- A rule is data with a scope, a deterministic match and a deterministic action. Its content never changes after it is
-- created: a change is a new version. Rules are retired, never deleted, because past results point at them.
CREATE TABLE ductwork.InterpretationRule
(
    RuleId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_InterpretationRule PRIMARY KEY,
    PublicId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Rule_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_Rule_PublicId UNIQUE,
    -- The same for every version of one rule.
    RuleKey         UNIQUEIDENTIFIER NOT NULL,
    Version         INT NOT NULL,
    Kind            VARCHAR(14) NOT NULL CONSTRAINT DF_Rule_Kind DEFAULT ('Interpretation'),
    Scope           VARCHAR(12) NOT NULL,
    -- Where the rule applies. An instance rule applies to every job and has none of these.
    JobId           BIGINT NULL CONSTRAINT FK_Rule_Job REFERENCES ductwork.Job (JobId),
    BuildingTypeId  BIGINT NULL CONSTRAINT FK_Rule_BuildingType REFERENCES ductwork.BuildingType (BuildingTypeId),
    SheetId         BIGINT NULL CONSTRAINT FK_Rule_Sheet REFERENCES ductwork.Sheet (SheetId),
    -- The job it was written on, so its creation is on that job's audit log whatever its scope.
    OriginJobId     BIGINT NOT NULL CONSTRAINT FK_Rule_OriginJob REFERENCES ductwork.Job (JobId),
    Name            NVARCHAR(100) NOT NULL,
    Description     NVARCHAR(1000) NULL,
    MatchJson       NVARCHAR(MAX) NOT NULL,
    ActionJson      NVARCHAR(MAX) NOT NULL,
    Status          VARCHAR(8) NOT NULL CONSTRAINT DF_Rule_Status DEFAULT ('Proposed'),
    FromFindingId   BIGINT NULL CONSTRAINT FK_Rule_Finding REFERENCES ductwork.Finding (FindingId),
    CreatedBy       NVARCHAR(256) NOT NULL,
    CreatedUtc      DATETIME2(7) NOT NULL CONSTRAINT DF_Rule_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    ApprovedBy      NVARCHAR(256) NULL,
    ApprovedUtc     DATETIME2(7) NULL,
    RetiredBy       NVARCHAR(256) NULL,
    RetiredUtc      DATETIME2(7) NULL,
    RetireReason    NVARCHAR(400) NULL,
    CONSTRAINT UQ_Rule_Key_Version UNIQUE (RuleKey, Version),
    CONSTRAINT CK_Rule_Kind CHECK (Kind IN ('Interpretation', 'Requirement')),
    CONSTRAINT CK_Rule_Scope CHECK (
        (Scope = 'Instance'     AND JobId IS NULL     AND BuildingTypeId IS NULL     AND SheetId IS NULL)
        OR (Scope = 'Job'          AND JobId IS NOT NULL AND BuildingTypeId IS NULL     AND SheetId IS NULL)
        OR (Scope = 'BuildingType' AND JobId IS NOT NULL AND BuildingTypeId IS NOT NULL AND SheetId IS NULL)
        OR (Scope = 'Sheet'        AND JobId IS NOT NULL AND BuildingTypeId IS NULL     AND SheetId IS NOT NULL)),
    CONSTRAINT CK_Rule_Status CHECK (Status IN ('Proposed', 'Approved', 'Retired')),
    CONSTRAINT CK_Rule_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
    CONSTRAINT CK_Rule_Version CHECK (Version >= 1),
    -- A rule that can change a count was approved by someone, and a retired one says who retired it and why.
    CONSTRAINT CK_Rule_Approved CHECK (Status = 'Proposed' OR (ApprovedBy IS NOT NULL AND ApprovedUtc IS NOT NULL) OR (Status = 'Retired' AND ApprovedBy IS NULL)),
    CONSTRAINT CK_Rule_Retired CHECK ((Status = 'Retired' AND RetiredBy IS NOT NULL AND RetiredUtc IS NOT NULL AND LEN(LTRIM(RTRIM(ISNULL(RetireReason, '')))) > 0)
        OR (Status <> 'Retired' AND RetiredBy IS NULL AND RetiredUtc IS NULL AND RetireReason IS NULL))
);
-- Only one version of a rule is in force at a time.
CREATE UNIQUE INDEX UX_Rule_OneApproved ON ductwork.InterpretationRule (RuleKey) WHERE Status = 'Approved';
CREATE INDEX IX_Rule_Job ON ductwork.InterpretationRule (JobId, Status);
GO

CREATE TRIGGER ductwork.TR_Rule_NoDelete ON ductwork.InterpretationRule
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51014, 'Rules are retired, never deleted, because past results refer to them.', 1;
END;
GO

CREATE TRIGGER ductwork.TR_Rule_Immutable ON ductwork.InterpretationRule
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1 FROM inserted i JOIN deleted d ON d.RuleId = i.RuleId
        WHERE i.RuleKey <> d.RuleKey OR i.Version <> d.Version OR i.Kind <> d.Kind OR i.Scope <> d.Scope
           OR ISNULL(i.JobId, -1) <> ISNULL(d.JobId, -1) OR ISNULL(i.BuildingTypeId, -1) <> ISNULL(d.BuildingTypeId, -1) OR ISNULL(i.SheetId, -1) <> ISNULL(d.SheetId, -1)
           OR i.Name <> d.Name OR ISNULL(i.Description, '') <> ISNULL(d.Description, '')
           OR i.MatchJson <> d.MatchJson OR i.ActionJson <> d.ActionJson OR i.CreatedBy <> d.CreatedBy
           OR (d.Status = 'Retired' AND i.Status <> 'Retired'))
        THROW 51015, 'A rule cannot be changed after it is created, and a retired rule cannot come back. Write a new version instead.', 1;
END;
GO

-- What a rule changed, item by item. The change itself is on the item's history; this ties it back to the rule and the batch.
CREATE TABLE ductwork.RuleApplication
(
    RuleApplicationId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RuleApplication PRIMARY KEY,
    JobId             BIGINT NOT NULL CONSTRAINT FK_RuleApplication_Job REFERENCES ductwork.Job (JobId),
    RuleId            BIGINT NOT NULL CONSTRAINT FK_RuleApplication_Rule REFERENCES ductwork.InterpretationRule (RuleId),
    EntityId          BIGINT NOT NULL CONSTRAINT FK_RuleApplication_Entity REFERENCES ductwork.Entity (EntityId),
    EntityRevisionNo  INT NOT NULL,
    BatchId           UNIQUEIDENTIFIER NOT NULL,
    Change            NVARCHAR(400) NOT NULL,
    AppliedBy         NVARCHAR(256) NOT NULL,
    AppliedUtc        DATETIME2(7) NOT NULL CONSTRAINT DF_RuleApplication_AppliedUtc DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_RuleApplication_Rule ON ductwork.RuleApplication (RuleId, JobId);
CREATE INDEX IX_RuleApplication_Entity ON ductwork.RuleApplication (EntityId);
GO

ALTER TABLE ductwork.Entity ADD ResolvedByRuleId BIGINT NULL CONSTRAINT FK_Entity_Rule REFERENCES ductwork.InterpretationRule (RuleId);
GO

ALTER TABLE ductwork.EntityRevision DROP CONSTRAINT CK_EntityRevision_Action;
ALTER TABLE ductwork.EntityRevision ADD CONSTRAINT CK_EntityRevision_Action CHECK (Action IN ('Added', 'Reviewed', 'Disposition', 'Edited', 'Rule', 'Undone'));
GO

-- How a finding was closed: by a rule, by overriding the item, or by waiving it with a reason.
ALTER TABLE ductwork.Finding ADD ResolvedByKind VARCHAR(8) NULL;
ALTER TABLE ductwork.Finding ADD ResolvedRuleId BIGINT NULL CONSTRAINT FK_Finding_Rule REFERENCES ductwork.InterpretationRule (RuleId);
ALTER TABLE ductwork.Finding ADD ResolvedBy NVARCHAR(256) NULL;
ALTER TABLE ductwork.Finding ADD ResolvedUtc DATETIME2(7) NULL;
ALTER TABLE ductwork.Finding ADD ResolutionNote NVARCHAR(400) NULL;
GO

ALTER TABLE ductwork.Finding ADD CONSTRAINT CK_Finding_Resolution CHECK (
    (State = 'Open' AND ResolvedByKind IS NULL AND ResolvedRuleId IS NULL AND ResolvedBy IS NULL AND ResolvedUtc IS NULL)
    OR (State = 'Resolved' AND ResolvedByKind IN ('Rule', 'Override') AND ResolvedBy IS NOT NULL AND ResolvedUtc IS NOT NULL AND (ResolvedByKind <> 'Rule' OR ResolvedRuleId IS NOT NULL))
    OR (State = 'Waived' AND ResolvedByKind = 'Waiver' AND ResolvedBy IS NOT NULL AND ResolvedUtc IS NOT NULL AND LEN(LTRIM(RTRIM(ISNULL(ResolutionNote, '')))) > 0));
GO
