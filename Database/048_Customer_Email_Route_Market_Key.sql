IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CustomerEmailRoutes', N'MarketKey') IS NULL
BEGIN
    ALTER TABLE dbo.CustomerEmailRoutes ADD MarketKey nvarchar(160) NULL;
END;
