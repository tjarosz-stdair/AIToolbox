-- Jobs, membership, tags, workflow steps, permissions and the append-only audit log
-- (plan sections 7, 8 and 10). A Job is a whole development, never a single building.

CREATE TABLE ductwork.Job
(
    JobId                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Job PRIMARY KEY,
    PublicId             UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Job_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_Job_PublicId UNIQUE,
    Name                 NVARCHAR(200) NOT NULL,
    CustomerName         NVARCHAR(200) NULL,
    ProjectName          NVARCHAR(200) NULL,
    SiteAddress          NVARCHAR(400) NULL,
    OwnerEmail           NVARCHAR(256) NOT NULL,
    State                VARCHAR(16) NOT NULL CONSTRAINT DF_Job_State DEFAULT ('Draft'),
    -- The state a job had before it was archived, so restore returns it there.
    StateBeforeArchive   VARCHAR(16) NULL,
    CompletionVersion    INT NOT NULL CONSTRAINT DF_Job_CompletionVersion DEFAULT (0),
    -- Increments on every change to the job, so a stale write is detected and pushed views can refresh.
    StateVersion         BIGINT NOT NULL CONSTRAINT DF_Job_StateVersion DEFAULT (1),
    ReviewLevel          VARCHAR(12) NOT NULL CONSTRAINT DF_Job_ReviewLevel DEFAULT ('Sensitive'),
    BaselineManualHours  DECIMAL(9,2) NULL,
    CompletedUtc         DATETIME2 NULL,
    CompletedBy          NVARCHAR(256) NULL,
    ClosedUtc            DATETIME2 NULL,
    ClosedBy            NVARCHAR(256) NULL,
    CloseReason          NVARCHAR(1000) NULL,
    ReopenedUtc          DATETIME2 NULL,
    ReopenedBy           NVARCHAR(256) NULL,
    ReopenReason         NVARCHAR(1000) NULL,
    SummaryJson          NVARCHAR(MAX) NULL,
    CreatedUtc           DATETIME2 NOT NULL CONSTRAINT DF_Job_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CreatedBy            NVARCHAR(256) NOT NULL,
    UpdatedUtc           DATETIME2 NOT NULL CONSTRAINT DF_Job_UpdatedUtc DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT CK_Job_State CHECK (State IN
        ('Draft', 'InReview', 'Completing', 'Complete', 'Closed', 'Archiving', 'Archived', 'Restoring')),
    CONSTRAINT CK_Job_StateBeforeArchive CHECK (StateBeforeArchive IS NULL OR StateBeforeArchive IN ('Complete', 'Closed')),
    CONSTRAINT CK_Job_ReviewLevel CHECK (ReviewLevel IN ('Sensitive', 'Normal', 'Insensitive')),
    CONSTRAINT CK_Job_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
    CONSTRAINT CK_Job_OwnerEmail CHECK (LEN(LTRIM(RTRIM(OwnerEmail))) > 0),
    -- A closed job always says why.
    CONSTRAINT CK_Job_CloseReason CHECK (State <> 'Closed' OR (CloseReason IS NOT NULL AND LEN(LTRIM(RTRIM(CloseReason))) > 0)),
    CONSTRAINT CK_Job_Baseline CHECK (BaselineManualHours IS NULL OR BaselineManualHours >= 0)
);
CREATE INDEX IX_Job_State ON ductwork.Job (State, UpdatedUtc DESC);
CREATE INDEX IX_Job_Owner ON ductwork.Job (OwnerEmail);
GO

-- The master record is a permanent record: it is never deleted, only its detail rows can be purged.
CREATE TRIGGER ductwork.TR_Job_NoDelete ON ductwork.Job
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51010, 'A Ductwork job record is permanent and cannot be deleted. Only its detail rows can be purged.', 1;
END;
GO

