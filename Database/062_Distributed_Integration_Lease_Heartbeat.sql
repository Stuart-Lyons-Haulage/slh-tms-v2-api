IF OBJECT_ID(N'dbo.DistributedLease', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DistributedLease
    (
        LeaseId nvarchar(128) NOT NULL,
        AcquiredAt datetime2(7) NOT NULL,
        ExpiresAt datetime2(7) NOT NULL,
        InstanceId nvarchar(128) NOT NULL,
        CONSTRAINT PK_DistributedLease PRIMARY KEY (LeaseId)
    );
END;

IF COL_LENGTH(N'dbo.DistributedLease', N'RunId') IS NULL
BEGIN
    ALTER TABLE dbo.DistributedLease ADD RunId nvarchar(64) NULL;
END;

IF COL_LENGTH(N'dbo.DistributedLease', N'RunId') IS NOT NULL
BEGIN
    EXEC sp_executesql N'UPDATE dbo.DistributedLease SET RunId = CONVERT(nvarchar(64), NEWID()) WHERE RunId IS NULL;';
END;

IF COL_LENGTH(N'dbo.DistributedLease', N'RunId') IS NOT NULL
   AND EXISTS (
       SELECT 1
       FROM sys.columns
       WHERE object_id = OBJECT_ID(N'dbo.DistributedLease')
         AND name = N'RunId'
         AND is_nullable = 1
   )
BEGIN
    ALTER TABLE dbo.DistributedLease ALTER COLUMN RunId nvarchar(64) NOT NULL;
END;

IF COL_LENGTH(N'dbo.DistributedLease', N'HeartbeatAt') IS NULL
BEGIN
    ALTER TABLE dbo.DistributedLease ADD HeartbeatAt datetime2(7) NULL;
END;

IF COL_LENGTH(N'dbo.DistributedLease', N'HeartbeatAt') IS NOT NULL
BEGIN
    EXEC sp_executesql N'UPDATE dbo.DistributedLease SET HeartbeatAt = COALESCE(AcquiredAt, SYSUTCDATETIME()) WHERE HeartbeatAt IS NULL;';
END;

IF COL_LENGTH(N'dbo.DistributedLease', N'HeartbeatAt') IS NOT NULL
   AND EXISTS (
       SELECT 1
       FROM sys.columns
       WHERE object_id = OBJECT_ID(N'dbo.DistributedLease')
         AND name = N'HeartbeatAt'
         AND is_nullable = 1
   )
BEGIN
    ALTER TABLE dbo.DistributedLease ALTER COLUMN HeartbeatAt datetime2(7) NOT NULL;
END;
