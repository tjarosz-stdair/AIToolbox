-- Issued reports (plan section 12). A report is a file made from the job's effective state at one moment: kept in the artifact store, described
-- here with its hash and the hash of the model it was drawn from, so two files made from the same state can be shown to agree. Reports are
-- records of what was issued: never changed and never deleted. They stay in place when a job's detail is archived and purged.

CREATE TABLE ductwork.JobReport
(
    ReportId          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobReport PRIMARY KEY,
    PublicId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_JobReport_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_JobReport_PublicId UNIQUE,
    JobId             BIGINT NOT NULL CONSTRAINT FK_JobReport_Job REFERENCES ductwork.Job (JobId),
    -- The completion version these reports belong to (the one the job becomes when it is completed).
    CompletionVersion INT NOT NULL,
    Format            VARCHAR(8) NOT NULL,
    FileName          NVARCHAR(200) NOT NULL,
    StorageKey        VARCHAR(200) NOT NULL,
    Sha256            BINARY(32) NOT NULL,
    Bytes             BIGINT NOT NULL,
    -- A hash of the content the file was drawn from (not of the file), the same for every format made from the same state.
    ModelHash         BINARY(32) NOT NULL,
    -- Whether nothing was still open when it was made. An issued report that is not final says so in its own text.
    IsFinal           BIT NOT NULL,
    GeneratedBy       NVARCHAR(256) NOT NULL,
    GeneratedUtc      DATETIME2(7) NOT NULL CONSTRAINT DF_JobReport_GeneratedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_JobReport_Format CHECK (Format IN ('xlsx', 'html')),
    CONSTRAINT CK_JobReport_Bytes CHECK (Bytes > 0)
);
CREATE INDEX IX_JobReport_Job ON ductwork.JobReport (JobId, ReportId DESC);
GO

CREATE TRIGGER ductwork.TR_JobReport_Immutable ON ductwork.JobReport
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51018, 'An issued report is a record and cannot be changed or deleted.', 1;
END;
GO
