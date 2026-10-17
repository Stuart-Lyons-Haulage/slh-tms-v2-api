/*
   Index the actual high-frequency planning queries. This schema uses Loads,
   RunOrderAllocations and ReceivedAtUtc; it does not have Load, PlanningDate on
   OrderMovements, or CreatedAtUtc on StagedImports.
*/
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.OrderMovements', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.OrderMovements', N'LifecycleStatus') IS NOT NULL
   AND COL_LENGTH(N'dbo.OrderMovements', N'CustomerCode') IS NOT NULL
   AND COL_LENGTH(N'dbo.OrderMovements', N'StableMovementKey') IS NOT NULL
   AND COL_LENGTH(N'dbo.OrderMovements', N'CurrentRevisionId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OrderMovements_LifecycleStatus_CustomerCode_StableMovementKey' AND object_id = OBJECT_ID(N'dbo.OrderMovements'))
BEGIN
    CREATE INDEX IX_OrderMovements_LifecycleStatus_CustomerCode_StableMovementKey
        ON dbo.OrderMovements(LifecycleStatus, CustomerCode, StableMovementKey)
        INCLUDE (CurrentRevisionId);
END;

IF OBJECT_ID(N'dbo.Loads', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Loads', N'VehicleId') IS NOT NULL
   AND COL_LENGTH(N'dbo.Loads', N'PlanningDate') IS NOT NULL
   AND COL_LENGTH(N'dbo.Loads', N'CreatedAtUtc') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loads_VehicleId_PlanningDate_CreatedAtUtc' AND object_id = OBJECT_ID(N'dbo.Loads'))
BEGIN
    CREATE INDEX IX_Loads_VehicleId_PlanningDate_CreatedAtUtc
        ON dbo.Loads(VehicleId, PlanningDate DESC, CreatedAtUtc DESC)
        INCLUDE (Reference, DriverId, TrailerId, Status);
END;

IF OBJECT_ID(N'dbo.RunOrderAllocations', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.RunOrderAllocations', N'OrderId') IS NOT NULL
   AND COL_LENGTH(N'dbo.RunOrderAllocations', N'RunId') IS NOT NULL
   AND COL_LENGTH(N'dbo.RunOrderAllocations', N'Pallets') IS NOT NULL
   AND COL_LENGTH(N'dbo.RunOrderAllocations', N'Trolleys') IS NOT NULL
   AND COL_LENGTH(N'dbo.RunOrderAllocations', N'Trays') IS NOT NULL
   AND COL_LENGTH(N'dbo.RunOrderAllocations', N'CapacityUnits') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunOrderAllocations_OrderId' AND object_id = OBJECT_ID(N'dbo.RunOrderAllocations'))
        DROP INDEX IX_RunOrderAllocations_OrderId ON dbo.RunOrderAllocations;

    CREATE INDEX IX_RunOrderAllocations_OrderId
        ON dbo.RunOrderAllocations(OrderId)
        INCLUDE (RunId, Pallets, Trolleys, Trays, CapacityUnits);
END;

/* The date/status index starts with PlanningDate, so the older single-column
   index is redundant and adds write cost. */
IF OBJECT_ID(N'dbo.Loads', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loads_PlanningDate' AND object_id = OBJECT_ID(N'dbo.Loads'))
    DROP INDEX IX_Loads_PlanningDate ON dbo.Loads;

/* Create the model's entity-first staging index before removing the older
   status-first equivalent. This also repairs installations where the EF
   migration that introduced the model index was not applied. */
IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.StagedImports', N'EntityType') IS NOT NULL
   AND COL_LENGTH(N'dbo.StagedImports', N'Status') IS NOT NULL
   AND COL_LENGTH(N'dbo.StagedImports', N'ReceivedAtUtc') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StagedImports_Entity_Status_ReceivedAtUtc' AND object_id = OBJECT_ID(N'dbo.StagedImports'))
BEGIN
    CREATE INDEX IX_StagedImports_Entity_Status_ReceivedAtUtc
        ON dbo.StagedImports(EntityType, Status, ReceivedAtUtc DESC);
END;

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StagedImports_Status_EntityType_ReceivedAtUtc' AND object_id = OBJECT_ID(N'dbo.StagedImports'))
    DROP INDEX IX_StagedImports_Status_EntityType_ReceivedAtUtc ON dbo.StagedImports;
