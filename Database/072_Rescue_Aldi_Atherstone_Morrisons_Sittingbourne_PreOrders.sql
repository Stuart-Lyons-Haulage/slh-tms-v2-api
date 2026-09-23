/*
   Targeted rescue for confirmed Aldi Atherstone and Morrisons Sittingbourne staged orders.

   Purpose:
   - Some confirmed orders were imported as PreOrder / plannerReady=false.
   - This makes only the matching pending staged records planner-approved so Order Review can approve/promote them.
   - SQL remains the authoritative source; this is idempotent and safe to rerun.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
BEGIN
    DECLARE @now nvarchar(40) = CONVERT(nvarchar(40), SYSUTCDATETIME(), 127);

    ;WITH target AS
    (
        SELECT Id, PayloadJson
        FROM dbo.StagedImports
        WHERE EntityType = N'order'
          AND Status = 0 -- PendingReview
          AND (
                (
                    PayloadJson LIKE N'%ALDI%'
                    AND PayloadJson LIKE N'%Atherstone%'
                )
                OR
                (
                    PayloadJson LIKE N'%MORRISONS%'
                    AND PayloadJson LIKE N'%Sittingbourne%'
                )
              )
          AND (
                JSON_VALUE(PayloadJson, '$.intakeStatus') = N'PreOrder'
                OR JSON_VALUE(PayloadJson, '$.plannerReady') = N'false'
                OR JSON_VALUE(PayloadJson, '$.plannerReady') = N'False'
              )
    )
    UPDATE target
       SET PayloadJson = JSON_MODIFY(
            JSON_MODIFY(
              JSON_MODIFY(
                JSON_MODIFY(PayloadJson, '$.plannerReady', CAST(1 AS bit)),
                '$.intakeStatus', N'PlannerApproved'),
              '$.plannerApprovalOverride', CAST(1 AS bit)),
            '$.plannerApprovedAtUtc', @now);

    INSERT dbo.StagedImportEvents (Id, StagedImportId, EventType, PreviousStatus, NewStatus, PayloadJson, Note, Actor, OccurredAtUtc)
    SELECT NEWID(), s.Id, N'PlannerApprovalRescue', 0, 0, s.PayloadJson,
           N'Targeted rescue: confirmed Aldi Atherstone / Morrisons Sittingbourne order was changed from PreOrder/not-planner-ready to PlannerApproved for manual approval.',
           N'Database/072_Rescue_Aldi_Atherstone_Morrisons_Sittingbourne_PreOrders.sql',
           SYSUTCDATETIME()
    FROM dbo.StagedImports s
    WHERE s.EntityType = N'order'
      AND s.Status = 0
      AND JSON_VALUE(s.PayloadJson, '$.plannerApprovalOverride') IN (N'true', N'True')
      AND JSON_VALUE(s.PayloadJson, '$.intakeStatus') = N'PlannerApproved'
      AND (
            (s.PayloadJson LIKE N'%ALDI%' AND s.PayloadJson LIKE N'%Atherstone%')
            OR (s.PayloadJson LIKE N'%MORRISONS%' AND s.PayloadJson LIKE N'%Sittingbourne%')
          )
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.StagedImportEvents e
          WHERE e.StagedImportId = s.Id
            AND e.EventType = N'PlannerApprovalRescue'
      );
END;

COMMIT TRANSACTION;
