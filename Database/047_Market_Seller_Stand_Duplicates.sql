IF OBJECT_ID(N'dbo.master_markets', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_master_markets_Market_Name' AND object_id = OBJECT_ID(N'dbo.master_markets'))
        DROP INDEX UX_master_markets_Market_Name ON dbo.master_markets;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_master_markets_Market_Name' AND object_id = OBJECT_ID(N'dbo.master_markets'))
        CREATE INDEX IX_master_markets_Market_Name ON dbo.master_markets(Market, [Name]);
END;
