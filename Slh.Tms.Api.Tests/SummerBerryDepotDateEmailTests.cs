using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

/// <summary>
/// Regression tests built from real TSBC (The Summer Berry Company) emails to
/// info@lyonshaulage.com. Each email lists one block per Reynolds depot date:
/// "Monday 19th October depot date" / PO / Total Pallets / Collection time.
/// Reynolds is a same-day job: collected 08:00-09:00 and delivered on the depot date.
/// </summary>
public sealed class SummerBerryDepotDateEmailTests
{
    private readonly EmailOrderIntakeService service = new();

    // Body from Antoni Hristov, 17 Oct 2026 08:43 UTC, subject "TSBC - Reynolds".
    // The signature and footer name Groves Farm, Colworth and Leythorne Nurseries;
    // neither may be used as the collection site.
    private const string WeeklyReynoldsHtml = """
        <div class="WordSection1"><p class="MsoNormal">Good morning,</p><p class="MsoNormal"> </p>
        <p class="MsoNormal">Please see below pallet requirements for confirmed orders for next week:</p><p class="MsoNormal"> </p>
        <p class="MsoNormal"><b>TSBC Reynolds</b></p><p class="MsoNormal"><b> </b></p>
        <p class="MsoNormal"><b>Monday 19th October depot date</b><b></b></p>
        <p class="MsoNormal">PO 3175627<b></b></p>
        <p class="MsoNormal" style="margin-bottom:12.0pt"><b>Total Pallets: 2<br />Collection time: 08:00-09:00</b></p>
        <p class="MsoNormal" style="margin-bottom:12.0pt"><b> </b></p>
        <p class="MsoNormal"><b>Wednesday 21st October depot date</b></p>
        <p class="MsoNormal">PO 3175629</p>
        <p class="MsoNormal"><b>Total Pallets: 2<br />Collection time: 08:00-09:00</b></p>
        <p class="MsoNormal"><b> </b></p><p class="MsoNormal"><b> </b></p>
        <p class="MsoNormal"><b>Friday 23rd October depot date</b></p>
        <p class="MsoNormal">PO 3175630<b><br />Total Pallets: 2<br />Collection time: 08:00-09:00</b></p>
        <table><tbody><tr><td><p class="MsoNormal"><b><br />Kind Regards<br />Antoni Hristov</b></p></td></tr>
        <tr><td><p class="MsoNormal"><b>Commercial Operations Coordinator</b></p></td></tr>
        <tr><td><p class="MsoNormal">TSBC - The Summer Berry Company UK is a company registered in England and Wales.<br />Registered number: 02087609. Registered office: Groves Farm, Colworth, Chichester, PO20 2DX</p></td></tr></tbody></table>
        </div>The information contained within this email is confidential. Registered office: Leythorne Nurseries, Vinnetrow Road, Runcton, Chichester PO20 1QB.
        """;

