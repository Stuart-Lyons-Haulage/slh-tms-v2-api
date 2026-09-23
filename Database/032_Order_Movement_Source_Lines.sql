IF OBJECT_ID(N'dbo.OrderMovements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OrderMovements
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OrderMovements PRIMARY KEY,
        CustomerCode nvarchar(40) NOT NULL,
        StableMovementKey nvarchar(240) NOT NULL,
        CurrentRevisionId uniqueidentifier NULL,
        LifecycleStatus int NOT NULL CONSTRAINT DF_OrderMovements_LifecycleStatus DEFAULT(1),
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_OrderMovements_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_OrderMovements_UpdatedAtUtc DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX IX_OrderMovements_CustomerCode_StableMovementKey ON dbo.OrderMovements(CustomerCode, StableMovementKey);
END;

IF OBJECT_ID(N'dbo.OrderRevisions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OrderRevisions
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OrderRevisions PRIMARY KEY,
        MovementId uniqueidentifier NOT NULL,
        StagedImportId uniqueidentifier NOT NULL,
        RevisionNumber int NOT NULL,
        MessageId nvarchar(500) NULL,
        AttachmentIdentity nvarchar(500) NULL,
        ParserTemplate nvarchar(120) NULL,
        ParserVersion nvarchar(40) NULL,
        PayloadJson nvarchar(max) NOT NULL,
        ReceivedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_OrderRevisions_ReceivedAtUtc DEFAULT(SYSUTCDATETIME()),
        SupersedesRevisionId uniqueidentifier NULL,
        CONSTRAINT FK_OrderRevisions_OrderMovements_MovementId FOREIGN KEY(MovementId) REFERENCES dbo.OrderMovements(Id),
        CONSTRAINT FK_OrderRevisions_StagedImports_StagedImportId FOREIGN KEY(StagedImportId) REFERENCES dbo.StagedImports(Id),
        CONSTRAINT FK_OrderRevisions_OrderRevisions_SupersedesRevisionId FOREIGN KEY(SupersedesRevisionId) REFERENCES dbo.OrderRevisions(Id)
    );
    CREATE UNIQUE INDEX IX_OrderRevisions_MovementId_RevisionNumber ON dbo.OrderRevisions(MovementId, RevisionNumber);
    CREATE UNIQUE INDEX IX_OrderRevisions_StagedImportId ON dbo.OrderRevisions(StagedImportId);
END;

IF OBJECT_ID(N'dbo.OrderSourceLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OrderSourceLines
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OrderSourceLines PRIMARY KEY,
        RevisionId uniqueidentifier NOT NULL,
        SourceRowKey nvarchar(160) NOT NULL,
        CollectionSite nvarchar(200) NULL,
        DeliverySite nvarchar(200) NULL,
        CollectionDate date NULL,
        DeliveryDate date NULL,
        CollectionTimeFrom time NULL,
        CollectionTimeTo time NULL,
        PalletType nvarchar(40) NULL,
        Pallets int NULL,
        TemperatureRequirement nvarchar(80) NULL,
        LoadReference nvarchar(120) NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CONSTRAINT FK_OrderSourceLines_OrderRevisions_RevisionId FOREIGN KEY(RevisionId) REFERENCES dbo.OrderRevisions(Id)
    );
    CREATE UNIQUE INDEX IX_OrderSourceLines_RevisionId_SourceRowKey ON dbo.OrderSourceLines(RevisionId, SourceRowKey);
END;

IF COL_LENGTH(N'dbo.TransportOrders', N'SourceMovementId') IS NULL
    ALTER TABLE dbo.TransportOrders ADD SourceMovementId uniqueidentifier NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TransportOrders_SourceMovementId' AND object_id = OBJECT_ID(N'dbo.TransportOrders'))
    CREATE INDEX IX_TransportOrders_SourceMovementId ON dbo.TransportOrders(SourceMovementId);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TransportOrders_OrderMovements_SourceMovementId')
    ALTER TABLE dbo.TransportOrders ADD CONSTRAINT FK_TransportOrders_OrderMovements_SourceMovementId
        FOREIGN KEY(SourceMovementId) REFERENCES dbo.OrderMovements(Id);
