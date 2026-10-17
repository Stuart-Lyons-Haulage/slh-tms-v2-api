/* Governed SharePoint projection for customer contacts. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.master_customer_contacts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.master_customer_contacts
    (
        ContactId nvarchar(40) NOT NULL CONSTRAINT PK_master_customer_contacts PRIMARY KEY,
        CustomerId nvarchar(40) NOT NULL,
        ContactName nvarchar(200) NOT NULL,
        Email nvarchar(320) NULL,
        MobileNumber nvarchar(40) NULL,
        ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_master_customer_contacts_ReceivesEtaUpdates DEFAULT 1,
        SharePointItemId int NOT NULL,
        LastSyncedAt datetime2(3) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_customer_contacts_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_master_customer_contacts_UpdatedAt DEFAULT SYSUTCDATETIME(),
        IsActive bit NOT NULL CONSTRAINT DF_master_customer_contacts_IsActive DEFAULT 1
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_master_customer_contacts_CustomerId' AND object_id = OBJECT_ID(N'dbo.master_customer_contacts'))
    CREATE INDEX IX_master_customer_contacts_CustomerId ON dbo.master_customer_contacts(CustomerId, IsActive);

EXEC(N'
CREATE OR ALTER VIEW dbo.vw_ActiveCustomerContacts
AS
SELECT ContactId, CustomerId, ContactName, Email, MobileNumber, ReceivesEtaUpdates,
       SharePointItemId, LastSyncedAt, CreatedAt, UpdatedAt, IsActive
FROM dbo.master_customer_contacts
WHERE IsActive = 1;
');

COMMIT TRANSACTION;
