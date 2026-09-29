/*
   Reconcile the duplicate Summer Berry customer created in operational master data.
   SUMMERBERRY is the canonical key used by the parser, seed data and route rules.
   The duplicate is archived rather than hard-deleted so audit/history remains safe.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
BEGIN
    DECLARE @canonicalId uniqueidentifier = (SELECT TOP (1) Id FROM dbo.Customers WHERE Code = N'SUMMERBERRY' ORDER BY Active DESC, Id);
    DECLARE @duplicateId uniqueidentifier = (SELECT TOP (1) Id FROM dbo.Customers WHERE Code IN (N'SUMMER-BERRY', N'SUMMER BERRY') ORDER BY Active DESC, Id);

    IF @canonicalId IS NULL AND @duplicateId IS NOT NULL
    BEGIN
        UPDATE dbo.Customers
        SET Code = N'SUMMERBERRY', Name = CASE WHEN NULLIF(LTRIM(RTRIM(Name)), N'') IS NULL OR Name IN (N'SUMMERBERRY', N'SUMMER-BERRY', N'SummerBerry', N'Summer Berry') THEN N'The Summer Berry Company' ELSE Name END,
            Active = 1
        WHERE Id = @duplicateId;
        SET @canonicalId = @duplicateId;
        SET @duplicateId = NULL;
    END;

    IF @canonicalId IS NOT NULL
    BEGIN
        UPDATE dbo.Customers SET Active = 1, Name = CASE WHEN NULLIF(LTRIM(RTRIM(Name)), N'') IS NULL OR Name IN (N'SUMMERBERRY', N'SUMMER-BERRY', N'SummerBerry', N'Summer Berry') THEN N'The Summer Berry Company' ELSE Name END WHERE Id = @canonicalId;
        UPDATE dbo.Sites SET CustomerCode = N'SUMMERBERRY' WHERE CustomerCode IN (N'SUMMER-BERRY', N'SUMMER BERRY');
        UPDATE dbo.CustomerContacts SET CustomerCode = N'SUMMERBERRY' WHERE CustomerCode IN (N'SUMMER-BERRY', N'SUMMER BERRY');
        UPDATE dbo.OrderMovements SET CustomerCode = N'SUMMERBERRY' WHERE CustomerCode IN (N'SUMMER-BERRY', N'SUMMER BERRY');
        UPDATE dbo.TransportOrders SET CustomerCode = N'SUMMERBERRY' WHERE CustomerCode IN (N'SUMMER-BERRY', N'SUMMER BERRY');
        UPDATE dbo.CustomerEmailRoutes SET CustomerCode = N'SUMMERBERRY' WHERE CustomerCode IN (N'SUMMER-BERRY', N'SUMMER BERRY');
        UPDATE dbo.OrderIntakeRouteRules SET CustomerCode = N'SUMMERBERRY' WHERE CustomerCode IN (N'SUMMER-BERRY', N'SUMMER BERRY');
        IF @duplicateId IS NOT NULL UPDATE dbo.Customers SET Active = 0 WHERE Id = @duplicateId;
    END;
END;

COMMIT TRANSACTION;
