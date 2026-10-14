IF OBJECT_ID(N'dbo.DriverAvailabilitySnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DriverAvailabilitySnapshots
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_DriverAvailabilitySnapshots PRIMARY KEY,
        PlanningDate date NOT NULL,
        GeneratedAtUtc datetimeoffset(7) NOT NULL,
        Source nvarchar(40) NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        StoredAtUtc datetimeoffset(7) NOT NULL CONSTRAINT DF_DriverAvailabilitySnapshots_StoredAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UX_DriverAvailabilitySnapshots_PlanningDate UNIQUE (PlanningDate)
    );
END;
