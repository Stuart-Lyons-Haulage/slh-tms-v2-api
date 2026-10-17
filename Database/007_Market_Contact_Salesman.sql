IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.MarketContacts', N'Salesman') IS NULL
ALTER TABLE dbo.MarketContacts ADD Salesman nvarchar(200) NULL;
