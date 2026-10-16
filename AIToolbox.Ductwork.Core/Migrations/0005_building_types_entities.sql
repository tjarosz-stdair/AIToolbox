-- Building types and multipliers, detected entities with enforced provenance, and findings
-- (plan sections 6.11, 7 and 12).

-- A group of identical buildings or units inside a job, counted once and multiplied. The multiplier is
-- user-confirmed input: a detector can only suggest it.
CREATE TABLE ductwork.BuildingType
(
    BuildingTypeId     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BuildingType PRIMARY KEY,
    PublicId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_BuildingType_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_BuildingType_PublicId UNIQUE,
    JobId              BIGINT NOT NULL CONSTRAINT FK_BuildingType_Job REFERENCES ductwork.Job (JobId),
    Name               NVARCHAR(100) NOT NULL,
    -- Unique within a job ignoring case and spacing, so a detector's "UNIT A" and a user's "Unit A" are one type.
    NameNormalized     NVARCHAR(100) NOT NULL,
    Kind               VARCHAR(12) NOT NULL CONSTRAINT DF_BuildingType_Kind DEFAULT ('Other'),
    -- The number of identical buildings or units. Null means not known yet. Zero is a valid, known count.
    Multiplier         INT NULL,
    MultiplierSource   VARCHAR(18) NULL,
    MultiplierEvidence NVARCHAR(1000) NULL,
    Confirmed          BIT NOT NULL CONSTRAINT DF_BuildingType_Confirmed DEFAULT (0),
    ConfirmedBy        NVARCHAR(256) NULL,
    ConfirmedUtc       DATETIME2(7) NULL,
    Notes              NVARCHAR(1000) NULL,
    CreatedUtc         DATETIME2(7) NOT NULL CONSTRAINT DF_BuildingType_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CreatedBy          NVARCHAR(256) NOT NULL,
    CONSTRAINT UQ_BuildingType_Job_Name UNIQUE (JobId, NameNormalized),
    CONSTRAINT CK_BuildingType_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
    CONSTRAINT CK_BuildingType_Kind CHECK (Kind IN ('UnitType', 'Building', 'CommonArea', 'Other')),
    CONSTRAINT CK_BuildingType_Source CHECK (MultiplierSource IS NULL OR MultiplierSource IN ('Manual', 'ArchitecturalList', 'Suggested')),
    CONSTRAINT CK_BuildingType_Multiplier CHECK (Multiplier IS NULL OR Multiplier >= 0),
    -- No value, no source; a value always says where it came from.
    CONSTRAINT CK_BuildingType_ValueHasSource CHECK ((Multiplier IS NULL AND MultiplierSource IS NULL) OR (Multiplier IS NOT NULL AND MultiplierSource IS NOT NULL)),
    -- A suggestion is never a confirmation: only a person's own entry (or the architectural list) can be confirmed.
    CONSTRAINT CK_BuildingType_ConfirmedNeedsPerson CHECK (
        Confirmed = 0 OR (Multiplier IS NOT NULL AND MultiplierSource IN ('Manual', 'ArchitecturalList') AND ConfirmedBy IS NOT NULL AND ConfirmedUtc IS NOT NULL))
);
GO

