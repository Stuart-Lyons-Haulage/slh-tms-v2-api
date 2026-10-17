IF OBJECT_ID(N'dbo.DriverAvailabilityWindows', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DriverAvailabilityWindows
    (
        Id uniqueidentifier NOT NULL,
        DriverId uniqueidentifier NOT NULL,
        AvailableFromUtc datetimeoffset NOT NULL,
        AvailableUntilUtc datetimeoffset NOT NULL,
        Confirmed bit NOT NULL CONSTRAINT DF_DriverAvailabilityWindows_Confirmed DEFAULT (0),
        LongTermPlacement bit NOT NULL CONSTRAINT DF_DriverAvailabilityWindows_LongTermPlacement DEFAULT (0),
        PlacementEndDate date NULL,
        UsualDays nvarchar(80) NULL,
        Notes nvarchar(500) NULL,
        BookingReference nvarchar(160) NULL,
        CreatedBy nvarchar(160) NOT NULL,
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_DriverAvailabilityWindows_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedBy nvarchar(160) NOT NULL,
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_DriverAvailabilityWindows_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DriverAvailabilityWindows PRIMARY KEY (Id),
        CONSTRAINT FK_DriverAvailabilityWindows_Drivers_DriverId FOREIGN KEY (DriverId) REFERENCES dbo.Drivers(Id),
        CONSTRAINT CK_DriverAvailabilityWindows_ValidWindow CHECK (AvailableUntilUtc > AvailableFromUtc)
    );
END;

INSERT INTO dbo.DriverAvailabilityWindows
    (Id, DriverId, AvailableFromUtc, AvailableUntilUtc, Confirmed, LongTermPlacement, PlacementEndDate,
     UsualDays, Notes, BookingReference, CreatedBy, CreatedAtUtc, UpdatedBy, UpdatedAtUtc)
SELECT NEWID(),
       driver.Id,
       CAST(TRY_CONVERT(date, JSON_VALUE(staged.PayloadJson, '$.fromDate')) AS datetime2) AT TIME ZONE 'GMT Standard Time',
       DATEADD(day, 1, CAST(TRY_CONVERT(date, JSON_VALUE(staged.PayloadJson, '$.throughDate')) AS datetime2)) AT TIME ZONE 'GMT Standard Time',
       1, 0, NULL, NULL,
       N'Migrated from the existing Driver Dispatch agency roster.',
       CONCAT(N'legacy-roster:', JSON_VALUE(staged.PayloadJson, '$.weekStart')),
       COALESCE(JSON_VALUE(staged.PayloadJson, '$.addedBy'), N'Schema migration 086'),
       COALESCE(TRY_CONVERT(datetimeoffset, JSON_VALUE(staged.PayloadJson, '$.addedAtUtc')), SYSUTCDATETIME()),
       COALESCE(JSON_VALUE(staged.PayloadJson, '$.addedBy'), N'Schema migration 086'),
       COALESCE(TRY_CONVERT(datetimeoffset, JSON_VALUE(staged.PayloadJson, '$.addedAtUtc')), SYSUTCDATETIME())
FROM dbo.StagedImports staged
INNER JOIN dbo.Drivers driver
    ON driver.Id = TRY_CONVERT(uniqueidentifier, JSON_VALUE(staged.PayloadJson, '$.driverId'))
WHERE staged.EntityType = N'driverdispatchagencyroster'
  AND staged.Status = 3
  AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(staged.PayloadJson, '$.driverId')) IS NOT NULL
  AND TRY_CONVERT(date, JSON_VALUE(staged.PayloadJson, '$.fromDate')) IS NOT NULL
  AND TRY_CONVERT(date, JSON_VALUE(staged.PayloadJson, '$.throughDate')) IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.DriverAvailabilityWindows existing
      WHERE existing.DriverId = TRY_CONVERT(uniqueidentifier, JSON_VALUE(staged.PayloadJson, '$.driverId'))
        AND existing.BookingReference = CONCAT(N'legacy-roster:', JSON_VALUE(staged.PayloadJson, '$.weekStart'))
  );

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_DriverAvailabilityWindows_Driver_Window'
      AND object_id = OBJECT_ID(N'dbo.DriverAvailabilityWindows')
)
BEGIN
    CREATE INDEX IX_DriverAvailabilityWindows_Driver_Window
        ON dbo.DriverAvailabilityWindows (DriverId, AvailableFromUtc, AvailableUntilUtc)
        INCLUDE (Confirmed, LongTermPlacement, PlacementEndDate);
END;
