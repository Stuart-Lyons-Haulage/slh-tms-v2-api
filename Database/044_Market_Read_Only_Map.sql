IF COL_LENGTH(N'dbo.MarketContacts', N'ReadOnlyMapPdfUrl') IS NULL
    ALTER TABLE dbo.MarketContacts ADD ReadOnlyMapPdfUrl nvarchar(1000) NULL;
