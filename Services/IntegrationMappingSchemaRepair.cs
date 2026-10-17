using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class IntegrationMappingSchemaRepair
{
    internal const string RepairSql = """
IF OBJECT_ID(N'dbo.IntegrationMappings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IntegrationMappings (
        Id uniqueidentifier NOT NULL CONSTRAINT DF_IntegrationMappings_Id_Runtime DEFAULT (NEWID()),
        Provider nvarchar(40) NOT NULL,
        ExternalKey nvarchar(200) NOT NULL,
        ExternalLabel nvarchar(200) NULL,
        TmsEntityType nvarchar(20) NOT NULL,
        TmsEntityId uniqueidentifier NOT NULL,
        Active bit NOT NULL CONSTRAINT DF_IntegrationMappings_Active_Runtime DEFAULT (1),
        Notes nvarchar(1000) NULL,
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_CreatedAtUtc_Runtime DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_UpdatedAtUtc_Runtime DEFAULT (SYSUTCDATETIME()),
        UpdatedBy nvarchar(200) NULL,
        CONSTRAINT PK_IntegrationMappings_Runtime PRIMARY KEY (Id)
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'Id') IS NULL ALTER TABLE dbo.IntegrationMappings ADD Id uniqueidentifier NOT NULL CONSTRAINT DF_IntegrationMappings_Id_RuntimeRepair DEFAULT (NEWID()) WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'Provider') IS NULL ALTER TABLE dbo.IntegrationMappings ADD Provider nvarchar(40) NOT NULL CONSTRAINT DF_IntegrationMappings_Provider_RuntimeRepair DEFAULT (N'Unknown') WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'ExternalKey') IS NULL ALTER TABLE dbo.IntegrationMappings ADD ExternalKey nvarchar(200) NOT NULL CONSTRAINT DF_IntegrationMappings_ExternalKey_RuntimeRepair DEFAULT (N'') WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'ExternalLabel') IS NULL ALTER TABLE dbo.IntegrationMappings ADD ExternalLabel nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'TmsEntityType') IS NULL ALTER TABLE dbo.IntegrationMappings ADD TmsEntityType nvarchar(20) NOT NULL CONSTRAINT DF_IntegrationMappings_TmsEntityType_RuntimeRepair DEFAULT (N'Unknown') WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'TmsEntityId') IS NULL ALTER TABLE dbo.IntegrationMappings ADD TmsEntityId uniqueidentifier NOT NULL CONSTRAINT DF_IntegrationMappings_TmsEntityId_RuntimeRepair DEFAULT ('00000000-0000-0000-0000-000000000000') WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'Active') IS NULL ALTER TABLE dbo.IntegrationMappings ADD Active bit NOT NULL CONSTRAINT DF_IntegrationMappings_Active_RuntimeRepair DEFAULT (1) WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'Notes') IS NULL ALTER TABLE dbo.IntegrationMappings ADD Notes nvarchar(1000) NULL;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'CreatedAtUtc') IS NULL ALTER TABLE dbo.IntegrationMappings ADD CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_CreatedAtUtc_RuntimeRepair DEFAULT (SYSUTCDATETIME()) WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'UpdatedAtUtc') IS NULL ALTER TABLE dbo.IntegrationMappings ADD UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_UpdatedAtUtc_RuntimeRepair DEFAULT (SYSUTCDATETIME()) WITH VALUES;
    IF COL_LENGTH(N'dbo.IntegrationMappings', N'UpdatedBy') IS NULL ALTER TABLE dbo.IntegrationMappings ADD UpdatedBy nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_IntegrationMappings_Provider_ExternalKey_Type'
      AND object_id = OBJECT_ID(N'dbo.IntegrationMappings')
)
AND NOT EXISTS (
    SELECT 1
    FROM dbo.IntegrationMappings
    WHERE Active = 1
    GROUP BY Provider, ExternalKey, TmsEntityType
    HAVING COUNT(*) > 1
)
CREATE UNIQUE INDEX IX_IntegrationMappings_Provider_ExternalKey_Type
    ON dbo.IntegrationMappings(Provider, ExternalKey, TmsEntityType)
    WHERE Active = 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_IntegrationMappings_TmsEntityId'
      AND object_id = OBJECT_ID(N'dbo.IntegrationMappings')
)
CREATE INDEX IX_IntegrationMappings_TmsEntityId ON dbo.IntegrationMappings(TmsEntityId);
""";

    public static async Task<bool> EnsureAsync(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return true;

        try
        {
            await db.Database.ExecuteSqlRawAsync(RepairSql, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "IntegrationMappings schema repair failed; Fleetio will continue with deterministic identity matching.");
            return false;
        }
    }
}
