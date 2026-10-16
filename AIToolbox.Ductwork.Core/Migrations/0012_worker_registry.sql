-- The workers that serve this instance (plan sections 6.5 and 6.9). A worker introduces itself when it connects, so an
-- administrator can see which are alive, what they are running, and ask one to finish its work and stop taking more.
-- Additive: one new table. The API key itself lives in AIToolbox's own consumer tables; this only remembers who connected.

CREATE TABLE ductwork.WorkerInstance
(
    WorkerInstanceId  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WorkerInstance PRIMARY KEY,
    -- dbo.ApiConsumers.Id, kept as a plain number because that table belongs to the rest of AIToolbox.
    ApiConsumerId     INT NOT NULL,
    WorkerName        NVARCHAR(60) NOT NULL,
    HostName          NVARCHAR(100) NULL,
    WorkerVersion     NVARCHAR(40) NULL,
    ContractVersion   VARCHAR(20) NOT NULL,
    TaskTypesJson     NVARCHAR(MAX) NOT NULL CONSTRAINT DF_WorkerInstance_Types DEFAULT ('[]'),
    MaxConcurrency    INT NULL,
    FirstSeenUtc      DATETIME2(7) NOT NULL CONSTRAINT DF_WorkerInstance_First DEFAULT (SYSUTCDATETIME()),
    LastSeenUtc       DATETIME2(7) NOT NULL CONSTRAINT DF_WorkerInstance_Seen DEFAULT (SYSUTCDATETIME()),
    LastHandshakeUtc  DATETIME2(7) NOT NULL CONSTRAINT DF_WorkerInstance_Handshake DEFAULT (SYSUTCDATETIME()),
    LastClaimUtc      DATETIME2(7) NULL,
    -- Set by an administrator: take no new work, finish what you have.
    DrainRequested    BIT NOT NULL CONSTRAINT DF_WorkerInstance_Drain DEFAULT (0),
    DrainRequestedBy  NVARCHAR(256) NULL,
    DrainRequestedUtc DATETIME2(7) NULL,
    CONSTRAINT UQ_WorkerInstance_Name UNIQUE (ApiConsumerId, WorkerName),
    CONSTRAINT CK_WorkerInstance_Name CHECK (LEN(LTRIM(RTRIM(WorkerName))) > 0),
    CONSTRAINT CK_WorkerInstance_Drain CHECK (DrainRequested = 0 OR (DrainRequestedBy IS NOT NULL AND DrainRequestedUtc IS NOT NULL)),
    CONSTRAINT CK_WorkerInstance_Concurrency CHECK (MaxConcurrency IS NULL OR MaxConcurrency >= 1)
);
GO
