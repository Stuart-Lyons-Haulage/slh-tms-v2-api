-- Master workbook fields retained for ongoing edits and exports.
IF COL_LENGTH('dbo.Drivers','Coding') IS NULL ALTER TABLE dbo.Drivers ADD Coding nvarchar(80) NULL;
IF COL_LENGTH('dbo.Drivers','AgencyName') IS NULL ALTER TABLE dbo.Drivers ADD AgencyName nvarchar(160) NULL;
IF COL_LENGTH('dbo.Drivers','NorthEligible') IS NULL ALTER TABLE dbo.Drivers ADD NorthEligible bit NULL;
IF COL_LENGTH('dbo.Drivers','PreloadEligible') IS NULL ALTER TABLE dbo.Drivers ADD PreloadEligible bit NULL;
IF COL_LENGTH('dbo.Drivers','Notes') IS NULL ALTER TABLE dbo.Drivers ADD Notes nvarchar(500) NULL;
IF COL_LENGTH('dbo.Drivers','TachoMasterDriverId') IS NULL ALTER TABLE dbo.Drivers ADD TachoMasterDriverId nvarchar(80) NULL;
IF COL_LENGTH('dbo.Drivers','DrivingLicenceNumber') IS NULL ALTER TABLE dbo.Drivers ADD DrivingLicenceNumber nvarchar(80) NULL;
IF COL_LENGTH('dbo.Drivers','LicenceExpiry') IS NULL ALTER TABLE dbo.Drivers ADD LicenceExpiry date NULL;
IF COL_LENGTH('dbo.Drivers','LicenceStatus') IS NULL ALTER TABLE dbo.Drivers ADD LicenceStatus nvarchar(40) NULL;
IF COL_LENGTH('dbo.Drivers','LastTachoSyncUtc') IS NULL ALTER TABLE dbo.Drivers ADD LastTachoSyncUtc datetimeoffset NULL;
IF COL_LENGTH('dbo.Trailers','Notes') IS NULL ALTER TABLE dbo.Trailers ADD Notes nvarchar(500) NULL;
IF COL_LENGTH('dbo.Sites','Aliases') IS NULL ALTER TABLE dbo.Sites ADD Aliases nvarchar(500) NULL;
IF COL_LENGTH('dbo.Sites','CustomField1') IS NULL ALTER TABLE dbo.Sites ADD CustomField1 nvarchar(200) NULL;
IF COL_LENGTH('dbo.Sites','CustomField2') IS NULL ALTER TABLE dbo.Sites ADD CustomField2 nvarchar(200) NULL;
IF COL_LENGTH('dbo.Sites','CustomField3') IS NULL ALTER TABLE dbo.Sites ADD CustomField3 nvarchar(200) NULL;
