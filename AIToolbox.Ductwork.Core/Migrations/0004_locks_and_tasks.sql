-- Single-editor lock with fencing and takeover (plan section 9) and processing tasks (plan sections 6.5 and 7).

-- Fencing tokens come from one sequence, so a token is never issued twice, even after a lock row is
-- deleted and a new one created. A write carrying an old token is therefore always recognisable.
CREATE SEQUENCE ductwork.FencingTokenSeq AS BIGINT START WITH 1 INCREMENT BY 1 NO CYCLE;
GO

-- At most one row per job: the editing session. Expiry is judged against the database clock.
CREATE TABLE ductwork.JobEditLock
(
    JobId             BIGINT NOT NULL CONSTRAINT PK_JobEditLock PRIMARY KEY
                      CONSTRAINT FK_JobEditLock_Job REFERENCES ductwork.Job (JobId),
    HolderUserEmail   NVARCHAR(256) NOT NULL,
    HolderDisplayName NVARCHAR(200) NULL,
    -- The lock belongs to a browser session, not a user, so a second tab by the same person is view-only.
    HolderSessionId   NVARCHAR(100) NOT NULL,
    FencingToken      BIGINT NOT NULL,
    AcquiredUtc       DATETIME2(7) NOT NULL,
    LastHeartbeatUtc  DATETIME2(7) NOT NULL,
    ExpiresUtc        DATETIME2(7) NOT NULL,
    CONSTRAINT CK_JobEditLock_Expiry CHECK (ExpiresUtc >= LastHeartbeatUtc),
    CONSTRAINT CK_JobEditLock_Session CHECK (LEN(LTRIM(RTRIM(HolderSessionId))) > 0)
);
GO

CREATE TABLE ductwork.PreemptRequest
(
    PreemptRequestId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PreemptRequest PRIMARY KEY,
    JobId                BIGINT NOT NULL CONSTRAINT FK_PreemptRequest_Job REFERENCES ductwork.Job (JobId),
    RequesterEmail       NVARCHAR(256) NOT NULL,
    RequesterDisplayName NVARCHAR(200) NULL,
    RequesterSessionId   NVARCHAR(100) NOT NULL,
    RequestedUtc         DATETIME2(7) NOT NULL,
    -- If the holder does not answer by then the request is granted: an open but unattended tab must not block a job forever.
    ExpiresUtc           DATETIME2(7) NOT NULL,
    Status               VARCHAR(10) NOT NULL CONSTRAINT DF_PreemptRequest_Status DEFAULT ('Pending'),
    ResolvedUtc          DATETIME2(7) NULL,
    ResolvedBy           NVARCHAR(256) NULL,
    CONSTRAINT CK_PreemptRequest_Status CHECK (Status IN ('Pending', 'Granted', 'Cancelled', 'Denied', 'Expired')),
    CONSTRAINT CK_PreemptRequest_Resolved CHECK ((Status = 'Pending' AND ResolvedUtc IS NULL) OR (Status <> 'Pending' AND ResolvedUtc IS NOT NULL))
);
-- Only one takeover request can be waiting on a job at a time.
CREATE UNIQUE INDEX UX_PreemptRequest_OnePending ON ductwork.PreemptRequest (JobId) WHERE Status = 'Pending';
GO

