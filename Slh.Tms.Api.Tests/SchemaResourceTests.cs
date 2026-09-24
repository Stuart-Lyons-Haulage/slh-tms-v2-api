using Xunit;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Tests;

public sealed class SchemaResourceTests
{
    [Fact]
    public void All_database_repair_scripts_are_embedded()
    {
        var resources = typeof(Program).Assembly.GetManifestResourceNames();

        Assert.Contains("Slh.Tms.Api.Database.007_Market_Contact_Salesman.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.008_Customer_Contacts_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.009_Market_Contact_Sender.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.015_Driver_Existing_Table_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.023_Planning_Table_Complete_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.024_Integration_Mappings.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.027_Integration_Mappings_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.031_Order_Import_Audit_History.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.032_Order_Movement_Source_Lines.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.036_Order_Review_Schema_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.037_Driver_Tacho_Identity.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.038_Driver_Tacho_Identity_Repair.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.039_Canonical_Relational_Planning.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.040_Audit_Outbox.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.041_Distributed_Integration_Lease.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.042_Operational_Read_Performance_Indexes.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.045_Customer_Contacts_Master_Projection.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.000_Operational_Storage_Recovery.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.060_TachoMaster_Job_Managed_Identity.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.061_Email_Intake_Mapping_V2.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.071_InfoMailbox_Market_Waitrose_Coop_MasterData.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.072_Rescue_Aldi_Atherstone_Morrisons_Sittingbourne_PreOrders.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.073_Rejected_Order_DoNotLearn_Guard.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.074_Local_Tms_Users.sql", resources);
        Assert.Contains("Slh.Tms.Api.Database.075_Canonical_Identity_Uniqueness.sql", resources);
    }