    [Fact]
    public void WeeklyReynoldsEmail_CreatesOneSameDayOrderPerDepotDate()
    {
        var result = service.Parse(Request("TSBC - Reynolds", "2026-10-17T08:43:02Z", WeeklyReynoldsHtml));

        var orders = result.Orders.Where(order => order.SourceKey.StartsWith("summerberry-depot-", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, orders.Count);
        Assert.Equal(3, result.Orders.Count);

        var expected = new[] { ("3175627", "2026-10-19"), ("3175629", "2026-10-21"), ("3175630", "2026-10-23") };
        foreach (var (po, date) in expected)
        {
            var order = Assert.Single(orders, item => item.Payload.GetProperty("customerPo").GetString() == po);
            var payload = order.Payload;
            Assert.Equal("SUMMERBERRY", payload.GetProperty("customerCode").GetString());
            Assert.Equal("Reynolds", payload.GetProperty("stallNumber").GetString());
            Assert.Equal("Summer Berry", payload.GetProperty("sellerName").GetString());
            Assert.Equal(2, payload.GetProperty("pallets").GetInt32());
            Assert.Equal(date, payload.GetProperty("collectionDate").GetString());
            Assert.Equal(date, payload.GetProperty("deliveryDate").GetString());
            Assert.Equal("08:00", payload.GetProperty("requestedTime").GetString());
            Assert.Equal("08:00-09:00", payload.GetProperty("collectionWindow").GetString());
            Assert.Equal("SameDay", payload.GetProperty("routeTiming").GetString());
            Assert.Contains(order.Warnings, warning => warning.StartsWith("Summer Berry farm was not stated", StringComparison.Ordinal));
            Assert.DoesNotContain(order.Warnings, warning => warning.Contains("does not match", StringComparison.OrdinalIgnoreCase) || warning.Contains(" is a ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WeeklyReynoldsEmail_NeverReadsSignatureFarmsAsCollectionSite()
    {
        var result = service.Parse(Request("TSBC - Reynolds", "2026-10-17T08:43:02Z", WeeklyReynoldsHtml));

        Assert.NotEmpty(result.Orders);
        Assert.All(result.Orders, order =>
        {
            var collection = order.Payload.GetProperty("sellerName").GetString() ?? string.Empty;
            var destination = order.Payload.GetProperty("stallNumber").GetString() ?? string.Empty;
            Assert.DoesNotContain("Groves", collection, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Leythorne", collection, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Groves", destination, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Leythorne", destination, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void WeekdayThatDoesNotMatchTheDate_IsFlaggedAndSingularPalletIsRead()
    {
        // 29 Sep 2026 email: "Friday 1st October" - 1 October 2026 is a Thursday.
        // Summer Berry later confirmed Friday 02/10, so the order must go to review.
        const string html = """
            <p>Good afternoon,</p><p>Please see below order for Reynolds</p>
            <p><b>TSBC Reynolds</b></p>
            <p><b>Wednesday 30th September depot date</b></p>
            <p>PO 3171459<br /><b>Total Pallets: 4<br />Collection time: 08:00-09:00</b></p>
            <p><b>Friday 1st October depot date</b></p>
            <p>PO 3171460<b><br />Total Pallets: 3 pallet<br />Collection time: 08:00-09:00</b></p>
            <p><b>Kind Regards<br />Antoni Hristov</b></p>
            """;

        var result = service.Parse(Request("TSBC - Reynolds", "2026-09-29T14:12:31Z", html));

        Assert.Equal(2, result.Orders.Count);
        var wednesday = Assert.Single(result.Orders, order => order.Payload.GetProperty("customerPo").GetString() == "3171459");
        Assert.Equal(4, wednesday.Payload.GetProperty("pallets").GetInt32());
        Assert.DoesNotContain(wednesday.Warnings, warning => warning.StartsWith("Depot date line says", StringComparison.Ordinal));

        var friday = Assert.Single(result.Orders, order => order.Payload.GetProperty("customerPo").GetString() == "3171460");
        Assert.Equal(3, friday.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-10-01", friday.Payload.GetProperty("deliveryDate").GetString());
        Assert.Contains(friday.Warnings, warning => warning.StartsWith("Depot date line says 'Friday 1st October depot date'", StringComparison.Ordinal) && warning.Contains("Thursday", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplyWithQuotedHistory_DoesNotRecreateOrdersFromTheQuotedEmail()
    {
        // 30 Sep 2026 reply: the new text is a date change; the original order
        // blocks only appear in the quoted history below "From:".
        const string html = """
            <p>Hello all ,</p>
            <p>Just to give you an update the next Reynolds collection should be on Friday 02/10 not tomorrow .</p>
            <p>Regards, </p><p><b>Plamen Dimitrov</b></p>
            <div><p><b>From:</b> Antoni Hristov &lt;Antoni.Hristov@summerberry.co.uk&gt; <br /><b>Sent:</b> 29 September 2026 15:12<br /><b>Subject:</b> TSBC - Reynolds</p></div>
            <p><b>TSBC Reynolds</b></p>
            <p><b>Wednesday 30th September depot date</b></p>
            <p>PO 3171459<br /><b>Total Pallets: 4<br />Collection time: 08:00-09:00</b></p>
            """;

        var result = service.Parse(Request("RE: TSBC - Reynolds", "2026-09-30T16:16:29Z", html, "Plamen.Dimitrov@summerberry.co.uk"));

        Assert.DoesNotContain(result.Orders, order => order.SourceKey.StartsWith("summerberry-depot-", StringComparison.Ordinal));
    }

    [Fact]
    public void UnconfirmedTsbcDestination_IsFlaggedForSameDayOrOvernightCheck()
    {
        const string html = """
            <p><b>TSBC Example Foods</b></p>
            <p><b>Tuesday 20th October depot date</b></p>
            <p>PO 9000001<br /><b>Total Pallets: 5<br />Collection time: 14:00-15:00</b></p>
            <p><b>Kind Regards</b></p>
            """;

        var result = service.Parse(Request("TSBC - Example Foods", "2026-10-17T08:43:02Z", html));

        var order = Assert.Single(result.Orders);
        Assert.Equal("Example Foods", order.Payload.GetProperty("stallNumber").GetString());
        Assert.False(order.Payload.TryGetProperty("routeTiming", out _));
        Assert.Contains(order.Warnings, warning => warning.StartsWith("Collection assumed on the Example Foods depot date", StringComparison.Ordinal));
    }

    [Fact]
    public void SameLayoutFromAnotherSender_IsNotReadAsSummerBerry()
    {
        var result = service.Parse(Request("TSBC - Reynolds", "2026-10-17T08:43:02Z", WeeklyReynoldsHtml, "planner@example.co.uk"));

        Assert.DoesNotContain(result.Orders, order => order.SourceKey.StartsWith("summerberry-depot-", StringComparison.Ordinal));
    }

    [Fact]
    public void SpinneysCustomerDetailsEmail_CreatesOneOvernightOrder()
    {
        const string html = """
            <p>Good morning,</p><p>Please see the details below:</p><p> </p>
            <p>Customer : Spinneys/JHF order</p><p> </p>
            <p>Depot Date: 20.10.2026</p>
            <p>Collection : 19.10 - 17:00</p><p> </p>
            <p>Pallets : 7</p><p> </p>
            <p>Adress of delivery : K&amp;N facility</p>
            <p>Building 1235, Eastern Perimeter Road, London Heathrow</p>
            <p>Kind Regards</p><p>Antoni Hristov</p>
            """;

        var result = service.Parse(Request("20.10.2026 Spinneys/JHF delivery", "2026-10-19T07:44:22Z", html));

        var order = Assert.Single(result.Orders);
        Assert.Equal("SUMMERBERRY", order.Payload.GetProperty("customerCode").GetString());
        Assert.Equal(7, order.Payload.GetProperty("pallets").GetInt32());
        Assert.Equal("2026-10-19", order.Payload.GetProperty("collectionDate").GetString());
        Assert.Equal("2026-10-20", order.Payload.GetProperty("deliveryDate").GetString());
        Assert.Equal("17:00", order.Payload.GetProperty("requestedTime").GetString());
        Assert.StartsWith("K&N facility", order.Payload.GetProperty("stallNumber").GetString());
        Assert.Equal("Overnight", order.Payload.GetProperty("routeTiming").GetString());
    }

    private static MailboxEmailIntakeRequest Request(string subject, string receivedAtUtc, string html, string sender = "Antoni.Hristov@summerberry.co.uk") =>
        new(
            $"message-{subject}-{receivedAtUtc}",
            null,
            "info@lyonshaulage.com",
            sender,
            "Summer Berry",
            subject,
            DateTimeOffset.Parse(receivedAtUtc),
            null,
            html,
            null,
            null);
}