-- What a detector found, or what a person added. Every row says which producer made it and in which task.
CREATE TABLE ductwork.Entity
(
    EntityId         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Entity PRIMARY KEY,
    PublicId         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Entity_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_Entity_PublicId UNIQUE,
    JobId            BIGINT NOT NULL CONSTRAINT FK_Entity_Job REFERENCES ductwork.Job (JobId),
    BuildingTypeId   BIGINT NULL CONSTRAINT FK_Entity_BuildingType REFERENCES ductwork.BuildingType (BuildingTypeId),
    -- Provenance is not optional: an entity cannot exist without the producer and the task that made it.
    ProducerId       BIGINT NOT NULL CONSTRAINT FK_Entity_Producer REFERENCES ductwork.Producer (ProducerId),
    ProcessingTaskId BIGINT NOT NULL CONSTRAINT FK_Entity_ProcessingTask REFERENCES ductwork.ProcessingTask (ProcessingTaskId),
    DetectionId      NVARCHAR(100) NOT NULL,
    Type             NVARCHAR(100) NOT NULL,
    Category         NVARCHAR(100) NULL,
    Tag              NVARCHAR(100) NULL,
    Size             NVARCHAR(100) NULL,
    Manufacturer     NVARCHAR(100) NULL,
    Model            NVARCHAR(100) NULL,
    CustomerPartCode NVARCHAR(100) NULL,
    Quantity         DECIMAL(18,4) NOT NULL,
    Unit             VARCHAR(2) NOT NULL CONSTRAINT DF_Entity_Unit DEFAULT ('EA'),
    Method           VARCHAR(10) NOT NULL,
    Family           VARCHAR(20) NOT NULL,
    Confidence       FLOAT NOT NULL,
    SurfacedAs       VARCHAR(8) NOT NULL,
    ReviewLevelAtCreation VARCHAR(12) NOT NULL,
    -- Review status and disposition are independent: resolving one never approves the other.
    ReviewStatus     VARCHAR(8) NOT NULL CONSTRAINT DF_Entity_ReviewStatus DEFAULT ('Proposed'),
    Disposition      VARCHAR(16) NOT NULL CONSTRAINT DF_Entity_Disposition DEFAULT ('Included'),
    ResolutionSource VARCHAR(10) NOT NULL CONSTRAINT DF_Entity_ResolutionSource DEFAULT ('Detector'),
    IsStale          BIT NOT NULL CONSTRAINT DF_Entity_IsStale DEFAULT (0),
    StaleReason      NVARCHAR(400) NULL,
    SheetViewRef     NVARCHAR(200) NULL,
    BoxX0            FLOAT NULL,
    BoxY0            FLOAT NULL,
    BoxX1            FLOAT NULL,
    BoxY1            FLOAT NULL,
    Evidence         NVARCHAR(1000) NULL,
    CreatedUtc       DATETIME2(7) NOT NULL CONSTRAINT DF_Entity_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_Entity_Task_Detection UNIQUE (ProcessingTaskId, DetectionId),
    CONSTRAINT CK_Entity_Quantity CHECK (Quantity >= 0),
    CONSTRAINT CK_Entity_Unit CHECK (Unit IN ('EA', 'LF', 'SF')),
    CONSTRAINT CK_Entity_Confidence CHECK (Confidence >= 0 AND Confidence <= 1),
    CONSTRAINT CK_Entity_Method CHECK (Method IN ('Geometry', 'Text', 'FlagNote', 'Symbol', 'Schedule', 'Vision', 'Manual')),
    CONSTRAINT CK_Entity_SurfacedAs CHECK (SurfacedAs IN ('Finding', 'Batch')),
    CONSTRAINT CK_Entity_ReviewLevel CHECK (ReviewLevelAtCreation IN ('Sensitive', 'Normal', 'Insensitive')),
    CONSTRAINT CK_Entity_ReviewStatus CHECK (ReviewStatus IN ('Proposed', 'Verified', 'Rejected', 'Manual')),
    CONSTRAINT CK_Entity_Disposition CHECK (Disposition IN ('Included', 'Excluded', 'FactoryFurnished', 'TBD')),
    CONSTRAINT CK_Entity_Resolution CHECK (ResolutionSource IN ('Detector', 'Rule', 'Override', 'UserAdded')),
    CONSTRAINT CK_Entity_Box CHECK (
        (BoxX0 IS NULL AND BoxY0 IS NULL AND BoxX1 IS NULL AND BoxY1 IS NULL)
        OR (BoxX0 IS NOT NULL AND BoxY0 IS NOT NULL AND BoxX1 IS NOT NULL AND BoxY1 IS NOT NULL))
);
CREATE INDEX IX_Entity_Job ON ductwork.Entity (JobId, BuildingTypeId);
CREATE INDEX IX_Entity_Task ON ductwork.Entity (ProcessingTaskId);
GO

-- A question the detector could not answer for the user. It stays visible until resolved (plan section 6.11).
CREATE TABLE ductwork.Finding
(
    FindingId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Finding PRIMARY KEY,
    PublicId         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Finding_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_Finding_PublicId UNIQUE,
    JobId            BIGINT NOT NULL CONSTRAINT FK_Finding_Job REFERENCES ductwork.Job (JobId),
    ProcessingTaskId BIGINT NOT NULL CONSTRAINT FK_Finding_ProcessingTask REFERENCES ductwork.ProcessingTask (ProcessingTaskId),
    ProducerId       BIGINT NOT NULL CONSTRAINT FK_Finding_Producer REFERENCES ductwork.Producer (ProducerId),
    EntityId         BIGINT NULL CONSTRAINT FK_Finding_Entity REFERENCES ductwork.Entity (EntityId),
    SourceFindingId  NVARCHAR(100) NOT NULL,
    Kind             VARCHAR(24) NOT NULL,
    Question         NVARCHAR(1000) NOT NULL,
    CandidatesJson   NVARCHAR(MAX) NULL,
    Evidence         NVARCHAR(1000) NULL,
    State            VARCHAR(8) NOT NULL CONSTRAINT DF_Finding_State DEFAULT ('Open'),
    CreatedUtc       DATETIME2(7) NOT NULL CONSTRAINT DF_Finding_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_Finding_Task_Source UNIQUE (ProcessingTaskId, SourceFindingId),
    CONSTRAINT CK_Finding_Kind CHECK (Kind IN ('AmbiguousLabel', 'GrammarVariant', 'ReferenceDisagreement', 'SymbolOnly', 'MissingEvidence', 'LowConfidence', 'Other')),
    CONSTRAINT CK_Finding_State CHECK (State IN ('Open', 'Resolved', 'Waived')),
    CONSTRAINT CK_Finding_Question CHECK (LEN(LTRIM(RTRIM(Question))) > 0)
);
CREATE INDEX IX_Finding_Job ON ductwork.Finding (JobId, State);
GO