-- Work for the central worker (plan sections 6.5 and 6.9). The worker only ever claims, heartbeats, reports and
-- completes through the web app's API; this table is the queue, the lease and the history.
CREATE TABLE ductwork.ProcessingTask
(
    ProcessingTaskId  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProcessingTask PRIMARY KEY,
    PublicId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ProcessingTask_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_ProcessingTask_PublicId UNIQUE,
    ParentTaskId      BIGINT NULL CONSTRAINT FK_ProcessingTask_Parent REFERENCES ductwork.ProcessingTask (ProcessingTaskId),
    JobId             BIGINT NOT NULL CONSTRAINT FK_ProcessingTask_Job REFERENCES ductwork.Job (JobId),
    TaskType          VARCHAR(60) NOT NULL,
    Status            VARCHAR(10) NOT NULL CONSTRAINT DF_ProcessingTask_Status DEFAULT ('Queued'),
    CancelRequested   BIT NOT NULL CONSTRAINT DF_ProcessingTask_CancelRequested DEFAULT (0),
    ProgressDone      INT NOT NULL CONSTRAINT DF_ProcessingTask_ProgressDone DEFAULT (0),
    ProgressTotal     INT NULL,
    ProgressMessage   NVARCHAR(400) NULL,
    Attempt           INT NOT NULL CONSTRAINT DF_ProcessingTask_Attempt DEFAULT (0),
    MaxAttempts       INT NOT NULL CONSTRAINT DF_ProcessingTask_MaxAttempts DEFAULT (3),
    -- A retry waits until this time (backoff). Null means it can run now.
    NotBeforeUtc      DATETIME2(7) NULL,
    LeaseOwner        NVARCHAR(100) NULL,
    LeaseExpiresUtc   DATETIME2(7) NULL,
    -- Every result is attributable to the algorithm or model that produced it.
    ProducerId        BIGINT NOT NULL CONSTRAINT FK_ProcessingTask_Producer REFERENCES ductwork.Producer (ProducerId),
    InputManifestRef  NVARCHAR(400) NULL,
    ResultManifestRef NVARCHAR(400) NULL,
    ContractVersion   VARCHAR(20) NOT NULL,
    -- Set for any task that sends drawing content to a hosted model (plan section 6.12); such a task needs an approval.
    SendsDrawingContentToHostedModel BIT NOT NULL CONSTRAINT DF_ProcessingTask_Sends DEFAULT (0),
    VisionApprovalId  BIGINT NULL,
    CreatedUtc        DATETIME2(7) NOT NULL CONSTRAINT DF_ProcessingTask_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CreatedBy         NVARCHAR(256) NOT NULL,
    StartedUtc        DATETIME2(7) NULL,
    FinishedUtc       DATETIME2(7) NULL,
    ErrorMessage      NVARCHAR(2000) NULL,

    CONSTRAINT CK_ProcessingTask_Status CHECK (Status IN ('Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled')),
    CONSTRAINT CK_ProcessingTask_TaskType CHECK (LEN(LTRIM(RTRIM(TaskType))) > 0),
    -- A running task always has a lease holder and an expiry; a task that is not running has neither.
    CONSTRAINT CK_ProcessingTask_Lease CHECK (
        (Status = 'Running' AND LeaseOwner IS NOT NULL AND LeaseExpiresUtc IS NOT NULL)
        OR (Status <> 'Running' AND LeaseOwner IS NULL AND LeaseExpiresUtc IS NULL)),
    CONSTRAINT CK_ProcessingTask_Finished CHECK (
        (Status IN ('Succeeded', 'Failed', 'Cancelled') AND FinishedUtc IS NOT NULL)
        OR (Status IN ('Queued', 'Running') AND FinishedUtc IS NULL)),
    CONSTRAINT CK_ProcessingTask_Attempts CHECK (Attempt >= 0 AND MaxAttempts >= 1 AND Attempt <= MaxAttempts),
    CONSTRAINT CK_ProcessingTask_Progress CHECK (ProgressDone >= 0 AND (ProgressTotal IS NULL OR (ProgressTotal >= 0 AND ProgressDone <= ProgressTotal))),
    -- Hosted vision is never used without an explicit approval.
    CONSTRAINT CK_ProcessingTask_VisionApproval CHECK (SendsDrawingContentToHostedModel = 0 OR VisionApprovalId IS NOT NULL)
);
CREATE INDEX IX_ProcessingTask_Claim ON ductwork.ProcessingTask (Status, NotBeforeUtc, ProcessingTaskId) INCLUDE (JobId, TaskType, LeaseExpiresUtc);
CREATE INDEX IX_ProcessingTask_Job ON ductwork.ProcessingTask (JobId, Status);
GO
