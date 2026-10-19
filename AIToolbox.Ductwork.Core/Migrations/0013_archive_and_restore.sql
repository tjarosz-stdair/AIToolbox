-- Archive, purge and restore (plan section 8.3). Additive apart from two deliberate relaxations described below, and the
-- ductwork schema is not in production yet.
--
-- JobArchive describes one zip: where it is, its hash, what it held, and how far it got. ArchiveOperation is the unit of
-- work that makes it (or restores from it): server-run, resumable, one active per job. A purge removes only the heavy detail
-- rows of a job; the job record, its members, audit trail, tasks, building types, rules and vision records stay.

CREATE TABLE ductwork.JobArchive
(
    ArchiveId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobArchive PRIMARY KEY,
    PublicId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_JobArchive_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_JobArchive_PublicId UNIQUE,
    JobId              BIGINT NOT NULL CONSTRAINT FK_JobArchive_Job REFERENCES ductwork.Job (JobId),
    CompletionVersion  INT NOT NULL,
    Kind               VARCHAR(8) NOT NULL,
    -- Writing: being made. Ready: a verified zip, and the job's data is still in SQL. PurgePending: the zip is verified and the
    -- detail rows are being removed. Purged: the zip is the only full copy. Failed: abandoned, nothing was removed.
    -- Superseded: the job was reopened after this was made (kept, never deleted).
    Status             VARCHAR(12) NOT NULL CONSTRAINT DF_JobArchive_Status DEFAULT ('Writing'),
    StateAtArchive     VARCHAR(16) NOT NULL,
    FormatVersion      INT NOT NULL,
    ContractVersion    VARCHAR(20) NOT NULL,
    -- Where the zip is, relative to the archive store, and what it must hash to.
    StorageKey         VARCHAR(200) NULL,
    FileName           NVARCHAR(200) NULL,
    SizeBytes          BIGINT NULL,
    Sha256             BINARY(32) NULL,
    -- What it held: rows per table, a hash over all detail rows, the source files, and the audit chain head at the time.
    TableCountsJson    NVARCHAR(MAX) NULL,
    DataHash           BINARY(32) NULL,
    SourceFileCount    INT NULL,
    SourceBytes        BIGINT NULL,
    AuditHeadEventId   BIGINT NULL,
    AuditHeadHash      CHAR(64) NULL,
    -- Every place the zip has been, oldest first, so a move or relink is never silent.
    LocationHistoryJson NVARCHAR(MAX) NULL,
    LastProblem        NVARCHAR(1000) NULL,
    LastProblemUtc     DATETIME2(7) NULL,
    CreatedUtc         DATETIME2(7) NOT NULL CONSTRAINT DF_JobArchive_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CreatedBy          NVARCHAR(256) NOT NULL,
    VerifiedUtc        DATETIME2(7) NULL,
    PurgedUtc          DATETIME2(7) NULL,
    RestoredUtc        DATETIME2(7) NULL,
    SupersededUtc      DATETIME2(7) NULL,
    CONSTRAINT CK_JobArchive_Kind CHECK (Kind IN ('ZipOnly', 'Purge')),
    CONSTRAINT CK_JobArchive_Status CHECK (Status IN ('Writing', 'Ready', 'PurgePending', 'Purged', 'Failed', 'Superseded')),
    CONSTRAINT CK_JobArchive_State CHECK (StateAtArchive IN ('Complete', 'Closed')),
    -- A zip that is relied on has a place, a size and a hash.
    CONSTRAINT CK_JobArchive_Relied CHECK (Status IN ('Writing', 'Failed') OR (StorageKey IS NOT NULL AND SizeBytes IS NOT NULL AND Sha256 IS NOT NULL AND DataHash IS NOT NULL))
);
CREATE INDEX IX_JobArchive_Job ON ductwork.JobArchive (JobId, ArchiveId DESC);
CREATE INDEX IX_JobArchive_Status ON ductwork.JobArchive (Status) WHERE Status IN ('Writing', 'PurgePending', 'Purged');
GO

