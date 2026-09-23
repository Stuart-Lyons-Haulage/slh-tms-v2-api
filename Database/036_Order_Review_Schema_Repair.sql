/*
   Repairs partially provisioned Order Control review/audit tables.
   This is deliberately idempotent and does not change customer/NWF order parsing,
   matching or identity rules.
*/
SET XACT_ABORT OFF;

DECLARE @changes table (SequenceNo int IDENTITY(1,1) NOT NULL, SqlText nvarchar(max) NOT NULL);

INSERT @changes (SqlText) VALUES
(N'IF OBJECT_ID(N''dbo.StagedImportEvents'', N''U'') IS NULL CREATE TABLE dbo.StagedImportEvents (Id uniqueidentifier NOT NULL CONSTRAINT PK_StagedImportEvents PRIMARY KEY, StagedImportId uniqueidentifier NOT NULL, EventType nvarchar(40) NOT NULL, PreviousStatus int NULL, NewStatus int NOT NULL, PayloadJson nvarchar(max) NOT NULL, Note nvarchar(1000) NULL, Actor nvarchar(200) NULL, OccurredAtUtc datetimeoffset NOT NULL CONSTRAINT DF_StagedImportEvents_OccurredAtUtc DEFAULT(SYSUTCDATETIME()))'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''StagedImportId'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD StagedImportId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''EventType'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD EventType nvarchar(40) NULL'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''PreviousStatus'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD PreviousStatus int NULL'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''NewStatus'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD NewStatus int NULL'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''PayloadJson'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD PayloadJson nvarchar(max) NULL'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''Note'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD Note nvarchar(1000) NULL'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''Actor'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD Actor nvarchar(200) NULL'),
(N'IF COL_LENGTH(N''dbo.StagedImportEvents'', N''OccurredAtUtc'') IS NULL ALTER TABLE dbo.StagedImportEvents ADD OccurredAtUtc datetimeoffset NULL'),
(N'IF OBJECT_ID(N''dbo.StagedImportEvents'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N''IX_StagedImportEvents_StagedImportId_OccurredAtUtc'' AND object_id=OBJECT_ID(N''dbo.StagedImportEvents'')) CREATE INDEX IX_StagedImportEvents_StagedImportId_OccurredAtUtc ON dbo.StagedImportEvents(StagedImportId, OccurredAtUtc)'),
(N'IF OBJECT_ID(N''dbo.StagedImportEvents'', N''U'') IS NOT NULL AND OBJECT_ID(N''dbo.StagedImports'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.StagedImportEvents'', N''StagedImportId'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N''FK_StagedImportEvents_StagedImports_StagedImportId'') ALTER TABLE dbo.StagedImportEvents WITH NOCHECK ADD CONSTRAINT FK_StagedImportEvents_StagedImports_StagedImportId FOREIGN KEY(StagedImportId) REFERENCES dbo.StagedImports(Id)'),

(N'IF OBJECT_ID(N''dbo.OrderMovements'', N''U'') IS NULL CREATE TABLE dbo.OrderMovements (Id uniqueidentifier NOT NULL CONSTRAINT PK_OrderMovements PRIMARY KEY, CustomerCode nvarchar(40) NOT NULL, StableMovementKey nvarchar(240) NOT NULL, CurrentRevisionId uniqueidentifier NULL, LifecycleStatus int NOT NULL CONSTRAINT DF_OrderMovements_LifecycleStatus DEFAULT(1), CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_OrderMovements_CreatedAtUtc DEFAULT(SYSUTCDATETIME()), UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_OrderMovements_UpdatedAtUtc DEFAULT(SYSUTCDATETIME()))'),
(N'IF COL_LENGTH(N''dbo.OrderMovements'', N''CustomerCode'') IS NULL ALTER TABLE dbo.OrderMovements ADD CustomerCode nvarchar(40) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderMovements'', N''StableMovementKey'') IS NULL ALTER TABLE dbo.OrderMovements ADD StableMovementKey nvarchar(240) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderMovements'', N''CurrentRevisionId'') IS NULL ALTER TABLE dbo.OrderMovements ADD CurrentRevisionId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.OrderMovements'', N''LifecycleStatus'') IS NULL ALTER TABLE dbo.OrderMovements ADD LifecycleStatus int NULL'),
(N'IF COL_LENGTH(N''dbo.OrderMovements'', N''CreatedAtUtc'') IS NULL ALTER TABLE dbo.OrderMovements ADD CreatedAtUtc datetimeoffset NULL'),
(N'IF COL_LENGTH(N''dbo.OrderMovements'', N''UpdatedAtUtc'') IS NULL ALTER TABLE dbo.OrderMovements ADD UpdatedAtUtc datetimeoffset NULL'),
(N'IF OBJECT_ID(N''dbo.OrderMovements'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N''IX_OrderMovements_CustomerCode_StableMovementKey'' AND object_id=OBJECT_ID(N''dbo.OrderMovements'')) CREATE UNIQUE INDEX IX_OrderMovements_CustomerCode_StableMovementKey ON dbo.OrderMovements(CustomerCode, StableMovementKey) WHERE CustomerCode IS NOT NULL AND StableMovementKey IS NOT NULL'),

(N'IF OBJECT_ID(N''dbo.OrderRevisions'', N''U'') IS NULL CREATE TABLE dbo.OrderRevisions (Id uniqueidentifier NOT NULL CONSTRAINT PK_OrderRevisions PRIMARY KEY, MovementId uniqueidentifier NOT NULL, StagedImportId uniqueidentifier NOT NULL, RevisionNumber int NOT NULL, MessageId nvarchar(500) NULL, AttachmentIdentity nvarchar(500) NULL, ParserTemplate nvarchar(120) NULL, ParserVersion nvarchar(40) NULL, PayloadJson nvarchar(max) NOT NULL, ReceivedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_OrderRevisions_ReceivedAtUtc DEFAULT(SYSUTCDATETIME()), SupersedesRevisionId uniqueidentifier NULL)'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''MovementId'') IS NULL ALTER TABLE dbo.OrderRevisions ADD MovementId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''StagedImportId'') IS NULL ALTER TABLE dbo.OrderRevisions ADD StagedImportId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''RevisionNumber'') IS NULL ALTER TABLE dbo.OrderRevisions ADD RevisionNumber int NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''MessageId'') IS NULL ALTER TABLE dbo.OrderRevisions ADD MessageId nvarchar(500) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''AttachmentIdentity'') IS NULL ALTER TABLE dbo.OrderRevisions ADD AttachmentIdentity nvarchar(500) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''ParserTemplate'') IS NULL ALTER TABLE dbo.OrderRevisions ADD ParserTemplate nvarchar(120) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''ParserVersion'') IS NULL ALTER TABLE dbo.OrderRevisions ADD ParserVersion nvarchar(40) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''PayloadJson'') IS NULL ALTER TABLE dbo.OrderRevisions ADD PayloadJson nvarchar(max) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''ReceivedAtUtc'') IS NULL ALTER TABLE dbo.OrderRevisions ADD ReceivedAtUtc datetimeoffset NULL'),
(N'IF COL_LENGTH(N''dbo.OrderRevisions'', N''SupersedesRevisionId'') IS NULL ALTER TABLE dbo.OrderRevisions ADD SupersedesRevisionId uniqueidentifier NULL'),
(N'IF OBJECT_ID(N''dbo.OrderRevisions'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N''IX_OrderRevisions_MovementId_RevisionNumber'' AND object_id=OBJECT_ID(N''dbo.OrderRevisions'')) CREATE UNIQUE INDEX IX_OrderRevisions_MovementId_RevisionNumber ON dbo.OrderRevisions(MovementId, RevisionNumber) WHERE MovementId IS NOT NULL AND RevisionNumber IS NOT NULL'),
(N'IF OBJECT_ID(N''dbo.OrderRevisions'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N''IX_OrderRevisions_StagedImportId'' AND object_id=OBJECT_ID(N''dbo.OrderRevisions'')) CREATE UNIQUE INDEX IX_OrderRevisions_StagedImportId ON dbo.OrderRevisions(StagedImportId) WHERE StagedImportId IS NOT NULL'),
(N'IF OBJECT_ID(N''dbo.OrderRevisions'', N''U'') IS NOT NULL AND OBJECT_ID(N''dbo.OrderMovements'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N''FK_OrderRevisions_OrderMovements_MovementId'') ALTER TABLE dbo.OrderRevisions WITH NOCHECK ADD CONSTRAINT FK_OrderRevisions_OrderMovements_MovementId FOREIGN KEY(MovementId) REFERENCES dbo.OrderMovements(Id)'),
(N'IF OBJECT_ID(N''dbo.OrderRevisions'', N''U'') IS NOT NULL AND OBJECT_ID(N''dbo.StagedImports'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N''FK_OrderRevisions_StagedImports_StagedImportId'') ALTER TABLE dbo.OrderRevisions WITH NOCHECK ADD CONSTRAINT FK_OrderRevisions_StagedImports_StagedImportId FOREIGN KEY(StagedImportId) REFERENCES dbo.StagedImports(Id)'),

