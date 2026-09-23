IF OBJECT_ID(N'dbo.StagedImportEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StagedImportEvents
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_StagedImportEvents PRIMARY KEY,
        StagedImportId uniqueidentifier NOT NULL,
        EventType nvarchar(40) NOT NULL,
        PreviousStatus int NULL,
        NewStatus int NOT NULL,
        PayloadJson nvarchar(max) NOT NULL,
        Note nvarchar(1000) NULL,
        Actor nvarchar(200) NULL,
        OccurredAtUtc datetimeoffset NOT NULL CONSTRAINT DF_StagedImportEvents_OccurredAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_StagedImportEvents_StagedImports_StagedImportId
            FOREIGN KEY (StagedImportId) REFERENCES dbo.StagedImports(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StagedImportEvents_StagedImportId_OccurredAtUtc' AND object_id = OBJECT_ID(N'dbo.StagedImportEvents'))
    CREATE INDEX IX_StagedImportEvents_StagedImportId_OccurredAtUtc ON dbo.StagedImportEvents(StagedImportId, OccurredAtUtc);

IF COL_LENGTH(N'dbo.TransportOrders', N'SourceStagedImportId') IS NULL
    ALTER TABLE dbo.TransportOrders ADD SourceStagedImportId uniqueidentifier NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TransportOrders_SourceStagedImportId' AND object_id = OBJECT_ID(N'dbo.TransportOrders'))
    CREATE INDEX IX_TransportOrders_SourceStagedImportId ON dbo.TransportOrders(SourceStagedImportId);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TransportOrders_StagedImports_SourceStagedImportId')
    ALTER TABLE dbo.TransportOrders ADD CONSTRAINT FK_TransportOrders_StagedImports_SourceStagedImportId
        FOREIGN KEY (SourceStagedImportId) REFERENCES dbo.StagedImports(Id);
