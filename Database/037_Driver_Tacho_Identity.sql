/*
   Persists the TachoMaster identity carried by Driver records.
   The guards keep this safe for databases that have already received part of
   the repair or are initialised more than once.
*/
IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Drivers', N'TachoMasterDriverId') IS NULL
        ALTER TABLE dbo.Drivers ADD TachoMasterDriverId nvarchar(80) NULL;

    IF COL_LENGTH(N'dbo.Drivers', N'TachoCardNumber') IS NULL
        ALTER TABLE dbo.Drivers ADD TachoCardNumber nvarchar(80) NULL;

    IF COL_LENGTH(N'dbo.Drivers', N'LastTachoSyncUtc') IS NULL
        ALTER TABLE dbo.Drivers ADD LastTachoSyncUtc datetimeoffset(7) NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'IX_Drivers_TachoMasterDriverId'
          AND object_id = OBJECT_ID(N'dbo.Drivers')
    )
        CREATE INDEX IX_Drivers_TachoMasterDriverId
            ON dbo.Drivers(TachoMasterDriverId)
            WHERE TachoMasterDriverId IS NOT NULL;
END;

-- Recover identities already written into promoted master-detail payloads before
-- the fields became persisted. Existing non-blank values always win.
IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
BEGIN
    ;WITH LatestDriverDetail AS
    (
        SELECT
            EmployeeNumber = COALESCE(
                JSON_VALUE(PayloadJson, '$.employeeNumber'),
                JSON_VALUE(PayloadJson, '$.driverId'),
                JSON_VALUE(PayloadJson, '$.payrollNumber')),
            TachoMasterDriverId = NULLIF(COALESCE(
                JSON_VALUE(PayloadJson, '$.tachoMasterDriverId'),
                JSON_VALUE(PayloadJson, '$.tachomasterDriverId')), N''),
            TachoCardNumber = NULLIF(COALESCE(
                JSON_VALUE(PayloadJson, '$.tachoCardNumber'),
                JSON_VALUE(PayloadJson, '$.cardNumber')), N''),
            LastTachoSyncUtc = TRY_CONVERT(
                datetimeoffset(7),
                JSON_VALUE(PayloadJson, '$.lastTachoSyncUtc')),
            rn = ROW_NUMBER() OVER
            (
                PARTITION BY COALESCE(
                    JSON_VALUE(PayloadJson, '$.employeeNumber'),
                    JSON_VALUE(PayloadJson, '$.driverId'),
                    JSON_VALUE(PayloadJson, '$.payrollNumber'))
                ORDER BY COALESCE(ReviewedAtUtc, ReceivedAtUtc) DESC
            )
        FROM dbo.StagedImports
        WHERE EntityType IN (N'masterdetail:driver', N'driver')
          AND Status = 3
          AND ISJSON(PayloadJson) = 1
    )
    UPDATE driver
    SET
        TachoMasterDriverId = COALESCE(
            NULLIF(driver.TachoMasterDriverId, N''),
            detail.TachoMasterDriverId),
        TachoCardNumber = COALESCE(
            NULLIF(driver.TachoCardNumber, N''),
            detail.TachoCardNumber),
        LastTachoSyncUtc = COALESCE(
            driver.LastTachoSyncUtc,
            detail.LastTachoSyncUtc)
    FROM dbo.Drivers AS driver
    INNER JOIN LatestDriverDetail AS detail
        ON detail.rn = 1
       AND detail.EmployeeNumber = driver.EmployeeNumber
    WHERE detail.TachoMasterDriverId IS NOT NULL
       OR detail.TachoCardNumber IS NOT NULL
       OR detail.LastTachoSyncUtc IS NOT NULL;
END;
