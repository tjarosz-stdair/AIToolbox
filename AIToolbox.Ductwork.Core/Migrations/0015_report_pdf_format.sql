-- A count sheet can now also be issued as a PDF. This only widens the set of formats a report may have, so every existing row stays valid
-- and an older build that never writes 'pdf' is unaffected.

ALTER TABLE ductwork.JobReport DROP CONSTRAINT CK_JobReport_Format;
GO
ALTER TABLE ductwork.JobReport ADD CONSTRAINT CK_JobReport_Format CHECK (Format IN ('xlsx', 'html', 'pdf'));
GO
