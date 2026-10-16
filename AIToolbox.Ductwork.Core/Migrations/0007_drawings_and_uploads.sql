-- Drawing intake: revisions, source files and resumable uploads (plan sections 6.8 and 7).
-- Additive only. The bytes live in the artifact store; SQL holds the key, the size and the hash.

-- A set of drawings for a job. Addenda become later revisions; diffing them is later work.
CREATE TABLE ductwork.DrawingSetRevision
(
    RevisionId           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DrawingSetRevision PRIMARY KEY,
    PublicId             UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_DrawingSetRevision_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_DrawingSetRevision_PublicId UNIQUE,
    JobId                BIGINT NOT NULL CONSTRAINT FK_DrawingSetRevision_Job REFERENCES ductwork.Job (JobId),
    RevisionNo           INT NOT NULL,
    SupersedesRevisionId BIGINT NULL CONSTRAINT FK_DrawingSetRevision_Supersedes REFERENCES ductwork.DrawingSetRevision (RevisionId),
    CreatedUtc           DATETIME2(7) NOT NULL CONSTRAINT DF_DrawingSetRevision_CreatedUtc DEFAULT (SYSUTCDATETIME()),
    CreatedBy            NVARCHAR(256) NOT NULL,
    CONSTRAINT UQ_DrawingSetRevision_Job_No UNIQUE (JobId, RevisionNo),
    CONSTRAINT CK_DrawingSetRevision_No CHECK (RevisionNo >= 1)
);
GO

-- An uploaded drawing file. A file whose content the job already holds is recorded too, pointing at the first copy
-- and storing no bytes, so the person sees what they uploaded and storage holds one copy.
CREATE TABLE ductwork.SourceFile
(
    SourceFileId            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SourceFile PRIMARY KEY,
    PublicId                UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_SourceFile_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_SourceFile_PublicId UNIQUE,
    JobId                   BIGINT NOT NULL CONSTRAINT FK_SourceFile_Job REFERENCES ductwork.Job (JobId),
    RevisionId              BIGINT NOT NULL CONSTRAINT FK_SourceFile_Revision REFERENCES ductwork.DrawingSetRevision (RevisionId),
    OriginalName            NVARCHAR(260) NOT NULL,
    Sha256                  BINARY(32) NOT NULL,
    Bytes                   BIGINT NOT NULL,
    ContentType             VARCHAR(100) NOT NULL,
    -- Opaque key inside the artifact store. Built by the server from ids, never from the file name.
    StorageKey              VARCHAR(200) NULL,
    DuplicateOfSourceFileId BIGINT NULL CONSTRAINT FK_SourceFile_Duplicate REFERENCES ductwork.SourceFile (SourceFileId),
    UploadedBy              NVARCHAR(256) NOT NULL,
    UploadedUtc             DATETIME2(7) NOT NULL CONSTRAINT DF_SourceFile_UploadedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_SourceFile_Bytes CHECK (Bytes > 0),
    CONSTRAINT CK_SourceFile_Name CHECK (LEN(LTRIM(RTRIM(OriginalName))) > 0),
    -- Exactly one of: it holds bytes, or it points at the file that does.
    CONSTRAINT CK_SourceFile_BytesOrDuplicate CHECK ((StorageKey IS NOT NULL AND DuplicateOfSourceFileId IS NULL) OR (StorageKey IS NULL AND DuplicateOfSourceFileId IS NOT NULL))
);
CREATE INDEX IX_SourceFile_Job_Hash ON ductwork.SourceFile (JobId, Sha256);
GO

-- A file arriving in pieces. ReceivedBytes is the authority on how much has been accepted: a crash between writing
-- and recording leaves extra bytes in staging, which the next piece truncates away.
CREATE TABLE ductwork.UploadSession
(
    UploadSessionId   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UploadSession PRIMARY KEY,
    PublicId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_UploadSession_PublicId DEFAULT (NEWID()) CONSTRAINT UQ_UploadSession_PublicId UNIQUE,
    JobId             BIGINT NOT NULL CONSTRAINT FK_UploadSession_Job REFERENCES ductwork.Job (JobId),
    OriginalName      NVARCHAR(260) NOT NULL,
    TotalBytes        BIGINT NOT NULL,
    ExpectedSha256    BINARY(32) NULL,
    ReceivedBytes     BIGINT NOT NULL CONSTRAINT DF_UploadSession_Received DEFAULT (0),
    Status            VARCHAR(10) NOT NULL CONSTRAINT DF_UploadSession_Status DEFAULT ('Open'),
    StagingKey        VARCHAR(200) NOT NULL,
    StartedBy         NVARCHAR(256) NOT NULL,
    -- The browser session that started it. Only that session may add pieces.
    StartedSessionId  NVARCHAR(100) NOT NULL,
    StartedUtc        DATETIME2(7) NOT NULL CONSTRAINT DF_UploadSession_StartedUtc DEFAULT (SYSUTCDATETIME()),
    LastActivityUtc   DATETIME2(7) NOT NULL CONSTRAINT DF_UploadSession_LastActivityUtc DEFAULT (SYSUTCDATETIME()),
    ExpiresUtc        DATETIME2(7) NOT NULL,
    SourceFileId      BIGINT NULL CONSTRAINT FK_UploadSession_SourceFile REFERENCES ductwork.SourceFile (SourceFileId),
    CONSTRAINT CK_UploadSession_Status CHECK (Status IN ('Open', 'Completed', 'Aborted', 'Expired')),
    CONSTRAINT CK_UploadSession_Total CHECK (TotalBytes > 0),
    CONSTRAINT CK_UploadSession_Received CHECK (ReceivedBytes >= 0 AND ReceivedBytes <= TotalBytes),
    CONSTRAINT CK_UploadSession_CompletedHasFile CHECK (Status <> 'Completed' OR SourceFileId IS NOT NULL)
);
CREATE INDEX IX_UploadSession_Job_Status ON ductwork.UploadSession (JobId, Status);
GO
