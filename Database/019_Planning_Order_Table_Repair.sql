/* Completes order/planning tables on partially provisioned production DBs. */
SET XACT_ABORT OFF;
DECLARE @changes table (SqlText nvarchar(max) NOT NULL);
INSERT @changes VALUES
(N'IF OBJECT_ID(N''dbo.TransportOrders'', N''U'') IS NULL CREATE TABLE dbo.TransportOrders (Id uniqueidentifier NOT NULL CONSTRAINT PK_Repair_TransportOrders_019 PRIMARY KEY, Reference nvarchar(80) NOT NULL, CustomerCode nvarchar(40) NOT NULL, CollectionDate date NOT NULL, DeliveryDate date NULL, DeliveryWindowStartUtc datetimeoffset NULL, DeliveryWindowEndUtc datetimeoffset NULL, Pallets int NULL, SellerName nvarchar(200) NULL, MarketName nvarchar(80) NULL, StallNumber nvarchar(200) NULL, DriverInstructions nvarchar(1000) NULL, MapLink nvarchar(1000) NULL, Status int NOT NULL CONSTRAINT DF_Repair_TransportOrders_Status_019 DEFAULT(0), CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_Repair_TransportOrders_Created_019 DEFAULT(SYSUTCDATETIME()))'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''SellerName'') IS NULL ALTER TABLE dbo.TransportOrders ADD SellerName nvarchar(200) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''MarketName'') IS NULL ALTER TABLE dbo.TransportOrders ADD MarketName nvarchar(80) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''StallNumber'') IS NULL ALTER TABLE dbo.TransportOrders ADD StallNumber nvarchar(200) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''DriverInstructions'') IS NULL ALTER TABLE dbo.TransportOrders ADD DriverInstructions nvarchar(1000) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''MapLink'') IS NULL ALTER TABLE dbo.TransportOrders ADD MapLink nvarchar(1000) NULL'),
(N'IF OBJECT_ID(N''dbo.Loads'', N''U'') IS NULL CREATE TABLE dbo.Loads (Id uniqueidentifier NOT NULL CONSTRAINT PK_Repair_Loads_019 PRIMARY KEY, Reference nvarchar(80) NOT NULL, PlanningDate date NOT NULL, Status int NOT NULL CONSTRAINT DF_Repair_Loads_Status_019 DEFAULT(0), VehicleId uniqueidentifier NULL, DriverId uniqueidentifier NULL, TrailerId uniqueidentifier NULL, CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_Repair_Loads_Created_019 DEFAULT(SYSUTCDATETIME()))'),
(N'IF OBJECT_ID(N''dbo.LoadStops'', N''U'') IS NULL CREATE TABLE dbo.LoadStops (Id uniqueidentifier NOT NULL CONSTRAINT PK_Repair_LoadStops_019 PRIMARY KEY, LoadId uniqueidentifier NOT NULL, OrderId uniqueidentifier NULL, Sequence int NOT NULL, Name nvarchar(200) NOT NULL, Address nvarchar(500) NULL, Latitude decimal(9,6) NULL, Longitude decimal(9,6) NULL, PlannedArrivalUtc datetimeoffset NULL)');
DECLARE @sql nvarchar(max);
DECLARE repair_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT SqlText FROM @changes;
OPEN repair_cursor; FETCH NEXT FROM repair_cursor INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY EXEC sys.sp_executesql @sql; END TRY BEGIN CATCH PRINT ERROR_MESSAGE(); END CATCH;
    FETCH NEXT FROM repair_cursor INTO @sql;
END;
CLOSE repair_cursor; DEALLOCATE repair_cursor;
