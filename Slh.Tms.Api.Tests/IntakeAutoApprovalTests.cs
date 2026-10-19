using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class IntakeAutoApprovalTests
{
    private static ParsedEmailOrder Order(string destination, int pallets, string collection = "Sefter North", params string[] warnings) =>
        new($"row-{destination}", "n", JsonSerializer.SerializeToElement(new
        {
            customerCode = "BARFOOTS", collectionDate = "2026-10-18", deliveryDate = "2026-10-18", pallets,
            sellerName = collection, stallNumber = destination, intakeProfile = "BARFOOTS_ALDI_CONFIRMED_WORKBOOK",
            intakeWarnings = warnings, plannerReady = true, intakeConfidence = "High"
        }), warnings);

    private static IntakeAutoApproval.FormatHistory History()
    {
        var history = new IntakeAutoApproval.FormatHistory { Orders = 12 };
        history.Destinations["ALDIBOLTON"] = [8, 10, 12, 9];
        history.Destinations["ALDISWINDON"] = [14, 16, 15];
        history.Collections.Add("SEFTERNORTH");
        history.Collections.Add("LEYTHORNE");
        return history;
    }

    [Fact]
    public void Usual_row_is_clean_and_eligible()
    {
        var annotated = IntakeAutoApproval.Annotate(Order("ALDI BOLTON", 11), "BARFOOTS_ALDI_CONFIRMED_WORKBOOK", History(), IntakeAutoApproval.ModeTrial);
        var decision = annotated.Payload.GetProperty("autoApproval");
        Assert.True(decision.GetProperty("eligible").GetBoolean());
        Assert.Equal("Trial", decision.GetProperty("mode").GetString());
        Assert.Empty(annotated.Payload.GetProperty("intakeWarnings").EnumerateArray());
    }

    [Fact]
    public void Unusual_count_new_depot_and_new_site_are_highlighted_and_block_auto_approval()
    {
        var high = IntakeAutoApproval.Annotate(Order("ALDI BOLTON", 21), "K", History(), IntakeAutoApproval.ModeOn);
        Assert.Contains(high.Payload.GetProperty("intakeWarnings").EnumerateArray(), w => w.GetString() == "Unusual: ALDI BOLTON 21 pallets, usually 8–12");
        Assert.False(high.Payload.GetProperty("autoApproval").GetProperty("eligible").GetBoolean());

        var depot = IntakeAutoApproval.Annotate(Order("ALDI NESTON", 2), "K", History(), IntakeAutoApproval.ModeOn);
        Assert.Contains(depot.Payload.GetProperty("intakeWarnings").EnumerateArray(), w => w.GetString()!.Contains("new delivery point ALDI NESTON"));

        var site = IntakeAutoApproval.Annotate(Order("ALDI SWINDON", 15, "Runcton"), "K", History(), IntakeAutoApproval.ModeOn);
        Assert.Contains(site.Payload.GetProperty("intakeWarnings").EnumerateArray(), w => w.GetString()!.Contains("new collection site Runcton"));
    }

    [Fact]
    public void Existing_warnings_or_thin_history_stop_auto_approval()
    {
        var warned = IntakeAutoApproval.Annotate(Order("ALDI BOLTON", 10, "Sefter North", "Temperature not stated"), "K", History(), IntakeAutoApproval.ModeOn);
        Assert.False(warned.Payload.GetProperty("autoApproval").GetProperty("eligible").GetBoolean());

        var thin = IntakeAutoApproval.Annotate(Order("ALDI BOLTON", 10), "K", new IntakeAutoApproval.FormatHistory { Orders = 2 }, IntakeAutoApproval.ModeOn);
        Assert.False(thin.Payload.GetProperty("autoApproval").GetProperty("eligible").GetBoolean());
        Assert.Empty(thin.Payload.GetProperty("intakeWarnings").EnumerateArray());
    }

    [Fact]
    public void Generic_body_guess_is_never_a_fixed_format()
    {
        var payload = JsonSerializer.SerializeToElement(new { customerCode = "EMAIL", sourceSender = "someone@example.com" });
        Assert.Null(IntakeAutoApproval.FormatKey(payload, "body-1"));
        Assert.Equal("fowlerwelch.co.uk|WAITROSE", IntakeAutoApproval.FormatKey(JsonSerializer.SerializeToElement(new { customerCode = "WAITROSE", sourceSender = "Tom.Whiting@fowlerwelch.co.uk" }), "waitrose-aylesford-1"));
    }

    [Fact]
    public async Task Format_switched_on_approves_clean_orders_into_planning()
    {
        IntakeAutoApproval.ClearCache();
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        for (var i = 0; i < 6; i++)
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "order", IdempotencyKey = $"hist-{i}", Status = StagingStatus.Promoted,
                PayloadJson = Order("ALDI BOLTON", 9 + i % 3).Payload.GetRawText()
            });
        await db.SaveChangesAsync();
        await IntakeAutoApproval.SaveModeAsync(db, "BARFOOTS_ALDI_CONFIRMED_WORKBOOK", "On", "planner@lyonshaulage.com", CancellationToken.None);

        var parsed = await IntakeAutoApproval.AnnotateAsync(db, new EmailIntakeParseResult([Order("ALDI BOLTON", 10), Order("ALDI BOLTON", 30)], [], null), CancellationToken.None);
        Assert.True(parsed.Orders[0].Payload.GetProperty("autoApproval").GetProperty("eligible").GetBoolean());
        Assert.False(parsed.Orders[1].Payload.GetProperty("autoApproval").GetProperty("eligible").GetBoolean());
        Assert.Equal("On", parsed.Orders[0].Payload.GetProperty("autoApproval").GetProperty("mode").GetString());
    }
}
