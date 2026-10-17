-- Adds full cab phone and multi-provider fuel-card details to vehicle master data.
IF COL_LENGTH('dbo.Vehicles', 'CabMobile') IS NULL ALTER TABLE dbo.Vehicles ADD CabMobile nvarchar(40) NULL;
IF COL_LENGTH('dbo.Vehicles', 'FuelPin') IS NULL ALTER TABLE dbo.Vehicles ADD FuelPin nvarchar(80) NULL;
IF COL_LENGTH('dbo.Vehicles', 'ShellCard') IS NULL ALTER TABLE dbo.Vehicles ADD ShellCard nvarchar(80) NULL;
IF COL_LENGTH('dbo.Vehicles', 'BpRedCard') IS NULL ALTER TABLE dbo.Vehicles ADD BpRedCard nvarchar(80) NULL;
IF COL_LENGTH('dbo.Vehicles', 'BpPlainCard') IS NULL ALTER TABLE dbo.Vehicles ADD BpPlainCard nvarchar(80) NULL;
IF COL_LENGTH('dbo.Vehicles', 'Notes') IS NULL ALTER TABLE dbo.Vehicles ADD Notes nvarchar(500) NULL;
IF OBJECT_ID(N'dbo.FuelPrices', N'U') IS NULL
CREATE TABLE dbo.FuelPrices (
    Id uniqueidentifier NOT NULL,
    WeekCommencing date NOT NULL,
    Provider nvarchar(120) NOT NULL,
    PricePencePerLitre decimal(10,3) NOT NULL,
    IsPricingMaximum bit NOT NULL,
    Source nvarchar(200) NULL,
    Notes nvarchar(500) NULL,
    CreatedAtUtc datetimeoffset NOT NULL,
    CONSTRAINT PK_FuelPrices PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FuelPrices_Provider_WeekCommencing' AND object_id = OBJECT_ID(N'dbo.FuelPrices'))
CREATE UNIQUE INDEX IX_FuelPrices_Provider_WeekCommencing ON dbo.FuelPrices(Provider, WeekCommencing);
