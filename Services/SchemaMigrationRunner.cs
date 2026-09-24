using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public sealed record SchemaMigrationDefinition(
    int Version,
    string Name,
    string ResourceName,
    string Sql,
    string Checksum);

internal sealed record AppliedSchemaMigration(int Version, string Name, string Checksum);

public sealed class SchemaMigrationException : Exception
{
    public SchemaMigrationException(int version, string name, string message, Exception innerException)
        : base(message, innerException)
    {
        Version = version;
        MigrationName = name;
    }

    public int Version { get; }
    public string MigrationName { get; }
}

/// <summary>
/// Applies the embedded SQL schema migrations exactly once and records their
/// immutable SHA256 checksums in dbo.SchemaMigration. The migration catalogue is
/// append-only: existing entries must never be reordered, renamed or edited after
/// they have been applied to an environment.
/// </summary>
public static class SchemaMigrationRunner
{
    private const string ResourcePrefix = "Slh.Tms.Api.Database.";
    private const string MigrationLockResource = "SLH.TMS.SchemaMigration";
    internal const string MarketContactsStableKeyPreparationSql = """
        IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.MarketContacts', N'MarketKey') IS NULL
        BEGIN
            ALTER TABLE dbo.MarketContacts ADD MarketKey nvarchar(160) NULL;
        END;
        """;
    internal const string IntakeMappingGovernancePreparationSql = """
        IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.Sites', N'OperationalRegion') IS NULL
        BEGIN
            ALTER TABLE dbo.Sites ADD OperationalRegion nvarchar(80) NULL;
        END;

        IF OBJECT_ID(N'dbo.IntegrationMappings', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'MappingKind') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD MappingKind nvarchar(40) NULL;
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'NormalizedExternalValue') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD NormalizedExternalValue nvarchar(300) NULL;
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'SenderPattern') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD SenderPattern nvarchar(320) NULL;
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'TemplateName') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD TemplateName nvarchar(120) NULL;
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'TemplateVersion') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD TemplateVersion nvarchar(40) NULL;
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'ConfidenceThreshold') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD ConfidenceThreshold decimal(5,4) NULL;
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'EffectiveFromUtc') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD EffectiveFromUtc datetimeoffset NULL;
            IF COL_LENGTH(N'dbo.IntegrationMappings', N'EffectiveToUtc') IS NULL
                ALTER TABLE dbo.IntegrationMappings ADD EffectiveToUtc datetimeoffset NULL;
        END;
        """;
    private const string IntakeMappingGovernanceMigration = "033_Intake_Mapping_Governance.sql";
    internal const string DriverTachoIdentityPreparationSql = """
        IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH(N'dbo.Drivers', N'TachoMasterDriverId') IS NULL
                ALTER TABLE dbo.Drivers ADD TachoMasterDriverId nvarchar(80) NULL;
            IF COL_LENGTH(N'dbo.Drivers', N'TachoCardNumber') IS NULL
                ALTER TABLE dbo.Drivers ADD TachoCardNumber nvarchar(80) NULL;
            IF COL_LENGTH(N'dbo.Drivers', N'LastTachoSyncUtc') IS NULL
                ALTER TABLE dbo.Drivers ADD LastTachoSyncUtc datetimeoffset(7) NULL;
        END;
        """;
    private const string DriverTachoIdentityMigration = "037_Driver_Tacho_Identity.sql";
    private const string MarketContactsStableKeyMigration = "049_Market_Contact_Stable_Key_And_Stands.sql";
    private static readonly IReadOnlySet<string> DeferredStartupMigrations = new HashSet<string>(StringComparer.Ordinal)
    {
        "042_Operational_Read_Performance_Indexes.sql",
        "043_Customer_Site_Crm_Links.sql",
        "060_TachoMaster_Job_Managed_Identity.sql"
    };

