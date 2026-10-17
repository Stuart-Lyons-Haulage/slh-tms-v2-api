IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.MarketContacts', N'MarketKey') IS NULL
        ALTER TABLE dbo.MarketContacts ADD MarketKey nvarchar(160) NULL;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name IN (N'IX_MarketContacts_Market_Name', N'IX_Repair_MarketContacts_Key') AND object_id = OBJECT_ID(N'dbo.MarketContacts'))
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MarketContacts_Market_Name' AND object_id = OBJECT_ID(N'dbo.MarketContacts'))
            DROP INDEX IX_MarketContacts_Market_Name ON dbo.MarketContacts;
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Repair_MarketContacts_Key' AND object_id = OBJECT_ID(N'dbo.MarketContacts'))
            DROP INDEX IX_Repair_MarketContacts_Key ON dbo.MarketContacts;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MarketContacts_Market_Name_Stand' AND object_id = OBJECT_ID(N'dbo.MarketContacts'))
        CREATE INDEX IX_MarketContacts_Market_Name_Stand ON dbo.MarketContacts(Market, Name, StandOrLocation);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_MarketContacts_MarketKey' AND object_id = OBJECT_ID(N'dbo.MarketContacts'))
        CREATE UNIQUE INDEX UX_MarketContacts_MarketKey ON dbo.MarketContacts(MarketKey) WHERE MarketKey IS NOT NULL;
END;
