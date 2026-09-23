using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SainsburyHaulierPlanParserTests
{
    private readonly SainsburyHaulierPlanParser parser = new();

    [Theory]
    [InlineData("[CrosspointPCC] STUART LYONS - Crosspoint PCC Plan for delivery date 19/09/2026")]
    [InlineData("[DaventryBond] STUART LYONS - Daventry Bond Plan for delivery date 19/09/2026")]
    [InlineData("[HDKPCC] STUART LYONS - Haydock PCC Plan for delivery date 19/09/2026")]
    public void CurrentCentralTransportSubjects_AreRecognised(string subject)
    {
        var result = parser.TryParse(Request(subject, "Central.Transport@sainsburys.co.uk"));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
        Assert.Contains("workbook", result.IgnoredReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoricTransportPlanSubject_RemainsRecognised()
    {
        var result = parser.TryParse(Request("Transport plan for STUART LYONS - delivery 19/09/2026", "planner@example.com"));

        Assert.NotNull(result);
        Assert.Empty(result!.Orders);
    }

    [Fact]
    public void SimilarNonSainsburySubject_IsNotClaimed()
    {
        var result = parser.TryParse(Request(
            "STUART LYONS - Customer Plan for delivery date 19/09/2026",
            "planner@anothercustomer.example"));

        Assert.Null(result);
    }

    private static MailboxEmailIntakeRequest Request(string subject, string sender) => new(
        "sainsbury-subject-regression",
        null,
        "info@lyonshaulage.com",
        sender,
        "Central Transport",
        subject,
        DateTimeOffset.Parse("2026-09-17T10:16:09Z"),
        "Please find attached the details for the load(s).",
        null,
        null,
        []);
}