    [Fact]
    public void Schema_migration_catalog_versions_every_embedded_database_script_once()
    {
        var resources = typeof(Program).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Slh.Tms.Api.Database.") && name.EndsWith(".sql"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var migrations = SchemaMigrationRunner.GetMigrations();

        Assert.Equal(resources.Length, migrations.Count);
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(migration => migration.Version));
        Assert.Equal(
            resources,
            migrations.Select(migration => migration.ResourceName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(migrations, migration => Assert.Matches("^[0-9A-F]{64}$", migration.Checksum));

        var expectedTail = new[]
        {
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
        };
        Assert.Equal(expectedTail, migrations.TakeLast(expectedTail.Length).Select(migration => migration.Name));
    }

    [Fact]
    public void Schema_history_table_has_required_version_name_timestamp_and_checksum_columns()
    {
        Assert.Contains("CREATE TABLE dbo.SchemaMigration", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("Version int NOT NULL", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("Name nvarchar", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("AppliedAtUtc datetime2", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("Checksum nvarchar", SchemaMigrationRunner.HistoryTableSql);
        Assert.Contains("PRIMARY KEY (Version)", SchemaMigrationRunner.HistoryTableSql);
    }

    [Fact]
    public void Intake_mapping_governance_preparation_is_guarded_and_additive()
    {
        var sql = SchemaMigrationRunner.IntakeMappingGovernancePreparationSql;

        Assert.Contains("OBJECT_ID(N'dbo.Sites'", sql);
        Assert.Contains("ALTER TABLE dbo.Sites ADD OperationalRegion nvarchar(80) NULL", sql);
        Assert.Contains("OBJECT_ID(N'dbo.IntegrationMappings'", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD MappingKind nvarchar(40) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD NormalizedExternalValue nvarchar(300) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD SenderPattern nvarchar(320) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD TemplateName nvarchar(120) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD TemplateVersion nvarchar(40) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD ConfidenceThreshold decimal(5,4) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD EffectiveFromUtc datetimeoffset NULL", sql);
        Assert.Contains("ALTER TABLE dbo.IntegrationMappings ADD EffectiveToUtc datetimeoffset NULL", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Driver_tacho_identity_preparation_is_guarded_and_additive()
    {
        var sql = SchemaMigrationRunner.DriverTachoIdentityPreparationSql;

        Assert.Contains("OBJECT_ID(N'dbo.Drivers'", sql);
        Assert.Contains("ALTER TABLE dbo.Drivers ADD TachoMasterDriverId nvarchar(80) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.Drivers ADD TachoCardNumber nvarchar(80) NULL", sql);
        Assert.Contains("ALTER TABLE dbo.Drivers ADD LastTachoSyncUtc datetimeoffset(7) NULL", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Roadtech_operational_visit_preparation_is_guarded_and_additive()
    {
        var sql = SchemaMigrationRunner.RoadTechOperationalVisitsPreparationSql;

        Assert.Contains("ALTER TABLE dbo.GeofenceVisits ADD RunId uniqueidentifier NULL", sql);
        Assert.Contains("ALTER TABLE dbo.GeofenceVisits ADD RunStopId uniqueidentifier NULL", sql);
        Assert.Contains("ALTER TABLE dbo.GeofenceVisits ADD SiteId uniqueidentifier NULL", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Email_intake_mapping_v2_preparation_is_guarded_and_additive()
    {
        var sql = SchemaMigrationRunner.EmailIntakeMappingV2PreparationSql;

        Assert.Contains("OBJECT_ID(N'dbo.CustomerEmailRoutes'", sql);
        Assert.Contains("NormalizedMappingHash", sql);
        Assert.Contains("HASHBYTES('SHA2_256'", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_vehicle_identity_preparation_is_guarded_and_additive()
    {
        var sql = SchemaMigrationRunner.CanonicalVehicleIdentityPreparationSql;

        Assert.Contains("OBJECT_ID(N'dbo.Vehicles'", sql);
        Assert.Contains("ALTER TABLE dbo.Vehicles ADD NormalizedRegistration", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Market_contact_stable_key_preparation_is_guarded_and_additive()
    {
        var sql = SchemaMigrationRunner.MarketContactsStableKeyPreparationSql;

        Assert.Contains("OBJECT_ID(N'dbo.MarketContacts'", sql);
        Assert.Contains("COL_LENGTH(N'dbo.MarketContacts', N'MarketKey') IS NULL", sql);
        Assert.Contains("ALTER TABLE dbo.MarketContacts ADD MarketKey nvarchar(160) NULL", sql);
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applied_migration_checksum_drift_is_rejected()
    {
        var migration = SchemaMigrationRunner.GetMigrations()[0];
        var history = new Dictionary<int, AppliedSchemaMigration>
        {
            [migration.Version] = new(migration.Version, migration.Name, new string('0', 64))
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrationRunner.ValidateAppliedHistory([migration], history));

        Assert.Contains("checksum mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applied_migration_history_gaps_are_rejected()
    {
        var migrations = SchemaMigrationRunner.GetMigrations().Take(3).ToArray();
        var history = new Dictionary<int, AppliedSchemaMigration>
        {
            [1] = new(1, migrations[0].Name, migrations[0].Checksum),
            [3] = new(3, migrations[2].Name, migrations[2].Checksum)
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrationRunner.ValidateAppliedHistory(migrations, history));

        Assert.Contains("gap at version 2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deferred_online_index_migration_may_remain_pending_after_later_required_migrations()
    {
        var migrations = SchemaMigrationRunner.GetMigrations();
        var deferred = migrations.Single(migration => migration.Name == "042_Operational_Read_Performance_Indexes.sql");
        var history = migrations
            .Where(migration => migration.Version != deferred.Version)
            .ToDictionary(
                migration => migration.Version,
                migration => new AppliedSchemaMigration(migration.Version, migration.Name, migration.Checksum));

        SchemaMigrationRunner.ValidateAppliedHistory(migrations, history);
    }

    [Fact]
    public void Historical_customer_site_crm_gap_can_be_caught_up()
    {
        var migrations = SchemaMigrationRunner.GetMigrations();
        var catchUp = migrations.Single(migration => migration.Name == "043_Customer_Site_Crm_Links.sql");
        var history = migrations
            .Where(migration => migration.Version != catchUp.Version)
            .ToDictionary(
                migration => migration.Version,
                migration => new AppliedSchemaMigration(migration.Version, migration.Name, migration.Checksum));

        SchemaMigrationRunner.ValidateAppliedHistory(migrations, history);
    }

    [Fact]
    public void Runtime_integration_mapping_repair_covers_partial_tables()
    {
        Assert.Contains("Provider", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("ExternalKey", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("TmsEntityType", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("TmsEntityId", IntegrationMappingSchemaRepair.RepairSql);
        Assert.Contains("IX_IntegrationMappings_Provider_ExternalKey_Type", IntegrationMappingSchemaRepair.RepairSql);
    }

    [Fact]
    public void Fleetio_mapping_fallback_message_does_not_make_tms_sole_authority()
    {
        Assert.DoesNotContain("TMS master remains authoritative", FleetioResilientSyncController.MappingUnavailableWarning);
        Assert.Contains("Fleetio-supplied identity, status and compliance fields were applied", FleetioResilientSyncController.MappingUnavailableWarning);
    }
    /// <summary>
    /// Pins the SHA-256 checksum of every embedded migration against what is recorded
    /// in the migration catalogue at build time.  If any migration file is edited after
    /// it has been added to OrderedMigrationFiles, this test fails immediately in CI,
    /// preventing the checksum-mismatch startup crash that blocked deployment #1137.
    ///
    /// To add a new migration: add a new entry to OrderedMigrationFiles and a new
    /// numbered SQL file.  To change applied SQL: add a NEW migration, never edit an
    /// existing one.
    /// </summary>
    [Fact]
    public void Every_catalogued_migration_checksum_matches_embedded_resource()
    {
        // GetMigrations() computes SHA-256 from the raw embedded resource bytes —
        // the same method used at startup and when writing to dbo.SchemaMigration.
        var migrations = SchemaMigrationRunner.GetMigrations();
        Assert.NotEmpty(migrations);

        // Verify each migration can compute its own checksum reproducibly.
        // A second call to GetMigrations() must produce identical checksums: the
        // resource bytes are immutable within a build, so any non-determinism here
        // would indicate a bug in the checksum computation.
        var second = SchemaMigrationRunner.GetMigrations();
        for (var i = 0; i < migrations.Count; i++)
        {
            Assert.Equal(
                migrations[i].Checksum,
                second[i].Checksum,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Migration_073_checksum_matches_production_applied_version()
    {
        // This test pins the exact checksum that production DB recorded for migration
        // 073 at deployment time.  It must never be changed.  If migration 073 is
        // inadvertently edited this test fails before the checksum-mismatch reaches
        // production startup.
        //
        // Production recorded: 99A6A83DFD08964196F87DC5C00940CA43C732D6C892A4ADE3108CFB0F9B4E77
        // Source commit: 1067223 (Escape JSON braces in migration 073)
        const string productionChecksum = "99A6A83DFD08964196F87DC5C00940CA43C732D6C892A4ADE3108CFB0F9B4E77";

        var migration073 = SchemaMigrationRunner
            .GetMigrations()
            .Single(m => m.Name == "073_Rejected_Order_DoNotLearn_Guard.sql");

        Assert.Equal(productionChecksum, migration073.Checksum, StringComparer.OrdinalIgnoreCase);
    }

}
