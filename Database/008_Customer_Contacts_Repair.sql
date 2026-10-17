IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.CustomerContacts', N'Email') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD Email nvarchar(320) NULL;

    IF COL_LENGTH(N'dbo.CustomerContacts', N'MobileNumber') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD MobileNumber nvarchar(40) NULL;

    IF COL_LENGTH(N'dbo.CustomerContacts', N'ReceivesEtaUpdates') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_CustomerContacts_ReceivesEtaUpdates_Repair DEFAULT (1);

    IF COL_LENGTH(N'dbo.CustomerContacts', N'Active') IS NULL
    ALTER TABLE dbo.CustomerContacts ADD Active bit NOT NULL CONSTRAINT DF_CustomerContacts_Active_Repair DEFAULT (1);
END;