CREATE TABLE ductwork.ArchiveOperation
(
    OperationId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ArchiveOperation PRIMARY KEY,
    PublicId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ArchiveOperation_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_ArchiveOperation_PublicId UNIQUE,
    JobId           BIGINT NOT NULL CONSTRAINT FK_ArchiveOperation_Job REFERENCES ductwork.Job (JobId),
    ArchiveId       BIGINT NOT NULL CONSTRAINT FK_ArchiveOperation_Archive REFERENCES ductwork.JobArchive (ArchiveId),
    Kind            VARCHAR(12) NOT NULL,
    Status          VARCHAR(10) NOT NULL CONSTRAINT DF_ArchiveOperation_Status DEFAULT ('Queued'),
    -- Where a resumed run picks up: Write, Verify, Purge (archive) or Locate, Import (restore).
    Step            VARCHAR(12) NOT NULL CONSTRAINT DF_ArchiveOperation_Step DEFAULT ('Start'),
    Message         NVARCHAR(400) NULL,
    ProgressDone    BIGINT NOT NULL CONSTRAINT DF_ArchiveOperation_Done DEFAULT (0),
    ProgressTotal   BIGINT NULL,
    Attempt         INT NOT NULL CONSTRAINT DF_ArchiveOperation_Attempt DEFAULT (0),
    MaxAttempts     INT NOT NULL CONSTRAINT DF_ArchiveOperation_MaxAttempts DEFAULT (3),
    LeaseOwner      NVARCHAR(100) NULL,
    LeaseExpiresUtc DATETIME2(7) NULL,
    RequestedBy     NVARCHAR(256) NOT NULL,
    RequestedUtc    DATETIME2(7) NOT NULL CONSTRAINT DF_ArchiveOperation_RequestedUtc DEFAULT (SYSUTCDATETIME()),
    StartedUtc      DATETIME2(7) NULL,
    FinishedUtc     DATETIME2(7) NULL,
    Error           NVARCHAR(2000) NULL,
    CONSTRAINT CK_ArchiveOperation_Kind CHECK (Kind IN ('ArchiveZip', 'ArchivePurge', 'Restore')),
    CONSTRAINT CK_ArchiveOperation_Status CHECK (Status IN ('Queued', 'Running', 'Succeeded', 'Failed')),
    CONSTRAINT CK_ArchiveOperation_Lease CHECK ((Status = 'Running' AND LeaseOwner IS NOT NULL AND LeaseExpiresUtc IS NOT NULL) OR (Status <> 'Running' AND LeaseOwner IS NULL AND LeaseExpiresUtc IS NULL)),
    CONSTRAINT CK_ArchiveOperation_Finished CHECK ((Status IN ('Succeeded', 'Failed') AND FinishedUtc IS NOT NULL) OR (Status IN ('Queued', 'Running') AND FinishedUtc IS NULL)),
    CONSTRAINT CK_ArchiveOperation_Attempts CHECK (Attempt >= 0 AND MaxAttempts >= 1 AND Attempt <= MaxAttempts)
);
-- One piece of archive work at a time per job.
CREATE UNIQUE INDEX UX_ArchiveOperation_OneActive ON ductwork.ArchiveOperation (JobId) WHERE Status IN ('Queued', 'Running');
CREATE INDEX IX_ArchiveOperation_Claim ON ductwork.ArchiveOperation (Status, LeaseExpiresUtc, OperationId);
CREATE INDEX IX_ArchiveOperation_Job ON ductwork.ArchiveOperation (JobId, OperationId DESC);
GO

-- A rule is kept when its job's detail is purged (rules are immutable and never deleted), and it may name a sheet or a finding
-- that the purge removes. Those two references become soft: restore brings the same ids back, so they resolve again.
ALTER TABLE ductwork.InterpretationRule DROP CONSTRAINT FK_Rule_Sheet;
ALTER TABLE ductwork.InterpretationRule DROP CONSTRAINT FK_Rule_Finding;
GO

-- The item history is append-only, with one narrow exception: a purge, which is only possible for a job that is Archiving and
-- whose archive is verified and marked PurgePending, run by a session that names that job. The rows come back on restore.
DROP TRIGGER ductwork.TR_EntityRevision_AppendOnly;
GO
CREATE TRIGGER ductwork.TR_EntityRevision_AppendOnly ON ductwork.EntityRevision
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        DECLARE @purgeJob BIGINT = TRY_CAST(SESSION_CONTEXT(N'ductwork.purge_job_id') AS BIGINT);
        IF @purgeJob IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM deleted d WHERE d.JobId <> @purgeJob)
           AND EXISTS (SELECT 1 FROM ductwork.Job j JOIN ductwork.JobArchive a ON a.JobId = j.JobId
                       WHERE j.JobId = @purgeJob AND j.State = 'Archiving' AND a.Status = 'PurgePending')
        BEGIN
            DELETE r FROM ductwork.EntityRevision r JOIN deleted d ON d.EntityRevisionId = r.EntityRevisionId;
            RETURN;
        END;
    END;
    THROW 51013, 'The item history is append-only. Revisions cannot be changed or deleted.', 1;
END;
GO
