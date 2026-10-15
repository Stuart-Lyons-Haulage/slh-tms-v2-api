-- 72-hour SharePoint order archive metadata. Safe to run repeatedly.
IF OBJECT_ID(N'dbo.TransportOrders', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.TransportOrders', N'CompletedAtUtc') IS NULL ALTER TABLE dbo.TransportOrders ADD CompletedAtUtc datetimeoffset NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'ArchivedAtUtc') IS NULL ALTER TABLE dbo.TransportOrders ADD ArchivedAtUtc datetimeoffset NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'ArchiveLocation') IS NULL ALTER TABLE dbo.TransportOrders ADD ArchiveLocation nvarchar(500) NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'TotalValue') IS NULL ALTER TABLE dbo.TransportOrders ADD TotalValue decimal(18,2) NULL;
    IF COL_LENGTH(N'dbo.TransportOrders', N'ItemCount') IS NULL ALTER TABLE dbo.TransportOrders ADD ItemCount int NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_TransportOrders_ArchiveRetention' AND object_id=OBJECT_ID(N'dbo.TransportOrders')) CREATE INDEX IX_TransportOrders_ArchiveRetention ON dbo.TransportOrders(Status, CompletedAtUtc, ArchivedAtUtc) INCLUDE (Reference, CustomerCode);
END
