-- Sheets, the views on them, and include/exclude regions (plan sections 6.3 and 7). Additive only.

-- One page of an uploaded drawing file. A page whose content a job already holds is flagged as a duplicate of the first
-- and left out of scope, so a unit plan repeated in several building files is read once.
CREATE TABLE ductwork.Sheet
(
    SheetId            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sheet PRIMARY KEY,
    PublicId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Sheet_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_Sheet_PublicId UNIQUE,
    JobId              BIGINT NOT NULL CONSTRAINT FK_Sheet_Job REFERENCES ductwork.Job (JobId),
    RevisionId         BIGINT NOT NULL CONSTRAINT FK_Sheet_Revision REFERENCES ductwork.DrawingSetRevision (RevisionId),
    SourceFileId       BIGINT NOT NULL CONSTRAINT FK_Sheet_SourceFile REFERENCES ductwork.SourceFile (SourceFileId),
    PageNo             INT NOT NULL,
    SheetNumber        NVARCHAR(40) NULL,
    Title              NVARCHAR(200) NULL,
    Discipline         VARCHAR(20) NULL,
    SheetKind          VARCHAR(10) NOT NULL CONSTRAINT DF_Sheet_Kind DEFAULT ('Other'),
    ContentHash        BINARY(32) NULL,
    DuplicateOfSheetId BIGINT NULL CONSTRAINT FK_Sheet_Duplicate REFERENCES ductwork.Sheet (SheetId),
    HasTextLayer       BIT NOT NULL CONSTRAINT DF_Sheet_HasText DEFAULT (1),
    -- Page size in PDF points before rotation. Coordinates in views, regions and boxes are in this space.
    WidthPts           FLOAT NOT NULL,
    HeightPts          FLOAT NOT NULL,
    Rotation           INT NOT NULL CONSTRAINT DF_Sheet_Rotation DEFAULT (0),
    InScope            BIT NOT NULL CONSTRAINT DF_Sheet_InScope DEFAULT (1),
    ProcessingTaskId   BIGINT NULL CONSTRAINT FK_Sheet_Task REFERENCES ductwork.ProcessingTask (ProcessingTaskId),
    CreatedBy          NVARCHAR(256) NOT NULL,
    CreatedUtc         DATETIME2(7) NOT NULL CONSTRAINT DF_Sheet_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_Sheet_File_Page UNIQUE (SourceFileId, PageNo),
    CONSTRAINT CK_Sheet_PageNo CHECK (PageNo >= 1),
    CONSTRAINT CK_Sheet_Kind CHECK (SheetKind IN ('Plan', 'Schedule', 'Legend', 'Notes', 'Detail', 'Riser', 'Other')),
    CONSTRAINT CK_Sheet_Size CHECK (WidthPts > 0 AND HeightPts > 0),
    CONSTRAINT CK_Sheet_Rotation CHECK (Rotation IN (0, 90, 180, 270))
);
CREATE INDEX IX_Sheet_Job ON ductwork.Sheet (JobId, RevisionId);
CREATE INDEX IX_Sheet_Job_Hash ON ductwork.Sheet (JobId, ContentHash) WHERE ContentHash IS NOT NULL;
GO

