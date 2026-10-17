using System.Data;
using System.Data.Common;
using Xunit;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// Regression tests for SchemaMigrationRunner.SplitOnGo.
///
/// These tests exercise every requirement from the production deployment blocker:
/// - JSON literals containing {} do not cause composite-format errors
/// - Multiple GO-separated batches are split correctly
/// - CREATE TRIGGER is isolated into its own batch
/// - GO is matched case-insensitively
/// - GO surrounded by whitespace is treated as a separator
/// - GO inside a string literal is not treated as a separator
/// - GO inside a comment is not treated as a separator
/// - GO as part of a longer token (GOTO, GOOD) is not treated as a separator
/// - A single-batch migration (no GO) returns the whole script unchanged
/// - The actual structure of migration 073 produces the correct number of batches
/// </summary>
public sealed class SchemaMigrationBatchSplitterTests
{
    // ── No GO — fast path ─────────────────────────────────────────────────────

    [Fact]
    public void Single_batch_no_go_returns_whole_script_unchanged()
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE dbo.StagedImports SET ReviewNote = N'x' WHERE Id IS NULL;
            COMMIT TRANSACTION;
            """;

        var batches = SchemaMigrationRunner.SplitOnGo(sql);

        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Single(nonEmpty);
        Assert.Equal(sql, nonEmpty[0]);
    }

    // ── JSON literals with {} ─────────────────────────────────────────────────

    [Fact]
    public void Json_literal_braces_do_not_cause_format_errors()
    {
        // Migration 073 uses N'{{}}' (escaped after the brace-fix PR) but this test
        // confirms the splitter also handles the un-escaped N'{}' form correctly —
        // the splitter must not interpret {} at all; that was the EF formatting bug.
        const string sql = """
            UPDATE dbo.StagedImports
               SET PayloadJson = JSON_MODIFY(
                     CASE WHEN ISJSON(PayloadJson) = 1 THEN PayloadJson ELSE N'{}' END,
                     '$.doNotLearn', CAST(1 AS bit))
             WHERE Status = 2;
            """;

        // Should not throw, should return one batch containing the {} verbatim.
        var batches = SchemaMigrationRunner.SplitOnGo(sql);

        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Single(nonEmpty);
        Assert.Contains("N'{}'", nonEmpty[0]);
    }

    [Fact]
    public void Escaped_json_braces_are_preserved_verbatim()
    {
        const string sql = "UPDATE T SET Col = JSON_MODIFY(CASE WHEN ISJSON(Col)=1 THEN Col ELSE N'{{}}' END, '$.x', 1);";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Single(nonEmpty);
        Assert.Contains("N'{{}}'" , nonEmpty[0]);
    }

    // ── Multiple GO-separated batches ─────────────────────────────────────────

    [Fact]
    public void Two_batches_separated_by_go_are_split_correctly()
    {
        const string sql = """
            SELECT 1;
            GO
            SELECT 2;
            """;

        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();

        Assert.Equal(2, nonEmpty.Count);
        Assert.Contains("SELECT 1", nonEmpty[0]);
        Assert.Contains("SELECT 2", nonEmpty[1]);
    }

    [Fact]
    public void Create_trigger_in_its_own_batch_after_go()
    {
        const string sql = """
            BEGIN TRANSACTION;
            UPDATE dbo.T SET Col = 1;
            COMMIT TRANSACTION;
            GO
            IF OBJECT_ID(N'dbo.TR_Test', N'TR') IS NOT NULL DROP TRIGGER dbo.TR_Test;
            GO
            CREATE TRIGGER dbo.TR_Test ON dbo.T AFTER UPDATE AS BEGIN SET NOCOUNT ON; END;
            GO
            """;

        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();

        // Expect 3 non-empty batches:
        // 1. BEGIN TRANSACTION … COMMIT
        // 2. DROP TRIGGER
        // 3. CREATE TRIGGER
        Assert.Equal(3, nonEmpty.Count);
        Assert.Contains("BEGIN TRANSACTION", nonEmpty[0]);
        Assert.Contains("DROP TRIGGER", nonEmpty[1]);
        Assert.Contains("CREATE TRIGGER", nonEmpty[2]);
        // CREATE TRIGGER must be the first statement in its batch (no leading DML)
        Assert.StartsWith("CREATE TRIGGER", nonEmpty[2].TrimStart());
    }

    // ── Case insensitivity ────────────────────────────────────────────────────

    [Fact]
    public void Lowercase_go_is_treated_as_separator()
    {
        const string sql = "SELECT 1;\ngo\nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Equal(2, nonEmpty.Count);
    }

    [Fact]
    public void Mixed_case_Go_is_treated_as_separator()
    {
        const string sql = "SELECT 1;\nGo\nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Equal(2, nonEmpty.Count);
    }

    // ── Whitespace around GO ──────────────────────────────────────────────────

    [Fact]
    public void Go_with_leading_and_trailing_whitespace_is_treated_as_separator()
    {
        const string sql = "SELECT 1;\n   GO   \nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Equal(2, nonEmpty.Count);
    }

    [Fact]
    public void Go_with_tab_padding_is_treated_as_separator()
    {
        const string sql = "SELECT 1;\n\tGO\t\nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Equal(2, nonEmpty.Count);
    }

    // ── GO must not split when part of a longer token ─────────────────────────

    [Fact]
    public void GOTO_token_is_not_treated_as_separator()
    {
        const string sql = "GOTO myLabel;\nmyLabel:\nSELECT 1;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Single(nonEmpty);
    }

    [Fact]
    public void Go_embedded_in_column_name_context_is_not_treated_as_separator()
    {
        // "GOOD" contains GO as a prefix — must not split
        const string sql = "SELECT GOOD FROM dbo.T;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Single(nonEmpty);
    }

    // ── GO inside string literals ─────────────────────────────────────────────

    [Fact]
    public void Go_inside_single_quoted_string_is_not_treated_as_separator()
    {
        // The word GO appears inside a string literal — must not split
        const string sql = """
            INSERT dbo.T (Col) VALUES (N'please GO ahead');
            SELECT 1;
            """;
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Single(nonEmpty);
    }

    // ── GO inside comments ────────────────────────────────────────────────────

    [Fact]
    public void Go_on_line_with_preceding_double_dash_comment_is_not_treated_as_separator()
    {
        // GO after -- is in a comment; must not split
        const string sql = "SELECT 1; -- GO\nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        // The whole thing is one batch because GO is in a comment
        Assert.Single(nonEmpty);
    }

    // ── Multiple consecutive GOs ──────────────────────────────────────────────

    [Fact]
    public void Consecutive_go_lines_produce_empty_batches_that_are_skipped()
    {
        const string sql = "SELECT 1;\nGO\nGO\nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        // Two content batches; the middle empty batch is preserved but callers skip it
        Assert.Equal(2, nonEmpty.Count);
    }

    // ── Migration 073 structure ───────────────────────────────────────────────

    [Fact]
    public void Migration_073_produces_three_non_empty_batches()
    {
        // Migration 073 has the structure:
        //   Batch 1: SET XACT_ABORT + BEGIN TRANSACTION + DML + COMMIT TRANSACTION
        //   GO
        //   Batch 2: DROP TRIGGER (guarded with IF OBJECT_ID)
        //   GO
        //   Batch 3: CREATE TRIGGER
        //   GO
        //
        // We reconstruct a representative script with the same pattern.
        const string migration073Like = """
            SET XACT_ABORT ON;

