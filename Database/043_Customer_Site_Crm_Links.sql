IF COL_LENGTH(N'dbo.Customers', N'TradingName') IS NULL
    ALTER TABLE dbo.Customers ADD TradingName nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.Customers', N'AccountOwner') IS NULL
    ALTER TABLE dbo.Customers ADD AccountOwner nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.Customers', N'ServiceNotes') IS NULL
    ALTER TABLE dbo.Customers ADD ServiceNotes nvarchar(1000) NULL;
IF COL_LENGTH(N'dbo.Customers', N'DefaultSiteCode') IS NULL
    ALTER TABLE dbo.Customers ADD DefaultSiteCode nvarchar(80) NULL;
IF COL_LENGTH(N'dbo.Sites', N'CustomerCode') IS NULL
    ALTER TABLE dbo.Sites ADD CustomerCode nvarchar(40) NULL;

IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerEmailRoutes
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CustomerEmailRoutes PRIMARY KEY,
        CustomerCode nvarchar(40) NOT NULL,
        SenderEmail nvarchar(320) NULL,
        SenderDomain nvarchar(320) NULL,
        SubjectContains nvarchar(200) NULL,
        ParserType nvarchar(120) NULL,
        DefaultSiteCode nvarchar(80) NULL,
        RequiresReview bit NOT NULL CONSTRAINT DF_CustomerEmailRoutes_RequiresReview DEFAULT(1),
        Active bit NOT NULL CONSTRAINT DF_CustomerEmailRoutes_Active DEFAULT(1)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customers_Code' AND object_id = OBJECT_ID(N'dbo.Customers'))
    CREATE UNIQUE INDEX IX_Customers_Code ON dbo.Customers(Code);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sites_CustomerCode_ExternalCode' AND object_id = OBJECT_ID(N'dbo.Sites'))
    CREATE INDEX IX_Sites_CustomerCode_ExternalCode ON dbo.Sites(CustomerCode, ExternalCode);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerEmailRoutes_CustomerCode_SenderEmail_SenderDomain' AND object_id = OBJECT_ID(N'dbo.CustomerEmailRoutes'))
    CREATE INDEX IX_CustomerEmailRoutes_CustomerCode_SenderEmail_SenderDomain ON dbo.CustomerEmailRoutes(CustomerCode, SenderEmail, SenderDomain);
