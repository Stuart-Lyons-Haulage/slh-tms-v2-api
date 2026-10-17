/* Apply after 004_Driver_Mobile_Number.sql. Safe to rerun. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.TransportOrders', N'DeliveryWindowStartUtc') IS NULL
    ALTER TABLE dbo.TransportOrders ADD DeliveryWindowStartUtc datetimeoffset NULL;
IF COL_LENGTH(N'dbo.TransportOrders', N'DeliveryWindowEndUtc') IS NULL
    ALTER TABLE dbo.TransportOrders ADD DeliveryWindowEndUtc datetimeoffset NULL;

COMMIT TRANSACTION;
