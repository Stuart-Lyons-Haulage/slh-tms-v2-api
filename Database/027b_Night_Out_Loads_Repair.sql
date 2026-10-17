/*
  Guarantees the core planning tables required by Night Outs exist even when an
  earlier planning schema transaction failed on a legacy database. Safe to rerun.
*/
SET XACT_ABORT OFF;

IF OBJECT_ID(N'dbo.Loads', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Loads (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_Loads PRIMARY KEY,
        Reference nvarchar(80) NOT NULL,
        PlanningDate date NOT NULL,
        Status int NOT NULL,
        VehicleId uniqueidentifier NULL,
        DriverId uniqueidentifier NULL,
        TrailerId uniqueidentifier NULL,
        CreatedAtUtc datetimeoffset NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.LoadStops', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoadStops (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_LoadStops PRIMARY KEY,
        LoadId uniqueidentifier NOT NULL,
        OrderId uniqueidentifier NULL,
        Sequence int NOT NULL,
        Name nvarchar(200) NOT NULL,
        Address nvarchar(500) NULL,
        Latitude decimal(9,6) NULL,
        Longitude decimal(9,6) NULL,
        PlannedArrivalUtc datetimeoffset NULL,
        CONSTRAINT FK_LoadStops_Loads FOREIGN KEY (LoadId) REFERENCES dbo.Loads(Id) ON DELETE CASCADE
    );
END;

IF COL_LENGTH(N'dbo.Loads', N'PlannerNotes') IS NULL
    ALTER TABLE dbo.Loads ADD PlannerNotes nvarchar(max) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loads_Reference' AND object_id = OBJECT_ID(N'dbo.Loads'))
    CREATE UNIQUE INDEX IX_Loads_Reference ON dbo.Loads(Reference);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loads_PlanningDate' AND object_id = OBJECT_ID(N'dbo.Loads'))
    CREATE INDEX IX_Loads_PlanningDate ON dbo.Loads(PlanningDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoadStops_LoadId_Sequence' AND object_id = OBJECT_ID(N'dbo.LoadStops'))
    CREATE UNIQUE INDEX IX_LoadStops_LoadId_Sequence ON dbo.LoadStops(LoadId, Sequence);
