IF OBJECT_ID(N'dbo.DistributedLease', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DistributedLease
    (
        LeaseId nvarchar(160) NOT NULL CONSTRAINT PK_DistributedLease PRIMARY KEY,
        AcquiredAt datetime2(7) NOT NULL,
        ExpiresAt datetime2(7) NOT NULL,
        InstanceId nvarchar(240) NOT NULL
    );
    CREATE INDEX IX_DistributedLease_ExpiresAt ON dbo.DistributedLease(ExpiresAt);
END;
