-- Evaluation harness (plan sections 4.5, 6.2, 6.13 and 7).
-- Gold sets carry a role. A Holdout set can be evaluated once: the first EvalRun spends it, and a
-- trigger refuses any later run against it.

CREATE TABLE ductwork.GoldSet
(
    GoldSetId            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GoldSet PRIMARY KEY,
    Name                 NVARCHAR(200) NOT NULL CONSTRAINT UQ_GoldSet_Name UNIQUE,
    Role                 VARCHAR(12) NOT NULL,
    SpentUtc             DATETIME2 NULL,
    SpentByEvalRunId     INT NULL,
    Notes                NVARCHAR(1000) NULL,
    CreatedUtc           DATETIME2 NOT NULL CONSTRAINT DF_GoldSet_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_GoldSet_Role CHECK (Role IN ('Development', 'Holdout', 'Regression')),
    CONSTRAINT CK_GoldSet_SpentOnlyHoldout CHECK (SpentUtc IS NULL OR Role = 'Holdout')
);
GO

CREATE TABLE ductwork.GoldLabel
(
    GoldLabelId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GoldLabel PRIMARY KEY,
    GoldSetId     INT NOT NULL CONSTRAINT FK_GoldLabel_GoldSet REFERENCES ductwork.GoldSet (GoldSetId),
    SheetRef      NVARCHAR(200) NULL,
    BuildingType  NVARCHAR(100) NULL,
    Type          NVARCHAR(100) NOT NULL,
    Category      NVARCHAR(100) NULL,
    Size          NVARCHAR(100) NULL,
    Quantity      FLOAT NOT NULL CONSTRAINT DF_GoldLabel_Quantity DEFAULT (1),
    BoxX0         FLOAT NULL,
    BoxY0         FLOAT NULL,
    BoxX1         FLOAT NULL,
    BoxY1         FLOAT NULL,
    VerifiedBy    NVARCHAR(256) NULL,
    VerifiedUtc   DATETIME2 NULL,
    CONSTRAINT CK_GoldLabel_Quantity CHECK (Quantity >= 0),
    CONSTRAINT CK_GoldLabel_Box CHECK (
        (BoxX0 IS NULL AND BoxY0 IS NULL AND BoxX1 IS NULL AND BoxY1 IS NULL)
        OR (BoxX0 IS NOT NULL AND BoxY0 IS NOT NULL AND BoxX1 IS NOT NULL AND BoxY1 IS NOT NULL))
);
CREATE INDEX IX_GoldLabel_GoldSet ON ductwork.GoldLabel (GoldSetId);
GO

CREATE TABLE ductwork.EvalRun
(
    EvalRunId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EvalRun PRIMARY KEY,
    GoldSetId       INT NOT NULL CONSTRAINT FK_EvalRun_GoldSet REFERENCES ductwork.GoldSet (GoldSetId),
    ProducerId      BIGINT NOT NULL CONSTRAINT FK_EvalRun_Producer REFERENCES ductwork.Producer (ProducerId),
    TaskKey         VARCHAR(60) NOT NULL,
    Family          VARCHAR(20) NULL,
    MatchMode       VARCHAR(10) NOT NULL,
    ContractVersion VARCHAR(20) NOT NULL,
    ParamsJson      NVARCHAR(MAX) NULL,
    Notes           NVARCHAR(1000) NULL,
    StartedUtc      DATETIME2 NOT NULL CONSTRAINT DF_EvalRun_StartedUtc DEFAULT (SYSUTCDATETIME()),
    FinishedUtc     DATETIME2 NULL,
    CONSTRAINT CK_EvalRun_MatchMode CHECK (MatchMode IN ('Count', 'Box'))
);
CREATE INDEX IX_EvalRun_GoldSet ON ductwork.EvalRun (GoldSetId);
GO