    private static readonly string[] OrderedMigrationFiles =
    [
        "000_Critical_Master_Site_Compatibility.sql",
        "000_Operational_Storage_Recovery.sql",
        "001_Initial_Tms_Schema.sql",
        "002_Planning_Loads_Schema.sql",
        "003_Market_Order_Details.sql",
        "004_Driver_Mobile_Number.sql",
        "005_Delivery_Windows.sql",
        "006_Customer_Contacts.sql",
        "007_Market_Contact_Salesman.sql",
        "008_Customer_Contacts_Repair.sql",
        "009_Market_Contact_Sender.sql",
        "010_Fuel_Prices.sql",
        "011_Master_Data_Column_Repair.sql",
        "012_Fleetio_Vehicle_Columns.sql",
        "013_Vehicle_Fuel_Card_Details.sql",
        "014_Master_Data_Table_Repair.sql",
        "015_Driver_Existing_Table_Repair.sql",
        "016_Master_Data_Existing_Table_Repair.sql",
        "017_Master_Data_Column_Repair_Retry.sql",
        "018_Fuel_Price_Table_Repair.sql",
        "019_Planning_Order_Table_Repair.sql",
        "019b_Master_Data_Audit.sql",
        "020_Master_Workbook_Detail_Columns.sql",
        "021_Fleetio_Compliance_Fields.sql",
        "022_Safe_Master_Field_Repair.sql",
        "023_Planning_Table_Complete_Repair.sql",
        "024_Integration_Mappings.sql",
        "025_Market_Stall_Backfill.sql",
        "026_Operations_Intelligence_Schema_Repair.sql",
        "027_Integration_Mappings_Repair.sql",
        "027b_Night_Out_Loads_Repair.sql",
        "028_Geofence_Runtime_Integrity_Repair.sql",
        "028_Geofence_Tables_Repair.sql",
        "029_Geofence_Site_Link_Repair.sql",
        "030_Geofence_Maintenance_Compatibility.sql",
        "031_Order_Import_Audit_History.sql",
        "032_Order_Movement_Source_Lines.sql",
        "033_Intake_Mapping_Governance.sql",
        "034_Order_Completeness_References.sql",
        "035_Planning_Optimiser.sql",
        "036_Order_Review_Schema_Repair.sql",
        "037_Driver_Tacho_Identity.sql",
        "038_Driver_Tacho_Identity_Repair.sql",
        "039_Canonical_Relational_Planning.sql",
        "040_Audit_Outbox.sql",
        "041_Distributed_Integration_Lease.sql",
        "042_Operational_Read_Performance_Indexes.sql",
        "043_Customer_Site_Crm_Links.sql",
        "044_Market_Read_Only_Map.sql",
        "045_Customer_Contacts_Master_Projection.sql",
        "046_Driver_Card_Read_And_Source_Detail.sql",
        "047_Market_Seller_Stand_Duplicates.sql",
        "048_Customer_Email_Route_Market_Key.sql",
        "049_Market_Contact_Stable_Key_And_Stands.sql",
        "050_Master_Vehicle_Source_Detail.sql",
        "051_Vehicle_Source_Detail.sql",
        "052_Email_Intake_Fast_Path.sql",
        "058_Operational_Compliance_Fields.sql",
        "059_RoadTech_Operational_Visits.sql",
        "060_TachoMaster_Job_Managed_Identity.sql",
        "061_Email_Intake_Mapping_V2.sql",
        "062_Distributed_Integration_Lease_Heartbeat.sql",
        "071_InfoMailbox_Market_Waitrose_Coop_MasterData.sql",
        "072_Rescue_Aldi_Atherstone_Morrisons_Sittingbourne_PreOrders.sql",
        "073_Rejected_Order_DoNotLearn_Guard.sql",
        "074_Local_Tms_Users.sql",
        "075_Canonical_Identity_Uniqueness.sql"
    ];

    internal const string HistoryTableSql = """
        IF OBJECT_ID(N'dbo.SchemaMigration', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.SchemaMigration
            (
                Version int NOT NULL,
                Name nvarchar(260) NOT NULL,
                AppliedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_SchemaMigration_AppliedAtUtc DEFAULT (SYSUTCDATETIME()),
                Checksum nvarchar(64) NOT NULL,
                CONSTRAINT PK_SchemaMigration PRIMARY KEY (Version)
            );
        END;
        """;

