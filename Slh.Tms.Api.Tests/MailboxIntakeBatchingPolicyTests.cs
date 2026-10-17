using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MailboxIntakeBatchingPolicyTests
{
    [Fact]
    public void Intake_batches_idempotency_lookup_supersession_and_save()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "Controllers", "OrderIntakeController.cs"));
        var intakeStart = source.IndexOf("public async Task<IActionResult> Intake", StringComparison.Ordinal);
        var sourceEmailStart = source.IndexOf("[HttpGet(\"source-email/{stagingId:guid}\")]", StringComparison.Ordinal);
        Assert.True(intakeStart >= 0 && sourceEmailStart > intakeStart);

        var intake = source[intakeStart..sourceEmailStart];
        Assert.Contains("idempotencyKeys.Contains(item.IdempotencyKey)", intake, StringComparison.Ordinal);
        Assert.Contains("SupersedeOlderPendingBatch", intake, StringComparison.Ordinal);
        Assert.Contains("createdByKey", intake, StringComparison.Ordinal);
        Assert.DoesNotContain("SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey", intake, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(intake, "SaveChangesAsync(ct)"));
    }

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }
        return count;
    }
}
