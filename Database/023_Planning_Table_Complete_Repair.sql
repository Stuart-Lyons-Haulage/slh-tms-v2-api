/*
  Completes every column read by the Planner on databases created by older
  revisions. Each statement is isolated so one legacy constraint cannot stop
  the remaining repairs.
*/
SET XACT_ABORT OFF;
DECLARE @planningChanges table (SqlText nvarchar(max) NOT NULL);
INSERT @planningChanges VALUES
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''Id'') IS NULL ALTER TABLE dbo.TransportOrders ADD Id uniqueidentifier NOT NULL CONSTRAINT DF_Repair_TransportOrders_Id_023 DEFAULT(NEWID())'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''Reference'') IS NULL ALTER TABLE dbo.TransportOrders ADD Reference nvarchar(80) NOT NULL CONSTRAINT DF_Repair_TransportOrders_Reference_023 DEFAULT(CONVERT(nvarchar(36),NEWID()))'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''CustomerCode'') IS NULL ALTER TABLE dbo.TransportOrders ADD CustomerCode nvarchar(40) NOT NULL CONSTRAINT DF_Repair_TransportOrders_Customer_023 DEFAULT(N''UNKNOWN'')'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''CollectionDate'') IS NULL ALTER TABLE dbo.TransportOrders ADD CollectionDate date NOT NULL CONSTRAINT DF_Repair_TransportOrders_Collection_023 DEFAULT(CONVERT(date,SYSUTCDATETIME()))'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''DeliveryDate'') IS NULL ALTER TABLE dbo.TransportOrders ADD DeliveryDate date NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''DeliveryWindowStartUtc'') IS NULL ALTER TABLE dbo.TransportOrders ADD DeliveryWindowStartUtc datetimeoffset NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''DeliveryWindowEndUtc'') IS NULL ALTER TABLE dbo.TransportOrders ADD DeliveryWindowEndUtc datetimeoffset NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''Pallets'') IS NULL ALTER TABLE dbo.TransportOrders ADD Pallets int NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''SellerName'') IS NULL ALTER TABLE dbo.TransportOrders ADD SellerName nvarchar(200) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''MarketName'') IS NULL ALTER TABLE dbo.TransportOrders ADD MarketName nvarchar(80) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''StallNumber'') IS NULL ALTER TABLE dbo.TransportOrders ADD StallNumber nvarchar(200) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''DriverInstructions'') IS NULL ALTER TABLE dbo.TransportOrders ADD DriverInstructions nvarchar(1000) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''MapLink'') IS NULL ALTER TABLE dbo.TransportOrders ADD MapLink nvarchar(1000) NULL'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''Status'') IS NULL ALTER TABLE dbo.TransportOrders ADD Status int NOT NULL CONSTRAINT DF_Repair_TransportOrders_Status_023 DEFAULT(1)'),
(N'IF COL_LENGTH(N''dbo.TransportOrders'', N''CreatedAtUtc'') IS NULL ALTER TABLE dbo.TransportOrders ADD CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_Repair_TransportOrders_Created_023 DEFAULT(SYSUTCDATETIME())'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''Id'') IS NULL ALTER TABLE dbo.Loads ADD Id uniqueidentifier NOT NULL CONSTRAINT DF_Repair_Loads_Id_023 DEFAULT(NEWID())'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''Reference'') IS NULL ALTER TABLE dbo.Loads ADD Reference nvarchar(80) NOT NULL CONSTRAINT DF_Repair_Loads_Reference_023 DEFAULT(CONVERT(nvarchar(36),NEWID()))'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''PlanningDate'') IS NULL ALTER TABLE dbo.Loads ADD PlanningDate date NOT NULL CONSTRAINT DF_Repair_Loads_PlanningDate_023 DEFAULT(CONVERT(date,SYSUTCDATETIME()))'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''Status'') IS NULL ALTER TABLE dbo.Loads ADD Status int NOT NULL CONSTRAINT DF_Repair_Loads_Status_023 DEFAULT(0)'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''VehicleId'') IS NULL ALTER TABLE dbo.Loads ADD VehicleId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''DriverId'') IS NULL ALTER TABLE dbo.Loads ADD DriverId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''TrailerId'') IS NULL ALTER TABLE dbo.Loads ADD TrailerId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.Loads'', N''CreatedAtUtc'') IS NULL ALTER TABLE dbo.Loads ADD CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_Repair_Loads_Created_023 DEFAULT(SYSUTCDATETIME())'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''Id'') IS NULL ALTER TABLE dbo.LoadStops ADD Id uniqueidentifier NOT NULL CONSTRAINT DF_Repair_LoadStops_Id_023 DEFAULT(NEWID())'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''LoadId'') IS NULL ALTER TABLE dbo.LoadStops ADD LoadId uniqueidentifier NOT NULL CONSTRAINT DF_Repair_LoadStops_LoadId_023 DEFAULT(NEWID())'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''OrderId'') IS NULL ALTER TABLE dbo.LoadStops ADD OrderId uniqueidentifier NULL'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''Sequence'') IS NULL ALTER TABLE dbo.LoadStops ADD Sequence int NOT NULL CONSTRAINT DF_Repair_LoadStops_Sequence_023 DEFAULT(0)'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''Name'') IS NULL ALTER TABLE dbo.LoadStops ADD Name nvarchar(200) NOT NULL CONSTRAINT DF_Repair_LoadStops_Name_023 DEFAULT(N''Unlabelled stop'')'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''Address'') IS NULL ALTER TABLE dbo.LoadStops ADD Address nvarchar(500) NULL'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''Latitude'') IS NULL ALTER TABLE dbo.LoadStops ADD Latitude decimal(9,6) NULL'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''Longitude'') IS NULL ALTER TABLE dbo.LoadStops ADD Longitude decimal(9,6) NULL'),
(N'IF COL_LENGTH(N''dbo.LoadStops'', N''PlannedArrivalUtc'') IS NULL ALTER TABLE dbo.LoadStops ADD PlannedArrivalUtc datetimeoffset NULL');

DECLARE @planningSql nvarchar(max);
DECLARE planning_repair_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT SqlText FROM @planningChanges;
OPEN planning_repair_cursor; FETCH NEXT FROM planning_repair_cursor INTO @planningSql;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY EXEC sys.sp_executesql @planningSql; END TRY BEGIN CATCH PRINT ERROR_MESSAGE(); END CATCH;
    FETCH NEXT FROM planning_repair_cursor INTO @planningSql;
END;
CLOSE planning_repair_cursor; DEALLOCATE planning_repair_cursor;