CREATE TABLE ductwork.JobMember
(
    JobId     BIGINT NOT NULL CONSTRAINT FK_JobMember_Job REFERENCES ductwork.Job (JobId),
    UserEmail NVARCHAR(256) NOT NULL,
    Role      VARCHAR(8) NOT NULL,
    AddedUtc  DATETIME2 NOT NULL CONSTRAINT DF_JobMember_AddedUtc DEFAULT (SYSUTCDATETIME()),
    AddedBy   NVARCHAR(256) NOT NULL,
    CONSTRAINT PK_JobMember PRIMARY KEY (JobId, UserEmail),
    CONSTRAINT CK_JobMember_Role CHECK (Role IN ('Owner', 'Editor', 'Viewer')),
    -- Emails are stored lower-case and trimmed. The binary collation matters: under the database's usual case-insensitive
    -- collation this check would pass for any spelling.
    CONSTRAINT CK_JobMember_Email CHECK (UserEmail COLLATE Latin1_General_BIN2 = LOWER(LTRIM(RTRIM(UserEmail))) COLLATE Latin1_General_BIN2 AND LEN(UserEmail) > 0)
);
CREATE INDEX IX_JobMember_User ON ductwork.JobMember (UserEmail);
GO

CREATE TABLE ductwork.Tag
(
    TagId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tag PRIMARY KEY,
    Name           NVARCHAR(100) NOT NULL,
    NameNormalized NVARCHAR(100) NOT NULL CONSTRAINT UQ_Tag_NameNormalized UNIQUE,
    CONSTRAINT CK_Tag_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0)
);
GO

CREATE TABLE ductwork.JobTag
(
    JobId    BIGINT NOT NULL CONSTRAINT FK_JobTag_Job REFERENCES ductwork.Job (JobId),
    TagId    INT NOT NULL CONSTRAINT FK_JobTag_Tag REFERENCES ductwork.Tag (TagId),
    AddedBy  NVARCHAR(256) NOT NULL,
    AddedUtc DATETIME2 NOT NULL CONSTRAINT DF_JobTag_AddedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_JobTag PRIMARY KEY (JobId, TagId)
);
CREATE INDEX IX_JobTag_Tag ON ductwork.JobTag (TagId);
GO

CREATE TABLE ductwork.JobWorkflowStep
(
    JobId  BIGINT NOT NULL CONSTRAINT FK_JobWorkflowStep_Job REFERENCES ductwork.Job (JobId),
    Step   VARCHAR(8) NOT NULL,
    Status VARCHAR(12) NOT NULL CONSTRAINT DF_JobWorkflowStep_Status DEFAULT ('NotStarted'),
    UpdatedUtc DATETIME2 NOT NULL CONSTRAINT DF_JobWorkflowStep_UpdatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_JobWorkflowStep PRIMARY KEY (JobId, Step),
    CONSTRAINT CK_JobWorkflowStep_Step CHECK (Step IN ('Setup', 'Process', 'Review', 'Report')),
    CONSTRAINT CK_JobWorkflowStep_Status CHECK (Status IN ('NotStarted', 'Active', 'Done'))
);
GO

-- Ductwork-only permissions, for example Job Manager. Not a global AIToolbox role.
CREATE TABLE ductwork.ToolPermission
(
    UserEmail  NVARCHAR(256) NOT NULL,
    Permission VARCHAR(20) NOT NULL,
    GrantedBy  NVARCHAR(256) NOT NULL,
    GrantedUtc DATETIME2 NOT NULL CONSTRAINT DF_ToolPermission_GrantedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ToolPermission PRIMARY KEY (UserEmail, Permission),
    CONSTRAINT CK_ToolPermission_Permission CHECK (Permission IN ('JobManager')),
    CONSTRAINT CK_ToolPermission_Email CHECK (UserEmail COLLATE Latin1_General_BIN2 = LOWER(LTRIM(RTRIM(UserEmail))) COLLATE Latin1_General_BIN2 AND LEN(UserEmail) > 0)
);
GO