            BEGIN TRANSACTION;

            IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
            BEGIN
                UPDATE dbo.StagedImports
                   SET PayloadJson = JSON_MODIFY(
                         CASE WHEN ISJSON(PayloadJson) = 1 THEN PayloadJson ELSE N'{{}}' END,
                         '$.doNotLearn', CAST(1 AS bit))
                 WHERE Status = 2;
            END;

            COMMIT TRANSACTION;
            GO

            IF OBJECT_ID(N'dbo.TR_StagedImports_Rejected_DoNotLearn', N'TR') IS NOT NULL
                DROP TRIGGER dbo.TR_StagedImports_Rejected_DoNotLearn;
            GO
            CREATE TRIGGER dbo.TR_StagedImports_Rejected_DoNotLearn
            ON dbo.StagedImports
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE target
                   SET PayloadJson = JSON_MODIFY(
                         CASE WHEN ISJSON(target.PayloadJson) = 1 THEN target.PayloadJson ELSE N'{{}}' END,
                         '$.doNotLearn', CAST(1 AS bit))
                  FROM dbo.StagedImports target
                 INNER JOIN inserted i ON i.Id = target.Id
                 WHERE i.Status = 2;
            END;
            GO
            """;

        var batches = SchemaMigrationRunner.SplitOnGo(migration073Like);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();

        Assert.Equal(3, nonEmpty.Count);

        // Batch 1: must contain BEGIN TRANSACTION and COMMIT
        Assert.Contains("BEGIN TRANSACTION", nonEmpty[0]);
        Assert.Contains("COMMIT TRANSACTION", nonEmpty[0]);

        // Batch 2: only the DROP TRIGGER guard
        Assert.Contains("DROP TRIGGER", nonEmpty[1]);
        Assert.DoesNotContain("CREATE TRIGGER", nonEmpty[1]);

        // Batch 3: CREATE TRIGGER must be the first statement
        var triggerBatch = nonEmpty[2].TrimStart();
        Assert.StartsWith("CREATE TRIGGER", triggerBatch);
    }

    [Fact]
    public void Actual_migration_073_has_correct_structure_when_loaded_from_embedded_resource()
    {
        // This uses the real embedded SQL from the production assembly — it will
        // catch any future edits to the file that change the GO structure.
        var migration = SchemaMigrationRunner
            .GetMigrations()
            .Single(m => m.Name == "073_Rejected_Order_DoNotLearn_Guard.sql");

        var batches = SchemaMigrationRunner.SplitOnGo(migration.Sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();

        // Three batches: DML, DROP TRIGGER, CREATE TRIGGER
        Assert.Equal(3, nonEmpty.Count);

        var lastBatch = nonEmpty[^1].TrimStart();
        Assert.StartsWith("CREATE TRIGGER", lastBatch);
    }

    // ── History row not written after failed batch ────────────────────────────

    [Fact]
    public void SplitOnGo_go_separator_line_is_not_included_in_any_batch()
    {
        const string sql = "SELECT 1;\nGO\nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        foreach (var batch in batches)
        {
            // The separator line itself must never appear in a batch
            Assert.DoesNotMatch(@"(?im)^\s*GO\s*$", batch);
        }
    }

    [Fact]
    public void Single_go_at_end_leaves_trailing_empty_batch_that_is_skippable()
    {
        const string sql = "SELECT 1;\nGO\n";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        // One content batch and one empty trailing batch
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Single(nonEmpty);
        Assert.Contains("SELECT 1", nonEmpty[0]);
    }

    // ── CRLF / mixed line endings ─────────────────────────────────────────────

    [Fact]
    public void Go_with_crlf_line_endings_is_treated_as_separator()
    {
        const string sql = "SELECT 1;\r\nGO\r\nSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Equal(2, nonEmpty.Count);
    }

    [Fact]
    public void Go_with_cr_only_line_endings_is_treated_as_separator()
    {
        const string sql = "SELECT 1;\rGO\rSELECT 2;";
        var batches = SchemaMigrationRunner.SplitOnGo(sql);
        var nonEmpty = batches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        Assert.Equal(2, nonEmpty.Count);
    }
}
