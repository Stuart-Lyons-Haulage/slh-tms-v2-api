/*
   Rejected order intake do-not-learn guard.

   Purpose:
   - Rejected orders must never become customer/email-route/site/route master-data truth.
   - Approved/promoted orders may continue to learn through the normal promotion path.
   - Rejected orders are retained as audit/parser evidence only.

   This script is idempotent and safe to rerun.
*/

SET XACT_ABORT ON;

BEGIN TRANSACTION;

/*
   Mark existing rejected staged orders so any later review/reporting/parser tools can
   distinguish negative examples from approved operational truth.
*/
IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
BEGIN
    UPDATE dbo.StagedImports
       SET PayloadJson = JSON_MODIFY(
             JSON_MODIFY(
               JSON_MODIFY(
                 JSON_MODIFY(
                   CASE WHEN ISJSON(PayloadJson) = 1 THEN PayloadJson ELSE N'{{}}' END,
                   '$.doNotLearn', CAST(1 AS bit)),
                 '$.badParseExample', CAST(1 AS bit)),
               '$.learningEligible', CAST(0 AS bit)),
             '$.learningSuppressedReason',
             COALESCE(NULLIF(ReviewNote, N''), N'Rejected by planner; retained for audit/parser evidence only.'))
     WHERE EntityType IN (N'order', N'communication')
       AND Status = 2 /* Rejected */
       AND ISJSON(PayloadJson) = 1
       AND COALESCE(JSON_VALUE(PayloadJson, '$.doNotLearn'), N'false') NOT IN (N'true', N'True', N'1');
END;

/*
   Add an audit event for rejected staged records missing an explicit negative-example
   marker. The NOT EXISTS keeps this idempotent.
*/
IF OBJECT_ID(N'dbo.StagedImportEvents', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
BEGIN
    INSERT dbo.StagedImportEvents
        (Id, StagedImportId, EventType, PreviousStatus, NewStatus, PayloadJson, Note, Actor, OccurredAtUtc)
    SELECT NEWID(),
           item.Id,
           N'LearningSuppressed',
           item.Status,
           item.Status,
           JSON_MODIFY(
             JSON_MODIFY(
               JSON_MODIFY(
                 CASE WHEN ISJSON(item.PayloadJson) = 1 THEN item.PayloadJson ELSE N'{{}}' END,
                 '$.doNotLearn', CAST(1 AS bit)),
               '$.badParseExample', CAST(1 AS bit)),
             '$.learningEligible', CAST(0 AS bit)),
           N'Rejected staged order retained as audit/parser evidence only; no customer, site, market or route master-data learning is allowed.',
           COALESCE(item.ReviewedBy, N'System'),
           SYSUTCDATETIME()
      FROM dbo.StagedImports item
     WHERE item.EntityType IN (N'order', N'communication')
       AND item.Status = 2 /* Rejected */
       AND NOT EXISTS
           (
               SELECT 1
                 FROM dbo.StagedImportEvents existing
                WHERE existing.StagedImportId = item.Id
                  AND existing.EventType = N'LearningSuppressed'
           );
END;

/*
   Optional hardening: if a future process stages a master-data learning row and marks
   it as being sourced from a rejected staged order, keep it in review by default.
*/
IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
BEGIN
    UPDATE dbo.StagedImports
       SET ReviewNote = CONCAT(COALESCE(NULLIF(ReviewNote, N''), N''),
           CASE WHEN COALESCE(NULLIF(ReviewNote, N''), N'') = N'' THEN N'' ELSE N' | ' END,
           N'Master-data learning suppressed because source was rejected.')
     WHERE EntityType IN (N'customer', N'customercontact', N'emailroute', N'site', N'marketcontact')
       AND Status = 0 /* PendingReview */
       AND ISJSON(PayloadJson) = 1
       AND COALESCE(JSON_VALUE(PayloadJson, '$.sourceRejected'), N'false') IN (N'true', N'True', N'1')
       AND COALESCE(JSON_VALUE(PayloadJson, '$.doNotLearn'), N'false') IN (N'true', N'True', N'1');
END;

COMMIT TRANSACTION;
GO

/*
   Future-proof the rejection path in SQL as well as the API. When a staged order or
   communication is rejected, stamp the payload before any later replay/reporting job
   can mistake it for learnable evidence.
*/
IF OBJECT_ID(N'dbo.TR_StagedImports_Rejected_DoNotLearn', N'TR') IS NOT NULL
    DROP TRIGGER dbo.TR_StagedImports_Rejected_DoNotLearn;
GO
CREATE TRIGGER dbo.TR_StagedImports_Rejected_DoNotLearn
ON dbo.StagedImports
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE target
       SET PayloadJson = JSON_MODIFY(
             JSON_MODIFY(
               JSON_MODIFY(
                 JSON_MODIFY(
                   CASE WHEN ISJSON(target.PayloadJson) = 1 THEN target.PayloadJson ELSE N'{{}}' END,
                   '$.doNotLearn', CAST(1 AS bit)),
                 '$.badParseExample', CAST(1 AS bit)),
               '$.learningEligible', CAST(0 AS bit)),
             '$.learningSuppressedReason',
             COALESCE(NULLIF(target.ReviewNote, N''), N'Rejected by planner; retained for audit/parser evidence only.'))
      FROM dbo.StagedImports target
      INNER JOIN inserted i ON i.Id = target.Id
      LEFT JOIN deleted d ON d.Id = target.Id
     WHERE target.EntityType IN (N'order', N'communication')
       AND i.Status = 2 /* Rejected */
       AND (d.Status IS NULL OR d.Status <> i.Status)
       AND ISJSON(target.PayloadJson) = 1;

    INSERT dbo.StagedImportEvents
        (Id, StagedImportId, EventType, PreviousStatus, NewStatus, PayloadJson, Note, Actor, OccurredAtUtc)
    SELECT NEWID(),
           target.Id,
           N'LearningSuppressed',
           d.Status,
           i.Status,
           target.PayloadJson,
           N'Rejected staged order retained as audit/parser evidence only; no customer, site, market or route master-data learning is allowed.',
           COALESCE(target.ReviewedBy, N'System'),
           SYSUTCDATETIME()
      FROM dbo.StagedImports target
      INNER JOIN inserted i ON i.Id = target.Id
      LEFT JOIN deleted d ON d.Id = target.Id
     WHERE target.EntityType IN (N'order', N'communication')
       AND i.Status = 2 /* Rejected */
       AND (d.Status IS NULL OR d.Status <> i.Status)
       AND NOT EXISTS
           (
               SELECT 1
                 FROM dbo.StagedImportEvents existing
                WHERE existing.StagedImportId = target.Id
                  AND existing.EventType = N'LearningSuppressed'
           );
END;
GO