    public static IReadOnlyList<SchemaMigrationDefinition> GetMigrations()
    {
        var assembly = typeof(SchemaMigrationRunner).Assembly;
        var embeddedFiles = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[ResourcePrefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var expectedFiles = OrderedMigrationFiles.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!embeddedFiles.SequenceEqual(expectedFiles, StringComparer.Ordinal))
        {
            var missing = OrderedMigrationFiles.Except(embeddedFiles, StringComparer.Ordinal).ToArray();
            var unexpected = embeddedFiles.Except(OrderedMigrationFiles, StringComparer.Ordinal).ToArray();
            throw new InvalidOperationException(
                $"Embedded schema migration catalogue mismatch. Missing: [{string.Join(", ", missing)}]. Unexpected/unversioned: [{string.Join(", ", unexpected)}]. " +
                "Every embedded Database/*.sql resource must have one immutable sequential entry in SchemaMigrationRunner.OrderedMigrationFiles.");
        }

        var migrations = new List<SchemaMigrationDefinition>(OrderedMigrationFiles.Length);
        for (var index = 0; index < OrderedMigrationFiles.Length; index++)
        {
            var fileName = OrderedMigrationFiles[index];
            var resourceName = ResourcePrefix + fileName;
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Schema migration resource {resourceName} was not found.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));
            var sql = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            migrations.Add(new SchemaMigrationDefinition(index + 1, fileName, resourceName, sql, checksum));
        }

        return migrations;
    }

    public static async Task ApplyAsync(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        IReadOnlyList<SchemaMigrationDefinition> migrations;
        try
        {
            migrations = GetMigrations();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Schema migration catalogue is invalid. Application startup will stop.");
            throw;
        }

        var openedHere = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(ct);

        try
        {
            var connection = db.Database.GetDbConnection();
            await AcquireMigrationLockAsync(connection, ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync(HistoryTableSql, ct);
                var applied = await ReadAppliedMigrationsAsync(connection, ct);
                ValidateAppliedHistory(migrations, applied);
                var appliedCount = applied.Count;

                foreach (var migration in migrations)
                {
                    if (applied.ContainsKey(migration.Version))
                    {
                        logger.LogDebug(
                            "Schema migration {Version} {MigrationName} already applied with checksum {Checksum}.",
                            migration.Version, migration.Name, migration.Checksum);
                        continue;
                    }

                    if (DeferredStartupMigrations.Contains(migration.Name))
                    {
                        logger.LogWarning(
                            "Deferring schema migration {Version} {MigrationName} during API startup. It is registered and checksum-protected, but must be applied by the maintenance runner without blocking availability.",
                            migration.Version, migration.Name);
                        continue;
                    }

                    logger.LogInformation(
                        "Applying required schema migration {Version} {MigrationName} ({Checksum}).",
                        migration.Version, migration.Name, migration.Checksum);

                    if (string.Equals(migration.Name, IntakeMappingGovernanceMigration, StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "Applying additive IntegrationMappings.NormalizedExternalValue compatibility preparation before migration {Version}.",
                            migration.Version);
                        await db.Database.ExecuteSqlRawAsync(IntakeMappingGovernancePreparationSql, ct);
                    }

                    if (string.Equals(migration.Name, DriverTachoIdentityMigration, StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "Applying additive Drivers Tacho identity compatibility preparation before migration {Version}.",
                            migration.Version);
                        await db.Database.ExecuteSqlRawAsync(DriverTachoIdentityPreparationSql, ct);
                    }

                    if (string.Equals(migration.Name, MarketContactsStableKeyMigration, StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "Applying additive MarketContacts.MarketKey compatibility preparation before migration {Version}.",
                            migration.Version);
                        await db.Database.ExecuteSqlRawAsync(MarketContactsStableKeyPreparationSql, ct);
                    }

                    await ApplySingleMigrationAsync(db, migration, logger, ct);
                    appliedCount++;
                }

                logger.LogInformation(
                    "Schema migration check complete. {AppliedMigrationCount} of {MigrationCount} registered migration(s) are applied; deferred maintenance migrations do not block API startup.",
                    appliedCount, migrations.Count);
            }
            finally
            {
                await ReleaseMigrationLockAsync(connection, logger, ct);
            }
        }
        catch (SchemaMigrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Schema migration initialisation failed. Application startup will stop.");
            throw;
        }
        finally
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task ApplySingleMigrationAsync(
        TmsDbContext db,
        SchemaMigrationDefinition migration,
        ILogger logger,
        CancellationToken ct)
    {
        var batches = SplitOnGo(migration.Sql);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();

            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var batch = batches[batchIndex];
                if (string.IsNullOrWhiteSpace(batch))
                    continue;

                logger.LogDebug(
                    "Executing batch {BatchNumber}/{BatchTotal} of migration {Version} {MigrationName}.",
                    batchIndex + 1, batches.Count, migration.Version, migration.Name);

                using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandTimeout = 300;
                await command.ExecuteNonQueryAsync(ct);
            }

            using var historyCommand = connection.CreateCommand();
            historyCommand.CommandText =
                "INSERT INTO dbo.SchemaMigration (Version, Name, AppliedAtUtc, Checksum) " +
                "VALUES (@Version, @Name, SYSUTCDATETIME(), @Checksum);";
            historyCommand.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var pVersion = historyCommand.CreateParameter();
            pVersion.ParameterName = "@Version";
            pVersion.Value = migration.Version;
            historyCommand.Parameters.Add(pVersion);
            var pName = historyCommand.CreateParameter();
            pName.ParameterName = "@Name";
            pName.Value = migration.Name;
            historyCommand.Parameters.Add(pName);
            var pChecksum = historyCommand.CreateParameter();
            pChecksum.ParameterName = "@Checksum";
            pChecksum.Value = migration.Checksum;
            historyCommand.Parameters.Add(pChecksum);
            await historyCommand.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Applied schema migration {Version} {MigrationName} successfully ({BatchCount} batch(es)).",
                migration.Version, migration.Name, batches.Count(b => !string.IsNullOrWhiteSpace(b)));
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync(ct);
            }
            catch (Exception rollbackException)
            {
                logger.LogError(
                    rollbackException,
                    "Rollback also failed for schema migration {Version} {MigrationName}.",
                    migration.Version, migration.Name);
            }

            logger.LogCritical(
                ex,
                "Required schema migration {Version} {MigrationName} failed. Checksum {Checksum}. Application startup will stop. Reason: {FailureReason}",
                migration.Version, migration.Name, migration.Checksum, ex.GetBaseException().Message);

            throw new SchemaMigrationException(
                migration.Version,
                migration.Name,
                $"Required schema migration {migration.Version} ({migration.Name}) failed; application startup cannot continue.",
                ex);
        }
    }

    /// <summary>
    /// Splits a SQL script into batches on standalone GO lines while ignoring GO inside
    /// string literals and comments.
    /// </summary>
    internal static IReadOnlyList<string> SplitOnGo(string sql)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sql, @"(?i)\bGO\b"))
            return [sql];

        var batches = new List<string>();
        var current = new StringBuilder();
        var inBlockComment = false;
        var inString = false;

        var lines = sql.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        foreach (var line in lines)
        {
            if (!inBlockComment && !inString &&
                line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                batches.Add(current.ToString());
                current.Clear();
                continue;
            }

            var i = 0;
            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i += 2;
                        continue;
                    }

                    i++;
                    continue;
                }

                if (inString)
                {
                    if (line[i] == '\'')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        inString = false;
                    }

                    i++;
                    continue;
                }

                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }

                if (i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-')
                    break;

                if (line[i] == '\'')
                {
                    inString = true;
                    i++;
                    continue;
                }

                i++;
            }

            if (inString)
                inString = false;

            current.AppendLine(line);
        }

        batches.Add(current.ToString());
        return batches;
    }

    private static async Task<Dictionary<int, AppliedSchemaMigration>> ReadAppliedMigrationsAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, Checksum FROM dbo.SchemaMigration ORDER BY Version;";
        var applied = new Dictionary<int, AppliedSchemaMigration>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var migration = new AppliedSchemaMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
            applied.Add(migration.Version, migration);
        }

        return applied;
    }

    internal static void ValidateAppliedHistory(
        IReadOnlyList<SchemaMigrationDefinition> migrations,
        IReadOnlyDictionary<int, AppliedSchemaMigration> applied)
    {
        if (applied.Count == 0) return;

        var migrationByVersion = migrations.ToDictionary(migration => migration.Version);
        var highestAppliedVersion = applied.Keys.Max();

        for (var version = 1; version <= highestAppliedVersion; version++)
        {
            if (!applied.ContainsKey(version))
            {
                if (migrationByVersion.TryGetValue(version, out var missingMigration) &&
                    DeferredStartupMigrations.Contains(missingMigration.Name))
                    continue;
                throw new InvalidOperationException(
                    $"SchemaMigration history has a gap at version {version}. Refusing to apply migrations out of order.");
            }
        }

        foreach (var pair in applied.OrderBy(pair => pair.Key))
        {
            if (!migrationByVersion.TryGetValue(pair.Key, out var expected))
                throw new InvalidOperationException(
                    $"SchemaMigration history contains unknown version {pair.Key} ({pair.Value.Name}).");

            var actual = pair.Value;
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"SchemaMigration version {pair.Key} name mismatch. Database has {actual.Name}; application expects {expected.Name}.");

            if (!string.Equals(actual.Checksum, expected.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SchemaMigration version {pair.Key} checksum mismatch for {expected.Name}. Database has {actual.Checksum}; application expects {expected.Checksum}.");
        }
    }

    private static async Task AcquireMigrationLockAsync(DbConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 30000;
            SELECT @result;
            """;
        var resourceParameter = command.CreateParameter();
        resourceParameter.ParameterName = "@resource";
        resourceParameter.Value = MigrationLockResource;
        command.Parameters.Add(resourceParameter);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (result < 0)
            throw new InvalidOperationException($"Unable to acquire schema migration lock {MigrationLockResource}; sp_getapplock returned {result}.");
    }

    private static async Task ReleaseMigrationLockAsync(DbConnection connection, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@resource";
            parameter.Value = MigrationLockResource;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release schema migration lock {MigrationLockResource}; SQL will release it when the connection closes.", MigrationLockResource);
        }
    }
}
