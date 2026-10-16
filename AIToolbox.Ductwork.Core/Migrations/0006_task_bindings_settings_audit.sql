-- Evaluation-gated task bindings (plan section 6.2) and an append-only audit of instance-level settings.

-- Which producer runs a task type. A binding cannot exist without a measured evaluation run of that exact
-- producer, so nothing reaches users that has not been scored. Bindings are retired, never deleted, so the
-- history of what ran when stays answerable.
CREATE TABLE ductwork.TaskBinding
(
    TaskBindingId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TaskBinding PRIMARY KEY,
    TaskType      VARCHAR(60) NOT NULL,
    ProducerId    BIGINT NOT NULL CONSTRAINT FK_TaskBinding_Producer REFERENCES ductwork.Producer (ProducerId),
    EvalRunId     INT NOT NULL CONSTRAINT FK_TaskBinding_EvalRun REFERENCES ductwork.EvalRun (EvalRunId),
    SendsDrawingContentToHostedModel BIT NOT NULL CONSTRAINT DF_TaskBinding_Sends DEFAULT (0),
    MaxAttempts   INT NOT NULL CONSTRAINT DF_TaskBinding_MaxAttempts DEFAULT (3),
    ActiveFromUtc DATETIME2(7) NOT NULL CONSTRAINT DF_TaskBinding_ActiveFromUtc DEFAULT (SYSUTCDATETIME()),
    RetiredUtc    DATETIME2(7) NULL,
    CreatedBy     NVARCHAR(256) NOT NULL,
    CONSTRAINT CK_TaskBinding_TaskType CHECK (LEN(LTRIM(RTRIM(TaskType))) > 0),
    CONSTRAINT CK_TaskBinding_MaxAttempts CHECK (MaxAttempts >= 1)
);
-- One active binding per task type.
CREATE UNIQUE INDEX UX_TaskBinding_ActivePerType ON ductwork.TaskBinding (TaskType) WHERE RetiredUtc IS NULL;
GO

-- Who changed an instance-level setting, when, and from what to what. Append-only.
CREATE TABLE ductwork.SettingsAuditEvent
(
    SettingsAuditEventId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SettingsAuditEvent PRIMARY KEY,
    OccurredUtc  DATETIME2(7) NOT NULL CONSTRAINT DF_SettingsAuditEvent_OccurredUtc DEFAULT (SYSUTCDATETIME()),
    ActorType    VARCHAR(8) NOT NULL,
    ActorEmail   NVARCHAR(256) NULL,
    ActorDisplayName NVARCHAR(200) NULL,
    ClientIp     VARCHAR(64) NULL,
    Category     VARCHAR(30) NOT NULL,
    Action       VARCHAR(60) NOT NULL,
    Summary      NVARCHAR(1000) NOT NULL,
    BeforeJson   NVARCHAR(MAX) NULL,
    AfterJson    NVARCHAR(MAX) NULL,
    Reason       NVARCHAR(1000) NULL,
    CONSTRAINT CK_SettingsAuditEvent_ActorType CHECK (ActorType IN ('User', 'System', 'Worker', 'Llm'))
);
GO

CREATE TRIGGER ductwork.TR_SettingsAuditEvent_AppendOnly ON ductwork.SettingsAuditEvent
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51012, 'The settings audit log is append-only. Entries cannot be changed or deleted.', 1;
END;
GO