-- Append-only, hash-chained history per job (plan section 10).
CREATE TABLE ductwork.JobAuditEvent
(
    JobId            BIGINT NOT NULL CONSTRAINT FK_JobAuditEvent_Job REFERENCES ductwork.Job (JobId),
    EventId          BIGINT NOT NULL,                       -- monotonic per job, starting at 1
    OccurredUtc      DATETIME2(7) NOT NULL,                 -- server clock, never the client's
    ActorType        VARCHAR(8) NOT NULL,
    ActorEmail       NVARCHAR(256) NULL,
    ActorDisplayName NVARCHAR(200) NULL,
    ActorSessionId   NVARCHAR(100) NULL,
    ClientIp         VARCHAR(64) NULL,
    UserAgent        NVARCHAR(400) NULL,
    Category         VARCHAR(20) NOT NULL,
    Action           VARCHAR(60) NOT NULL,
    SubjectType      VARCHAR(40) NULL,
    SubjectPublicId  NVARCHAR(64) NULL,
    Summary          NVARCHAR(1000) NOT NULL,
    BeforeJson       NVARCHAR(MAX) NULL,
    AfterJson        NVARCHAR(MAX) NULL,
    Reason           NVARCHAR(1000) NULL,
    CorrelationId    UNIQUEIDENTIFIER NULL,
    ProcessingTaskId BIGINT NULL,
    ProducerId       BIGINT NULL,
    RevisionId       BIGINT NULL,
    FencingToken     BIGINT NULL,
    StateVersion     BIGINT NOT NULL,
    PrevHash         CHAR(64) NOT NULL,
    Hash             CHAR(64) NOT NULL,
    CONSTRAINT PK_JobAuditEvent PRIMARY KEY (JobId, EventId),
    CONSTRAINT CK_JobAuditEvent_ActorType CHECK (ActorType IN ('User', 'System', 'Worker', 'Llm')),
    CONSTRAINT CK_JobAuditEvent_EventId CHECK (EventId >= 1),
    -- A user event always names the user.
    CONSTRAINT CK_JobAuditEvent_UserHasEmail CHECK (ActorType <> 'User' OR (ActorEmail IS NOT NULL AND LEN(LTRIM(RTRIM(ActorEmail))) > 0))
);
CREATE INDEX IX_JobAuditEvent_Category ON ductwork.JobAuditEvent (JobId, Category, EventId);
CREATE INDEX IX_JobAuditEvent_Time ON ductwork.JobAuditEvent (OccurredUtc);
GO

-- The application login should additionally have no UPDATE or DELETE permission on this table; the
-- trigger makes the rule hold even for a login that does.
CREATE TRIGGER ductwork.TR_JobAuditEvent_AppendOnly ON ductwork.JobAuditEvent
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51011, 'The job audit log is append-only. Entries cannot be changed or deleted.', 1;
END;
GO

-- Time on job (plan section 10.1). Totals are computed on the server from heartbeats, never reported by the client.
CREATE TABLE ductwork.ActivitySession
(
    SessionId         NVARCHAR(100) NOT NULL,
    JobId             BIGINT NOT NULL CONSTRAINT FK_ActivitySession_Job REFERENCES ductwork.Job (JobId),
    UserEmail         NVARCHAR(256) NOT NULL,
    WorkflowStep      VARCHAR(8) NULL,
    Surface           VARCHAR(20) NULL,
    ReviewLevel       VARCHAR(12) NULL,
    StartedUtc        DATETIME2 NOT NULL,
    LastHeartbeatUtc  DATETIME2 NOT NULL,
    EndedUtc          DATETIME2 NULL,
    ActiveSeconds     FLOAT NOT NULL CONSTRAINT DF_ActivitySession_Active DEFAULT (0),
    IdleSeconds       FLOAT NOT NULL CONSTRAINT DF_ActivitySession_Idle DEFAULT (0),
    UncreditedSeconds FLOAT NOT NULL CONSTRAINT DF_ActivitySession_Uncredited DEFAULT (0),
    Heartbeats        INT NOT NULL CONSTRAINT DF_ActivitySession_Heartbeats DEFAULT (0),
    CONSTRAINT PK_ActivitySession PRIMARY KEY (JobId, SessionId),
    CONSTRAINT CK_ActivitySession_NonNegative CHECK (ActiveSeconds >= 0 AND IdleSeconds >= 0 AND UncreditedSeconds >= 0 AND Heartbeats >= 0),
    CONSTRAINT CK_ActivitySession_Step CHECK (WorkflowStep IS NULL OR WorkflowStep IN ('Setup', 'Process', 'Review', 'Report'))
);
GO
