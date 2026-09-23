/* Apply after 003_Market_Order_Details.sql. Safe to rerun. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.Drivers', N'MobileNumber') IS NULL
    ALTER TABLE dbo.Drivers ADD MobileNumber nvarchar(40) NULL;

COMMIT TRANSACTION;
