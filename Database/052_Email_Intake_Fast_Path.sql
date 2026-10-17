IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CustomerEmailRoutes', N'DefaultDeliverySiteCode') IS NULL
    ALTER TABLE dbo.CustomerEmailRoutes ADD DefaultDeliverySiteCode nvarchar(80) NULL;

IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerEmailRoutes_Active_SenderEmail' AND object_id = OBJECT_ID(N'dbo.CustomerEmailRoutes'))
    CREATE INDEX IX_CustomerEmailRoutes_Active_SenderEmail ON dbo.CustomerEmailRoutes(Active, SenderEmail);

IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerEmailRoutes_Active_SenderDomain' AND object_id = OBJECT_ID(N'dbo.CustomerEmailRoutes'))
    CREATE INDEX IX_CustomerEmailRoutes_Active_SenderDomain ON dbo.CustomerEmailRoutes(Active, SenderDomain);
