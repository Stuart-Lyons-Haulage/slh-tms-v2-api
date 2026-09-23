/* Safe, repeatable repair for legacy Azure SQL shapes.
   Every ALTER is isolated so one stale table/column/constraint cannot stop the rest. */
SET NOCOUNT ON;
SET XACT_ABORT OFF;

DECLARE @repairs TABLE (TableName sysname NOT NULL, ColumnName sysname NOT NULL, Definition nvarchar(400) NOT NULL);
INSERT @repairs (TableName, ColumnName, Definition) VALUES
('Drivers','Coding','nvarchar(80) NULL'),('Drivers','AgencyName','nvarchar(160) NULL'),('Drivers','NorthEligible','bit NULL'),('Drivers','PreloadEligible','bit NULL'),('Drivers','Notes','nvarchar(500) NULL'),('Drivers','TachoMasterDriverId','nvarchar(80) NULL'),('Drivers','DrivingLicenceNumber','nvarchar(80) NULL'),('Drivers','LicenceExpiry','date NULL'),('Drivers','LicenceStatus','nvarchar(40) NULL'),('Drivers','LastTachoSyncUtc','datetimeoffset NULL'),
('Trailers','Notes','nvarchar(500) NULL'),
('Sites','Aliases','nvarchar(500) NULL'),('Sites','CustomField1','nvarchar(200) NULL'),('Sites','CustomField2','nvarchar(200) NULL'),('Sites','CustomField3','nvarchar(200) NULL'),
('Vehicles','FleetioVor','bit NULL'),('Vehicles','FleetioPmiDueUtc','datetimeoffset NULL'),('Vehicles','FleetioMotDueUtc','datetimeoffset NULL'),('Vehicles','FleetioServiceStatus','nvarchar(160) NULL'),('Vehicles','FleetioLastSyncedUtc','datetimeoffset NULL');

DECLARE @table sysname, @column sysname, @definition nvarchar(400), @sql nvarchar(max);
DECLARE repair_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT TableName, ColumnName, Definition FROM @repairs;
OPEN repair_cursor;
FETCH NEXT FROM repair_cursor INTO @table, @column, @definition;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL AND COL_LENGTH(N'dbo.' + @table, @column) IS NULL
    BEGIN
        SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@table) + N' ADD ' + QUOTENAME(@column) + N' ' + @definition + N';';
        BEGIN TRY EXEC sys.sp_executesql @sql; END TRY BEGIN CATCH PRINT CONCAT('Skipped ', @table, '.', @column, ': ', ERROR_MESSAGE()); END CATCH;
    END;
    FETCH NEXT FROM repair_cursor INTO @table, @column, @definition;
END;
CLOSE repair_cursor;
DEALLOCATE repair_cursor;