ALTER TABLE ductwork.GoldSet
    ADD CONSTRAINT FK_GoldSet_SpentBy FOREIGN KEY (SpentByEvalRunId) REFERENCES ductwork.EvalRun (EvalRunId);
GO

CREATE TABLE ductwork.EvalResult
(
    EvalResultId  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EvalResult PRIMARY KEY,
    EvalRunId     INT NOT NULL CONSTRAINT FK_EvalResult_EvalRun REFERENCES ductwork.EvalRun (EvalRunId),
    BuildingType  NVARCHAR(100) NULL,
    Type          NVARCHAR(100) NULL,
    TruePositives  FLOAT NOT NULL,
    FalsePositives FLOAT NOT NULL,
    FalseNegatives FLOAT NOT NULL,
    PrecisionRatio AS (CASE WHEN TruePositives + FalsePositives = 0 THEN NULL ELSE TruePositives / (TruePositives + FalsePositives) END),
    RecallRatio    AS (CASE WHEN TruePositives + FalseNegatives = 0 THEN NULL ELSE TruePositives / (TruePositives + FalseNegatives) END),
    CONSTRAINT CK_EvalResult_NonNegative CHECK (TruePositives >= 0 AND FalsePositives >= 0 AND FalseNegatives >= 0)
);
CREATE INDEX IX_EvalResult_EvalRun ON ductwork.EvalResult (EvalRunId);
GO

-- How well a detector's confidence predicts correctness (plan section 6.13). Review-level cutoffs
-- are derived from the latest calibration.
CREATE TABLE ductwork.CalibrationRun
(
    CalibrationRunId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CalibrationRun PRIMARY KEY,
    EvalRunId        INT NOT NULL CONSTRAINT FK_CalibrationRun_EvalRun REFERENCES ductwork.EvalRun (EvalRunId),
    Family           VARCHAR(20) NOT NULL,
    CreatedUtc       DATETIME2 NOT NULL CONSTRAINT DF_CalibrationRun_CreatedUtc DEFAULT (SYSUTCDATETIME())
);
GO

CREATE TABLE ductwork.CalibrationBin
(
    CalibrationRunId INT NOT NULL CONSTRAINT FK_CalibrationBin_Run REFERENCES ductwork.CalibrationRun (CalibrationRunId),
    BinIndex         INT NOT NULL,
    LowerBound       FLOAT NOT NULL,
    UpperBound       FLOAT NOT NULL,
    Predicted        INT NOT NULL,
    Correct          FLOAT NOT NULL,
    CONSTRAINT PK_CalibrationBin PRIMARY KEY (CalibrationRunId, BinIndex),
    CONSTRAINT CK_CalibrationBin_Range CHECK (LowerBound >= 0 AND UpperBound <= 1 AND LowerBound < UpperBound),
    CONSTRAINT CK_CalibrationBin_Counts CHECK (Predicted >= 0 AND Correct >= 0 AND Correct <= Predicted)
);
GO

-- A holdout set is evaluated once. The first run spends it and later runs are refused.
CREATE TRIGGER ductwork.TR_EvalRun_Holdout ON ductwork.EvalRun
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN ductwork.GoldSet g ON g.GoldSetId = i.GoldSetId
        WHERE g.Role = 'Holdout' AND g.SpentUtc IS NOT NULL)
       OR EXISTS (
        SELECT 1
        FROM inserted i
        JOIN ductwork.GoldSet g ON g.GoldSetId = i.GoldSetId
        WHERE g.Role = 'Holdout'
        GROUP BY i.GoldSetId
        HAVING COUNT(*) > 1)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51001, 'The holdout gold set has already been spent. Create a new holdout set.', 1;
    END;

    UPDATE g
    SET SpentUtc = SYSUTCDATETIME(), SpentByEvalRunId = i.EvalRunId
    FROM ductwork.GoldSet g
    JOIN inserted i ON i.GoldSetId = g.GoldSetId
    WHERE g.Role = 'Holdout';
END;
GO
