-- Producer and provenance (plan sections 6.1 and 7).
-- Directive snapshots live in dbo (AIToolbox's ToolDirectiveSnapshots), so DirectiveSnapshotId is a
-- soft reference here. Provider snapshots are owned by this schema because provider rows are mutable.

CREATE TABLE ductwork.ProviderSnapshot
(
    ProviderSnapshotId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProviderSnapshot PRIMARY KEY,
    ProviderType       INT NOT NULL,
    EndpointHost       NVARCHAR(255) NOT NULL,
    ModelName          NVARCHAR(200) NOT NULL,
    ApiVersion         NVARCHAR(50) NULL,
    MaxOutputTokens    INT NULL,
    SupportsVision     BIT NOT NULL CONSTRAINT DF_ProviderSnapshot_SupportsVision DEFAULT (0),
    GovernanceRoute    NVARCHAR(100) NULL,
    -- SHA-256 over the fields above. Never the API key, which is not stored here at all.
    ContentHash        CHAR(64) NOT NULL CONSTRAINT UQ_ProviderSnapshot_ContentHash UNIQUE,
    CreatedUtc         DATETIME2 NOT NULL CONSTRAINT DF_ProviderSnapshot_CreatedUtc DEFAULT (SYSUTCDATETIME())
);
GO

CREATE TABLE ductwork.Producer
(
    ProducerId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Producer PRIMARY KEY,
    ProducerKey         NVARCHAR(400) NOT NULL CONSTRAINT UQ_Producer_ProducerKey UNIQUE,
    Kind                VARCHAR(10) NOT NULL,
    DirectiveSnapshotId INT NULL,
    ProviderSnapshotId  INT NULL CONSTRAINT FK_Producer_ProviderSnapshot REFERENCES ductwork.ProviderSnapshot (ProviderSnapshotId),
    AlgorithmName       NVARCHAR(100) NULL,
    AlgorithmVersion    NVARCHAR(50) NULL,
    ParamsHash          NVARCHAR(128) NULL,
    UserEmail           NVARCHAR(256) NULL,
    CreatedUtc          DATETIME2 NOT NULL CONSTRAINT DF_Producer_CreatedUtc DEFAULT (SYSUTCDATETIME()),

    CONSTRAINT CK_Producer_Kind CHECK (Kind IN ('Llm', 'Algorithm', 'Manual')),

    -- Exactly one of three shapes. An LLM producer cannot exist without both snapshots.
    -- Every required column is tested IS NOT NULL explicitly: a CHECK passes when it evaluates to
    -- UNKNOWN, so LEN(NULL) > 0 alone would let a missing value through.
    CONSTRAINT CK_Producer_Shape CHECK (
        (Kind = 'Llm'
            AND DirectiveSnapshotId IS NOT NULL AND DirectiveSnapshotId > 0
            AND ProviderSnapshotId IS NOT NULL
            AND AlgorithmName IS NULL AND AlgorithmVersion IS NULL AND ParamsHash IS NULL AND UserEmail IS NULL)
        OR
        (Kind = 'Algorithm'
            AND AlgorithmName IS NOT NULL AND LEN(LTRIM(RTRIM(AlgorithmName))) > 0
            AND AlgorithmVersion IS NOT NULL AND LEN(LTRIM(RTRIM(AlgorithmVersion))) > 0
            AND ParamsHash IS NOT NULL AND LEN(LTRIM(RTRIM(ParamsHash))) > 0
            AND DirectiveSnapshotId IS NULL AND ProviderSnapshotId IS NULL AND UserEmail IS NULL)
        OR
        (Kind = 'Manual'
            AND UserEmail IS NOT NULL AND LEN(LTRIM(RTRIM(UserEmail))) > 0
            AND DirectiveSnapshotId IS NULL AND ProviderSnapshotId IS NULL
            AND AlgorithmName IS NULL AND AlgorithmVersion IS NULL AND ParamsHash IS NULL)
    )
);
GO
