IF OBJECT_ID(N'[dbo].[MasterDataAudits]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MasterDataAudits]
    (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_MasterDataAudits] PRIMARY KEY,
        [EntityType] nvarchar(80) NOT NULL,
        [EntityId] uniqueidentifier NOT NULL,
        [Action] nvarchar(80) NOT NULL,
        [ChangesJson] nvarchar(4000) NULL,
        [ChangedBy] nvarchar(200) NULL,
        [ChangedAtUtc] datetimeoffset NOT NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MasterDataAudits_Entity_History' AND object_id = OBJECT_ID(N'[dbo].[MasterDataAudits]'))
    CREATE INDEX [IX_MasterDataAudits_Entity_History] ON [dbo].[MasterDataAudits] ([EntityType], [EntityId], [ChangedAtUtc]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MasterDataAudits_ChangedAtUtc' AND object_id = OBJECT_ID(N'[dbo].[MasterDataAudits]'))
    CREATE INDEX [IX_MasterDataAudits_ChangedAtUtc] ON [dbo].[MasterDataAudits] ([ChangedAtUtc]);
