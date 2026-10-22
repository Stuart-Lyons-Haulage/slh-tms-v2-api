-- =============================================================================
-- VALIDATION_QUERIES.sql — Post-deployment monitoring for v2 confirmation workflow
-- Run these against the production Azure SQL database at T+0h, T+4h, T+24h, T+48h
-- to confirm the deployment is healthy.
-- =============================================================================

-- ─────────────────────────────────────────────────────────────────────────────
-- 1. SCHEMA VALIDATION — confirm migration columns exist
-- ─────────────────────────────────────────────────────────────────────────────
SELECT
    CASE WHEN COL_LENGTH(N'dbo.TransportOrders', N'ConfirmationStatus') IS NOT NULL THEN 'OK' ELSE 'MISSING' END AS ConfirmationStatus_Column,
    CASE WHEN COL_LENGTH(N'dbo.TransportOrders', N'ConfirmedAtUtc')     IS NOT NULL THEN 'OK' ELSE 'MISSING' END AS ConfirmedAtUtc_Column,
    CASE WHEN COL_LENGTH(N'dbo.TransportOrders', N'ConfirmedBy')        IS NOT NULL THEN 'OK' ELSE 'MISSING' END AS ConfirmedBy_Column;

-- Filtered index on ConfirmationStatus = 0 (AwaitingReview)
SELECT name, filter_definition
FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.TransportOrders')
  AND name = N'IX_TransportOrders_ConfirmationStatus';

-- ─────────────────────────────────────────────────────────────────────────────
-- 2. CONFIRMATION STATUS DISTRIBUTION — overview of the order queue
-- Run at each checkpoint to track orders flowing through the workflow.
-- ─────────────────────────────────────────────────────────────────────────────
SELECT
    ConfirmationStatus,
    CASE ConfirmationStatus
        WHEN 0 THEN 'AwaitingReview'
        WHEN 1 THEN 'Confirmed'
        WHEN 2 THEN 'Rejected'
        ELSE 'Unknown'
    END AS StatusLabel,
    COUNT(*) AS OrderCount,
    MIN(CreatedAtUtc) AS EarliestOrder,
    MAX(CreatedAtUtc) AS LatestOrder
FROM dbo.TransportOrders
WHERE CollectionDate >= CAST(GETUTCDATE() AS date)
GROUP BY ConfirmationStatus
ORDER BY ConfirmationStatus;

-- ─────────────────────────────────────────────────────────────────────────────
-- 3. PLANNING GATE INTEGRITY — orders on runs must be confirmed
-- Any rows returned here indicate the planning gate was bypassed.
-- Expected result: 0 rows. If rows appear, investigate immediately.
-- ─────────────────────────────────────────────────────────────────────────────
SELECT
    o.Id AS OrderId,
    o.Reference,
    o.CustomerCode,
    o.ConfirmationStatus,
    l.Id AS LoadId,
    l.Reference AS RunReference,
    l.Status AS RunStatus,
    l.PlanningDate
FROM dbo.LoadStops s
    INNER JOIN dbo.TransportOrders o ON o.Id = s.OrderId
    INNER JOIN dbo.Loads l ON l.Id = s.LoadId
WHERE o.ConfirmationStatus <> 1   -- Not Confirmed
  AND l.Status NOT IN (5)         -- Exclude Cancelled runs
  AND l.PlanningDate >= CAST(GETUTCDATE() AS date);

-- ─────────────────────────────────────────────────────────────────────────────
-- 4. CONFIRMATION ACTIVITY LOG — recent confirm/reject activity
-- Verifies the endpoints are being used and by whom.
-- ─────────────────────────────────────────────────────────────────────────────
SELECT TOP 50
    Id,
    Reference,
    CustomerCode,
    CollectionDate,
    ConfirmationStatus,
    CASE ConfirmationStatus
        WHEN 1 THEN 'Confirmed'
        WHEN 2 THEN 'Rejected'
        ELSE 'Other'
    END AS Action,
    ConfirmedBy,
    ConfirmedAtUtc
FROM dbo.TransportOrders
WHERE ConfirmedAtUtc IS NOT NULL
ORDER BY ConfirmedAtUtc DESC;

