SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.TransportOrders', 'SellerName') IS NULL ALTER TABLE dbo.TransportOrders ADD SellerName nvarchar(200) NULL;
IF COL_LENGTH('dbo.TransportOrders', 'MarketName') IS NULL ALTER TABLE dbo.TransportOrders ADD MarketName nvarchar(80) NULL;
IF COL_LENGTH('dbo.TransportOrders', 'StallNumber') IS NULL ALTER TABLE dbo.TransportOrders ADD StallNumber nvarchar(200) NULL;
IF COL_LENGTH('dbo.TransportOrders', 'DriverInstructions') IS NULL ALTER TABLE dbo.TransportOrders ADD DriverInstructions nvarchar(1000) NULL;
IF COL_LENGTH('dbo.TransportOrders', 'MapLink') IS NULL ALTER TABLE dbo.TransportOrders ADD MapLink nvarchar(1000) NULL;
COMMIT TRANSACTION;
