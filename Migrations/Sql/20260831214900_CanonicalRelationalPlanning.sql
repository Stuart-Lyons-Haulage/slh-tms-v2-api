/*
  Canonical relational planning model.
  Mirrors EF Core migration 20260831214900_CanonicalRelationalPlanning.
  Additive only: legacy Loads/LoadStops, Planning Register and planner audit remain untouched.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Runs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Runs
    (
        RunId uniqueidentifier NOT NULL,
        PlanningDate date NOT NULL,
        RunReference nvarchar(80) NOT NULL,
        Status nvarchar(32) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        UpdatedAt datetimeoffset NOT NULL,
        UpdatedBy nvarchar(200) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_Runs PRIMARY KEY (RunId)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Runs_PlanningDate_RunReference' AND object_id = OBJECT_ID(N'dbo.Runs'))
    CREATE UNIQUE INDEX UX_Runs_PlanningDate_RunReference ON dbo.Runs(PlanningDate, RunReference);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Runs_Status' AND object_id = OBJECT_ID(N'dbo.Runs'))
    CREATE INDEX IX_Runs_Status ON dbo.Runs(Status);

IF OBJECT_ID(N'dbo.RunStops', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RunStops
    (
        RunStopId uniqueidentifier NOT NULL,
        RunId uniqueidentifier NOT NULL,
        Sequence int NOT NULL,
        SiteId uniqueidentifier NOT NULL,
        PlannedArrival datetimeoffset NULL,
        PlannedDeparture datetimeoffset NULL,
        ActualArrival datetimeoffset NULL,
        ActualDeparture datetimeoffset NULL,
        GeofenceVisitId uniqueidentifier NULL,
        CONSTRAINT PK_RunStops PRIMARY KEY (RunStopId),
        CONSTRAINT FK_RunStops_Runs_RunId FOREIGN KEY (RunId) REFERENCES dbo.Runs(RunId) ON DELETE CASCADE,
        CONSTRAINT FK_RunStops_Sites_SiteId FOREIGN KEY (SiteId) REFERENCES dbo.Sites(Id),
        CONSTRAINT FK_RunStops_GeofenceVisits_GeofenceVisitId FOREIGN KEY (GeofenceVisitId) REFERENCES dbo.GeofenceVisits(Id) ON DELETE SET NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RunStops_RunId_Sequence' AND object_id = OBJECT_ID(N'dbo.RunStops'))
    CREATE UNIQUE INDEX UX_RunStops_RunId_Sequence ON dbo.RunStops(RunId, Sequence);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunStops_SiteId' AND object_id = OBJECT_ID(N'dbo.RunStops'))
    CREATE INDEX IX_RunStops_SiteId ON dbo.RunStops(SiteId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RunStops_GeofenceVisitId' AND object_id = OBJECT_ID(N'dbo.RunStops'))
    CREATE UNIQUE INDEX UX_RunStops_GeofenceVisitId ON dbo.RunStops(GeofenceVisitId) WHERE GeofenceVisitId IS NOT NULL;

IF OBJECT_ID(N'dbo.RunOrderAllocations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RunOrderAllocations
    (
        AllocationId uniqueidentifier NOT NULL,
        RunId uniqueidentifier NOT NULL,
        OrderId uniqueidentifier NOT NULL,
        Pallets int NOT NULL,
        Trolleys int NOT NULL,
        Trays int NOT NULL,
        CapacityUnits decimal(10,2) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        UpdatedAt datetimeoffset NOT NULL,
        UpdatedBy nvarchar(200) NULL,
        SourceRevisionId uniqueidentifier NOT NULL,
        CONSTRAINT PK_RunOrderAllocations PRIMARY KEY (AllocationId),
        CONSTRAINT FK_RunOrderAllocations_Runs_RunId FOREIGN KEY (RunId) REFERENCES dbo.Runs(RunId) ON DELETE CASCADE,
        CONSTRAINT FK_RunOrderAllocations_TransportOrders_OrderId FOREIGN KEY (OrderId) REFERENCES dbo.TransportOrders(Id),
        CONSTRAINT FK_RunOrderAllocations_OrderRevisions_SourceRevisionId FOREIGN KEY (SourceRevisionId) REFERENCES dbo.OrderRevisions(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RunOrderAllocations_RunId_OrderId' AND object_id = OBJECT_ID(N'dbo.RunOrderAllocations'))
    CREATE UNIQUE INDEX UX_RunOrderAllocations_RunId_OrderId ON dbo.RunOrderAllocations(RunId, OrderId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunOrderAllocations_OrderId' AND object_id = OBJECT_ID(N'dbo.RunOrderAllocations'))
    CREATE INDEX IX_RunOrderAllocations_OrderId ON dbo.RunOrderAllocations(OrderId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunOrderAllocations_SourceRevisionId' AND object_id = OBJECT_ID(N'dbo.RunOrderAllocations'))
    CREATE INDEX IX_RunOrderAllocations_SourceRevisionId ON dbo.RunOrderAllocations(SourceRevisionId);

IF OBJECT_ID(N'dbo.RunResourceAllocations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RunResourceAllocations
    (
        ResourceAllocationId uniqueidentifier NOT NULL,
        RunId uniqueidentifier NOT NULL,
        DriverId uniqueidentifier NOT NULL,
        VehicleId uniqueidentifier NOT NULL,
        TrailerId uniqueidentifier NOT NULL,
        AllocatedAt datetimeoffset NOT NULL,
        AllocatedBy nvarchar(200) NULL,
        CONSTRAINT PK_RunResourceAllocations PRIMARY KEY (ResourceAllocationId),
        CONSTRAINT FK_RunResourceAllocations_Runs_RunId FOREIGN KEY (RunId) REFERENCES dbo.Runs(RunId) ON DELETE CASCADE,
        CONSTRAINT FK_RunResourceAllocations_Drivers_DriverId FOREIGN KEY (DriverId) REFERENCES dbo.Drivers(Id),
        CONSTRAINT FK_RunResourceAllocations_Vehicles_VehicleId FOREIGN KEY (VehicleId) REFERENCES dbo.Vehicles(Id),
        CONSTRAINT FK_RunResourceAllocations_Trailers_TrailerId FOREIGN KEY (TrailerId) REFERENCES dbo.Trailers(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RunResourceAllocations_RunId' AND object_id = OBJECT_ID(N'dbo.RunResourceAllocations'))
    CREATE UNIQUE INDEX UX_RunResourceAllocations_RunId ON dbo.RunResourceAllocations(RunId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunResourceAllocations_DriverId' AND object_id = OBJECT_ID(N'dbo.RunResourceAllocations'))
    CREATE INDEX IX_RunResourceAllocations_DriverId ON dbo.RunResourceAllocations(DriverId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunResourceAllocations_VehicleId' AND object_id = OBJECT_ID(N'dbo.RunResourceAllocations'))
    CREATE INDEX IX_RunResourceAllocations_VehicleId ON dbo.RunResourceAllocations(VehicleId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunResourceAllocations_TrailerId' AND object_id = OBJECT_ID(N'dbo.RunResourceAllocations'))
    CREATE INDEX IX_RunResourceAllocations_TrailerId ON dbo.RunResourceAllocations(TrailerId);

IF OBJECT_ID(N'dbo.RunStatusHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RunStatusHistory
    (
        HistoryId uniqueidentifier NOT NULL,
        RunId uniqueidentifier NOT NULL,
        Status nvarchar(32) NOT NULL,
        ChangedAt datetimeoffset NOT NULL,
        ChangedBy nvarchar(200) NULL,
        Note nvarchar(1000) NULL,
        CONSTRAINT PK_RunStatusHistory PRIMARY KEY (HistoryId),
        CONSTRAINT FK_RunStatusHistory_Runs_RunId FOREIGN KEY (RunId) REFERENCES dbo.Runs(RunId) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunStatusHistory_RunId_ChangedAt' AND object_id = OBJECT_ID(N'dbo.RunStatusHistory'))
    CREATE INDEX IX_RunStatusHistory_RunId_ChangedAt ON dbo.RunStatusHistory(RunId, ChangedAt);

IF OBJECT_ID(N'dbo.RunTrackingStates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RunTrackingStates
    (
        RunId uniqueidentifier NOT NULL,
        LastLatitude decimal(9,6) NULL,
        LastLongitude decimal(9,6) NULL,
        LastUpdated datetimeoffset NULL,
        ETAMinutes int NULL,
        TrackingSource nvarchar(80) NULL,
        CONSTRAINT PK_RunTrackingStates PRIMARY KEY (RunId),
        CONSTRAINT FK_RunTrackingStates_Runs_RunId FOREIGN KEY (RunId) REFERENCES dbo.Runs(RunId) ON DELETE CASCADE
    );
END;

/* Keep manual SQL application aligned with EF Core migration history. */
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.__EFMigrationsHistory
    (
        MigrationId nvarchar(150) NOT NULL,
        ProductVersion nvarchar(32) NOT NULL,
        CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY (MigrationId)
    );
END;

IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260831214900_CanonicalRelationalPlanning')
    INSERT INTO dbo.__EFMigrationsHistory(MigrationId, ProductVersion)
    VALUES (N'20260831214900_CanonicalRelationalPlanning', N'8.0.8');

COMMIT TRANSACTION;
