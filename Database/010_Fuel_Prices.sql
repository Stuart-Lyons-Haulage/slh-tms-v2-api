SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.FuelPrices', N'U') IS NULL
CREATE TABLE dbo.FuelPrices (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_FuelPrices PRIMARY KEY,
    WeekCommencing date NOT NULL,
    Provider nvarchar(120) NOT NULL,
    PricePencePerLitre decimal(10,2) NOT NULL,
    IsPricingMaximum bit NOT NULL CONSTRAINT DF_FuelPrices_IsPricingMaximum DEFAULT (0),
    Source nvarchar(200) NULL,
    Notes nvarchar(500) NULL,
    CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_FuelPrices_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FuelPrices_WeekCommencing_Provider' AND object_id = OBJECT_ID(N'dbo.FuelPrices'))
CREATE UNIQUE INDEX IX_FuelPrices_WeekCommencing_Provider ON dbo.FuelPrices(WeekCommencing, Provider);

COMMIT TRANSACTION;