-- ─────────────────────────────────────────────────────────────────────────────
-- 5. STALE AWAITING-REVIEW CHECK — orders stuck without confirmation
-- Orders awaiting review for more than 24h may indicate a workflow problem.
-- ─────────────────────────────────────────────────────────────────────────────
SELECT
    Id,
    Reference,
    CustomerCode,
    CollectionDate,
    CreatedAtUtc,
    DATEDIFF(HOUR, CreatedAtUtc, GETUTCDATE()) AS HoursWaiting
FROM dbo.TransportOrders
WHERE ConfirmationStatus = 0  -- AwaitingReview
  AND CollectionDate >= CAST(GETUTCDATE() AS date)
  AND DATEDIFF(HOUR, CreatedAtUtc, GETUTCDATE()) > 24
ORDER BY CreatedAtUtc;

-- ─────────────────────────────────────────────────────────────────────────────
-- 6. STAGED IMPORT HEALTH — intake pipeline flowing
-- Confirms NWF and email intake are still producing staged imports.
-- ─────────────────────────────────────────────────────────────────────────────
SELECT
    Source,
    Status,
    COUNT(*) AS ImportCount,
    MAX(ReceivedAtUtc) AS MostRecent
FROM dbo.StagedImports
WHERE ReceivedAtUtc >= DATEADD(HOUR, -48, GETUTCDATE())
GROUP BY Source, Status
ORDER BY Source, Status;

-- ─────────────────────────────────────────────────────────────────────────────
-- 7. PLANNER SAVE PERFORMANCE — optimistic concurrency working
-- Check for recent concurrency conflicts (runs with RowVersion changes).
-- A healthy system has saves completing without conflicts most of the time.
-- ─────────────────────────────────────────────────────────────────────────────
SELECT
    l.Id,
    l.Reference,
    l.PlanningDate,
    l.Status,
    COUNT(s.Id) AS StopCount,
    l.RowVersion
FROM dbo.Loads l
    LEFT JOIN dbo.LoadStops s ON s.LoadId = l.Id
WHERE l.PlanningDate >= CAST(GETUTCDATE() AS date)
  AND l.Status NOT IN (5)  -- Exclude Cancelled
GROUP BY l.Id, l.Reference, l.PlanningDate, l.Status, l.RowVersion
ORDER BY l.PlanningDate, l.Reference;

-- ─────────────────────────────────────────────────────────────────────────────
-- 8. TRANSSHIPMENT / WAITROSE PARSER VALIDATION — Task 2 regression check
-- Orders with Waitrose-prefixed depot names should have correct site mapping.
-- ─────────────────────────────────────────────────────────────────────────────
SELECT TOP 20
    Id,
    Reference,
    CustomerCode,
    SellerName,
    MarketName,
    CollectionDate
FROM dbo.TransportOrders
WHERE (SellerName LIKE '%Waitrose%' OR MarketName LIKE '%Waitrose%')
  AND CollectionDate >= DATEADD(DAY, -7, CAST(GETUTCDATE() AS date))
ORDER BY CreatedAtUtc DESC;

-- ─────────────────────────────────────────────────────────────────────────────
-- MONITORING SCHEDULE
-- ─────────────────────────────────────────────────────────────────────────────
-- T+0h   (immediately after deploy): Run queries 1, 2, 6
--         Confirm schema exists, baseline status distribution, intake flowing.
--
-- T+4h   (first operational window): Run queries 2, 3, 4, 6
--         Verify orders are being confirmed, no gate bypasses, intake healthy.
--
-- T+24h  (full day cycle):           Run queries 2, 3, 4, 5, 6, 7, 8
--         Full validation including stale-check and planner save health.
--
-- T+48h  (sign-off):                 Run ALL queries
--         Final validation before declaring the deployment stable.
--
-- ALERT CONDITIONS:
--   - Query 1: Any column shows 'MISSING' → migration did not run
--   - Query 3: Any rows returned → planning gate bypass (P1)
--   - Query 5: More than 10 orders stale > 24h → workflow adoption issue
--   - Query 6: No recent staged imports → intake pipeline down (P1)
-- =============================================================================
