/* Apply after 005_Delivery_Windows.sql. Safe to rerun. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NULL
CREATE TABLE dbo.CustomerContacts (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_CustomerContacts PRIMARY KEY,
    CustomerCode nvarchar(40) NOT NULL,
    Name nvarchar(200) NOT NULL,
    Email nvarchar(320) NULL,
    MobileNumber nvarchar(40) NULL,
    ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_CustomerContacts_ReceivesEtaUpdates DEFAULT (1),
    Active bit NOT NULL CONSTRAINT DF_CustomerContacts_Active DEFAULT (1)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerContacts_CustomerCode_Name' AND object_id = OBJECT_ID(N'dbo.CustomerContacts'))
CREATE UNIQUE INDEX IX_CustomerContacts_CustomerCode_Name ON dbo.CustomerContacts(CustomerCode, Name);

COMMIT TRANSACTION;
