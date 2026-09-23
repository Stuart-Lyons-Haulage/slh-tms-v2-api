using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class StagedImportsTriggerSafetyTests
{
    [Fact]
    public void DbContext_disables_sql_server_output_for_triggered_staged_imports_table()
    {
        var source = ReadRepositoryFile("Data", "TmsDbContext.cs");

        Assert.Contains("ToTable(\"StagedImports\", table => table.UseSqlOutputClause(false))", source);
        Assert.Contains("IX_StagedImports_Entity_Status_ReceivedAtUtc", source);
    }

    [Fact]
    public void Migration_adds_operational_staging_queue_index_without_destructive_table_changes()
    {
        var migration = ReadRepositoryFile("Migrations", "20260916111500_StagedImportsTriggerSafeAndOperationalIndex.cs");

        Assert.Contains("CREATE INDEX [IX_StagedImports_Entity_Status_ReceivedAtUtc]", migration);
        Assert.Contains("IF NOT EXISTS", migration);
        Assert.DoesNotContain("DROP TABLE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM [dbo].[StagedImports]", migration, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(path).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(path)} from {AppContext.BaseDirectory}.");
    }
}
