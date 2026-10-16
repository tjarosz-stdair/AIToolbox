-- Review of detected items: a revision counter, the link to a sheet view, the reason a disposition was chosen, an
-- append-only history of every change (the basis for undo), and room for items a person adds by hand (plan sections 6.11 and 7).
-- Additive: new columns and one new table. The one relaxed column (Family) only loosens a constraint.

ALTER TABLE ductwork.Entity ADD Revision INT NOT NULL CONSTRAINT DF_Entity_Revision DEFAULT (1);
ALTER TABLE ductwork.Entity ADD SheetViewId BIGINT NULL CONSTRAINT FK_Entity_SheetView REFERENCES ductwork.SheetView (SheetViewId);
ALTER TABLE ductwork.Entity ADD DispositionReason NVARCHAR(400) NULL;
GO

-- A detector family does not apply to something a person added. Detected items still always have one.
ALTER TABLE ductwork.Entity ALTER COLUMN Family VARCHAR(20) NULL;
GO
ALTER TABLE ductwork.Entity ADD CONSTRAINT CK_Entity_FamilyForDetections CHECK (Method = 'Manual' OR Family IS NOT NULL);
GO

CREATE INDEX IX_Entity_SheetView ON ductwork.Entity (SheetViewId) WHERE SheetViewId IS NOT NULL;
GO

-- Every change to an item, as it was before and as it became. Append-only: undo adds a revision, it never rewrites one.
CREATE TABLE ductwork.EntityRevision
(
    EntityRevisionId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EntityRevision PRIMARY KEY,
    JobId            BIGINT NOT NULL CONSTRAINT FK_EntityRevision_Job REFERENCES ductwork.Job (JobId),
    EntityId         BIGINT NOT NULL CONSTRAINT FK_EntityRevision_Entity REFERENCES ductwork.Entity (EntityId),
    RevisionNo       INT NOT NULL,
    Action           VARCHAR(12) NOT NULL,
    ChangedBy        NVARCHAR(256) NOT NULL,
    ChangedUtc       DATETIME2(7) NOT NULL CONSTRAINT DF_EntityRevision_ChangedUtc DEFAULT (SYSUTCDATETIME()),
    Reason           NVARCHAR(400) NULL,
    BeforeJson       NVARCHAR(MAX) NULL,
    AfterJson        NVARCHAR(MAX) NOT NULL,
    -- For an undo: the revision it reverted.
    UndoOfRevisionNo INT NULL,
    CONSTRAINT UQ_EntityRevision_Entity_No UNIQUE (EntityId, RevisionNo),
    CONSTRAINT CK_EntityRevision_Action CHECK (Action IN ('Added', 'Reviewed', 'Disposition', 'Edited', 'Undone')),
    CONSTRAINT CK_EntityRevision_Undo CHECK ((Action = 'Undone' AND UndoOfRevisionNo IS NOT NULL) OR (Action <> 'Undone' AND UndoOfRevisionNo IS NULL)),
    CONSTRAINT CK_EntityRevision_Before CHECK (Action = 'Added' OR BeforeJson IS NOT NULL)
);
CREATE INDEX IX_EntityRevision_Job_User ON ductwork.EntityRevision (JobId, ChangedBy, EntityRevisionId DESC);
GO

CREATE TRIGGER ductwork.TR_EntityRevision_AppendOnly ON ductwork.EntityRevision
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51013, 'The item history is append-only. Revisions cannot be changed or deleted.', 1;
END;
GO