(N'IF OBJECT_ID(N''dbo.OrderSourceLines'', N''U'') IS NULL CREATE TABLE dbo.OrderSourceLines (Id uniqueidentifier NOT NULL CONSTRAINT PK_OrderSourceLines PRIMARY KEY, RevisionId uniqueidentifier NOT NULL, SourceRowKey nvarchar(160) NOT NULL, CollectionSite nvarchar(200) NULL, DeliverySite nvarchar(200) NULL, CollectionDate date NULL, DeliveryDate date NULL, CollectionTimeFrom time NULL, CollectionTimeTo time NULL, PalletType nvarchar(40) NULL, Pallets int NULL, TemperatureRequirement nvarchar(80) NULL, LoadReference nvarchar(120) NULL, PayloadJson nvarchar(max) NOT NULL)'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''RevisionId'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD RevisionId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''SourceRowKey'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD SourceRowKey nvarchar(160) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''CollectionSite'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD CollectionSite nvarchar(200) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''DeliverySite'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD DeliverySite nvarchar(200) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''CollectionDate'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD CollectionDate date NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''DeliveryDate'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD DeliveryDate date NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''CollectionTimeFrom'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD CollectionTimeFrom time NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''CollectionTimeTo'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD CollectionTimeTo time NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''PalletType'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD PalletType nvarchar(40) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''Pallets'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD Pallets int NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''TemperatureRequirement'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD TemperatureRequirement nvarchar(80) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''LoadReference'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD LoadReference nvarchar(120) NULL'),
(N'IF COL_LENGTH(N''dbo.OrderSourceLines'', N''PayloadJson'') IS NULL ALTER TABLE dbo.OrderSourceLines ADD PayloadJson nvarchar(max) NULL'),
(N'IF OBJECT_ID(N''dbo.OrderSourceLines'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N''IX_OrderSourceLines_RevisionId_SourceRowKey'' AND object_id=OBJECT_ID(N''dbo.OrderSourceLines'')) CREATE UNIQUE INDEX IX_OrderSourceLines_RevisionId_SourceRowKey ON dbo.OrderSourceLines(RevisionId, SourceRowKey) WHERE RevisionId IS NOT NULL AND SourceRowKey IS NOT NULL'),
(N'IF OBJECT_ID(N''dbo.OrderSourceLines'', N''U'') IS NOT NULL AND OBJECT_ID(N''dbo.OrderRevisions'', N''U'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N''FK_OrderSourceLines_OrderRevisions_RevisionId'') ALTER TABLE dbo.OrderSourceLines WITH NOCHECK ADD CONSTRAINT FK_OrderSourceLines_OrderRevisions_RevisionId FOREIGN KEY(RevisionId) REFERENCES dbo.OrderRevisions(Id)'),

(N'IF OBJECT_ID(N''dbo.TransportOrders'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.TransportOrders'', N''SourceStagedImportId'') IS NULL ALTER TABLE dbo.TransportOrders ADD SourceStagedImportId uniqueidentifier NULL'),
(N'IF OBJECT_ID(N''dbo.TransportOrders'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.TransportOrders'', N''SourceMovementId'') IS NULL ALTER TABLE dbo.TransportOrders ADD SourceMovementId uniqueidentifier NULL'),
(N'IF OBJECT_ID(N''dbo.TransportOrders'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.TransportOrders'', N''SourceStagedImportId'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N''IX_TransportOrders_SourceStagedImportId'' AND object_id=OBJECT_ID(N''dbo.TransportOrders'')) CREATE INDEX IX_TransportOrders_SourceStagedImportId ON dbo.TransportOrders(SourceStagedImportId)'),
(N'IF OBJECT_ID(N''dbo.TransportOrders'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.TransportOrders'', N''SourceMovementId'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N''IX_TransportOrders_SourceMovementId'' AND object_id=OBJECT_ID(N''dbo.TransportOrders'')) CREATE INDEX IX_TransportOrders_SourceMovementId ON dbo.TransportOrders(SourceMovementId)'),
(N'IF OBJECT_ID(N''dbo.TransportOrders'', N''U'') IS NOT NULL AND OBJECT_ID(N''dbo.StagedImports'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.TransportOrders'', N''SourceStagedImportId'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N''FK_TransportOrders_StagedImports_SourceStagedImportId'') ALTER TABLE dbo.TransportOrders WITH NOCHECK ADD CONSTRAINT FK_TransportOrders_StagedImports_SourceStagedImportId FOREIGN KEY(SourceStagedImportId) REFERENCES dbo.StagedImports(Id)'),
(N'IF OBJECT_ID(N''dbo.TransportOrders'', N''U'') IS NOT NULL AND OBJECT_ID(N''dbo.OrderMovements'', N''U'') IS NOT NULL AND COL_LENGTH(N''dbo.TransportOrders'', N''SourceMovementId'') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name=N''FK_TransportOrders_OrderMovements_SourceMovementId'') ALTER TABLE dbo.TransportOrders WITH NOCHECK ADD CONSTRAINT FK_TransportOrders_OrderMovements_SourceMovementId FOREIGN KEY(SourceMovementId) REFERENCES dbo.OrderMovements(Id)');

DECLARE @sql nvarchar(max);
DECLARE repair_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT SqlText FROM @changes ORDER BY SequenceNo;
OPEN repair_cursor;
FETCH NEXT FROM repair_cursor INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        EXEC sys.sp_executesql @sql;
    END TRY
    BEGIN CATCH
        PRINT CONCAT('Order review schema repair: ', ERROR_MESSAGE());
    END CATCH;
    FETCH NEXT FROM repair_cursor INTO @sql;
END;
CLOSE repair_cursor;
DEALLOCATE repair_cursor;
