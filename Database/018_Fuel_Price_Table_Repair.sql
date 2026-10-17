/* Ensures the fuel-price register exists on partially provisioned databases. */
SET XACT_ABORT OFF;

IF OBJECT_ID(N'dbo.FuelPrices', N'U') IS NULL
BEGIN
    BEGIN TRY
        CREATE TABLE dbo.FuelPrices (
            Id uniqueidentifier NOT NULL CONSTRAINT PK_Repair_FuelPrices_018 PRIMARY KEY,
            WeekCommencing date NOT NULL,
            Provider nvarchar(120) NOT NULL,
            PricePencePerLitre decimal(10,2) NOT NULL,
            IsPricingMaximum bit NOT NULL CONSTRAINT DF_Repair_FuelPrices_Max_018 DEFAULT (0),
            Source nvarchar(200) NULL,
            Notes nvarchar(500) NULL,
            CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_Repair_FuelPrices_Created_018 DEFAULT (SYSUTCDATETIME())
        );
        CREATE UNIQUE INDEX IX_Repair_FuelPrices_Week_Provider ON dbo.FuelPrices(WeekCommencing, Provider);
    END TRY
    BEGIN CATCH
        PRINT ERROR_MESSAGE();
    END CATCH;
END;