-- A region of a sheet that is read as one drawing with one scale. A sheet can hold several (plan, enlarged plan, detail).
-- Scale is never assumed: no value means the scale is not known yet.
CREATE TABLE ductwork.SheetView
(
    SheetViewId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SheetView PRIMARY KEY,
    PublicId             UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_SheetView_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_SheetView_PublicId UNIQUE,
    JobId                BIGINT NOT NULL CONSTRAINT FK_SheetView_Job REFERENCES ductwork.Job (JobId),
    SheetId              BIGINT NOT NULL CONSTRAINT FK_SheetView_Sheet REFERENCES ductwork.Sheet (SheetId),
    Title                NVARCHAR(200) NOT NULL,
    -- Null means the whole page.
    RegionPolygonJson    NVARCHAR(MAX) NULL,
    -- Real units per drawing unit, for example 48 for 1/4 inch = 1 foot. Null until stated or calibrated by a person.
    ScaleRatio           FLOAT NULL,
    ScaleMethod          VARCHAR(10) NULL,
    ScaleCalibrationJson NVARCHAR(MAX) NULL,
    ScaleSetBy           NVARCHAR(256) NULL,
    ScaleSetUtc          DATETIME2(7) NULL,
    Floor                NVARCHAR(50) NULL,
    ViewType             VARCHAR(10) NOT NULL CONSTRAINT DF_SheetView_Type DEFAULT ('Plan'),
    ExtractionMethod     VARCHAR(10) NOT NULL CONSTRAINT DF_SheetView_Method DEFAULT ('Manual'),
    BuildingTypeId       BIGINT NULL CONSTRAINT FK_SheetView_BuildingType REFERENCES ductwork.BuildingType (BuildingTypeId),
    CreatedBy            NVARCHAR(256) NOT NULL,
    CreatedUtc           DATETIME2(7) NOT NULL CONSTRAINT DF_SheetView_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_SheetView_Title CHECK (LEN(LTRIM(RTRIM(Title))) > 0),
    CONSTRAINT CK_SheetView_Type CHECK (ViewType IN ('Plan', 'Section', 'Elevation', 'Detail', 'Schedule', 'Riser', 'Other')),
    CONSTRAINT CK_SheetView_Method CHECK (ExtractionMethod IN ('Geometry', 'LabelCount', 'Schedule', 'Vision', 'Manual')),
    CONSTRAINT CK_SheetView_ScaleMethod CHECK (ScaleMethod IS NULL OR ScaleMethod IN ('Stated', 'Calibrated')),
    CONSTRAINT CK_SheetView_Scale CHECK (ScaleRatio IS NULL OR ScaleRatio > 0),
    -- A scale always says how it was obtained and who set it.
    CONSTRAINT CK_SheetView_ScaleProvenance CHECK ((ScaleRatio IS NULL AND ScaleMethod IS NULL AND ScaleSetBy IS NULL AND ScaleSetUtc IS NULL)
        OR (ScaleRatio IS NOT NULL AND ScaleMethod IS NOT NULL AND ScaleSetBy IS NOT NULL AND ScaleSetUtc IS NOT NULL))
);
CREATE INDEX IX_SheetView_Sheet ON ductwork.SheetView (SheetId);
GO

-- Part of a sheet to read or to skip (a legend, a revision cloud, another discipline drawn on the same page).
CREATE TABLE ductwork.SheetRegion
(
    SheetRegionId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SheetRegion PRIMARY KEY,
    PublicId      UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_SheetRegion_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_SheetRegion_PublicId UNIQUE,
    JobId         BIGINT NOT NULL CONSTRAINT FK_SheetRegion_Job REFERENCES ductwork.Job (JobId),
    SheetId       BIGINT NOT NULL CONSTRAINT FK_SheetRegion_Sheet REFERENCES ductwork.Sheet (SheetId),
    Kind          VARCHAR(7) NOT NULL,
    Reason        NVARCHAR(300) NULL,
    PolygonJson   NVARCHAR(MAX) NOT NULL,
    CreatedBy     NVARCHAR(256) NOT NULL,
    CreatedUtc    DATETIME2(7) NOT NULL CONSTRAINT DF_SheetRegion_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_SheetRegion_Kind CHECK (Kind IN ('Include', 'Exclude')),
    -- Leaving something out of the count is a decision that needs its reason on record.
    CONSTRAINT CK_SheetRegion_ExcludeReason CHECK (Kind = 'Include' OR LEN(LTRIM(RTRIM(ISNULL(Reason, '')))) > 0)
);
CREATE INDEX IX_SheetRegion_Sheet ON ductwork.SheetRegion (SheetId);
GO
