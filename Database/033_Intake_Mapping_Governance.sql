IF COL_LENGTH(N'dbo.Sites', N'OperationalRegion') IS NULL ALTER TABLE dbo.Sites ADD OperationalRegion nvarchar(80) NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'MappingKind') IS NULL ALTER TABLE dbo.IntegrationMappings ADD MappingKind nvarchar(40) NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'NormalizedExternalValue') IS NULL ALTER TABLE dbo.IntegrationMappings ADD NormalizedExternalValue nvarchar(300) NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'SenderPattern') IS NULL ALTER TABLE dbo.IntegrationMappings ADD SenderPattern nvarchar(320) NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'TemplateName') IS NULL ALTER TABLE dbo.IntegrationMappings ADD TemplateName nvarchar(120) NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'TemplateVersion') IS NULL ALTER TABLE dbo.IntegrationMappings ADD TemplateVersion nvarchar(40) NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'ConfidenceThreshold') IS NULL ALTER TABLE dbo.IntegrationMappings ADD ConfidenceThreshold decimal(5,4) NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'EffectiveFromUtc') IS NULL ALTER TABLE dbo.IntegrationMappings ADD EffectiveFromUtc datetimeoffset NULL;
IF COL_LENGTH(N'dbo.IntegrationMappings', N'EffectiveToUtc') IS NULL ALTER TABLE dbo.IntegrationMappings ADD EffectiveToUtc datetimeoffset NULL;

UPDATE dbo.IntegrationMappings
SET NormalizedExternalValue = LOWER(REPLACE(REPLACE(REPLACE(REPLACE(ExternalKey, N' ', N''), N'-', N''), N'_', N''), N'.', N''))
WHERE NormalizedExternalValue IS NULL;

DECLARE @SlhSiteId uniqueidentifier = (SELECT TOP (1) Id FROM dbo.Sites WHERE Name = N'SLH-Lyons Consolidation Centre FRV' OR ExternalCode = N'SLH-FRV' ORDER BY CASE WHEN Name = N'SLH-Lyons Consolidation Centre FRV' THEN 0 ELSE 1 END);
IF @SlhSiteId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.IntegrationMappings WHERE Provider = N'InfoMailbox' AND MappingKind = N'SiteAlias' AND NormalizedExternalValue = N'slhdistributioncentre' AND Active = 1)
    INSERT dbo.IntegrationMappings(Id, Provider, ExternalKey, ExternalLabel, TmsEntityType, TmsEntityId, Active, Notes, CreatedAtUtc, UpdatedAtUtc, UpdatedBy, MappingKind, NormalizedExternalValue, ConfidenceThreshold)
    VALUES(NEWID(), N'InfoMailbox', N'SLH Distribution Centre', N'SLH-Lyons Consolidation Centre FRV', N'Site', @SlhSiteId, 1, N'Confirmed canonical SLH distribution-centre alias.', SYSUTCDATETIME(), SYSUTCDATETIME(), N'schema-033', N'SiteAlias', N'slhdistributioncentre', 1.0);
