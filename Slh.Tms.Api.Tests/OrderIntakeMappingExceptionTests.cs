using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeMappingExceptionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public OrderIntakeMappingExceptionTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Incomplete_body_only_market_order_is_retained_as_evidence_only()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var payload = JsonSerializer.Serialize(new
        {
            messageId = $"market-mapping-{Guid.NewGuid():N}",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "planner@pmtransport.co.uk",
            subject = "Tangmere markets 06/09",
            receivedAtUtc = "2026-09-06T09:47:33Z",
            bodyText = "Please collecty 48pt from tangmere today\n15pt Fresh import spit"
        });
        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ignored\":true", await response.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, item => item.EntityType == "order" && item.PayloadJson.Contains("market-mapping-"));
        Assert.Contains(db.StagedImports, item => item.EntityType == "email-evidence" && item.PayloadJson.Contains("market-mapping-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Successful_reparse_archives_same_message_mapping_placeholder_and_stages_parsed_order()
    {
        var messageId = $"sainsbury-resolve-{Guid.NewGuid():N}";
        var compactMessageId = new string(messageId.Where(char.IsLetterOrDigit).ToArray());
        var placeholder = new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = $"email:{compactMessageId}:mapping-exception",
            PayloadJson = JsonSerializer.Serialize(new { customerCode = "SAINSBURY", intakeStatus = "MappingException", sourceMessageId = messageId }),
            Status = StagingStatus.PendingReview,
            Source = "Info mailbox intake",
            ReceivedAtUtc = DateTimeOffset.Parse("2026-10-11T14:09:00Z")
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.StagedImports.Add(placeholder);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var request = JsonSerializer.Serialize(new
        {
            messageId,
            mailbox = "info@lyonshaulage.com",
            senderAddress = "Backhaul.Requests@sainsburys.co.uk",
            senderName = "Backhaul.Requests",
            subject = "Transport plan for STUART LYONS for 12/10/2026 - Version 1",
            receivedAtUtc = "2026-10-11T14:09:00Z",
            bodyText = "Please find attached the transport plan.",
            attachments = new[]
            {
                new
                {
                    name = "Haulier Plan.xlsx",
                    contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    contentBase64 = Convert.ToBase64String(SainsburyHaulierPlanParserTests.BuildScreenshotWorkbook()),
                    isInline = false
                }
            }
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(request, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var resultScope = factory.Services.CreateScope();
        var resultDb = resultScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Equal(StagingStatus.Archived, (await resultDb.StagedImports.FindAsync(placeholder.Id))!.Status);
        var active = resultDb.StagedImports.Where(item => item.EntityType == "order" &&
            item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(messageId)).ToList();
        var parsed = Assert.Single(active);
        Assert.DoesNotContain("MappingException", parsed.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nwf_pallet_order_sender_without_readable_attachment_is_retained_as_evidence_only()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"nwf-mapping-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = "<nwf-mapping@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "D_StuartLyonsPalletOrdering@nwfltd.co.uk",
            senderName = "D_Stuart Lyons Pallet Ordering",
            subject = "NWAY Pallet Order 25/08/2026",
            receivedAtUtc = "2026-08-24T08:15:00Z",
            bodyText = "NWAY pallet order attached.",
            webLink = "https://outlook.office.com/mail/test",
            attachments = new[]
            {
                new
                {
                    name = "NWAY Pallet Order 25-08-2026.csv",
                    contentType = "text/csv",
                    isInline = false,
                    size = 4096
                }
            }
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ignored\":true", responseBody);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, item => item.EntityType == "order" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase));
        Assert.Single(db.StagedImports.Where(item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Internal_planner_load_plan_attachment_is_retained_as_evidence_only()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"internal-load-plan-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = "<internal-load-plan@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "michael@lyonshaulage.com",
            senderName = "Michael Lyons",
            subject = "Load plan",
            receivedAtUtc = "2026-08-25T16:29:41Z",
            bodyText = "Please find load plan attached for tonight",
            webLink = "https://outlook.office.com/mail/test",
            attachments = new[]
            {
                new
                {
                    name = "Load plan 25-08-2026.xlsx",
                    contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    isInline = false,
                    size = 4096
                }
            }
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ignored\":true", responseBody);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, item => item.EntityType == "order" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase));
        Assert.Single(db.StagedImports.Where(item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Aps_market_week_attachment_without_readable_content_is_retained_as_evidence_only()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"aps-market-week-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = "<aps-market-week@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "Marta.Rypien-Kabza@apsgroup.uk.com",
            senderName = "Marta Rypien-Kabza",
            subject = "Market Week 35.xls",
            receivedAtUtc = "2026-08-26T09:36:19Z",
            bodyText = "Please see attached.",
            webLink = "https://outlook.office.com/mail/test",
            attachments = new[]
            {
                new
                {
                    name = "Market Week 35.xls",
                    contentType = "application/vnd.ms-excel",
                    isInline = false,
                    size = 4096
                }
            }
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ignored\":true", responseBody);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, item => item.EntityType == "order" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase));
        Assert.Single(db.StagedImports.Where(item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Monarch_available_loads_mailshot_is_ignored_not_staged_as_mapping_exception()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var payload = JsonSerializer.Serialize(new
        {
            messageId = $"monarch-loads-{Guid.NewGuid():N}",
            internetMessageId = "<monarch-loads@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "mailshot@monarchtransport.co.uk",
            senderName = "Monarch Transport",
            subject = "URGENT - Monarch Transport Available Loads",
            receivedAtUtc = "2026-08-25T06:16:39Z",
            bodyText = "Monarch Available Loads. Can you cover the below loads? You are receiving this email because you opted in via our site.",
            webLink = "https://outlook.office.com/mail/test"
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ignored\":true", body);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, item => item.EntityType == "order" && item.PayloadJson.Contains("monarch-loads", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(db.StagedImports, item => item.EntityType == "email-evidence" && item.PayloadJson.Contains("monarch-loads", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Recognised_customer_name_without_quantity_or_attachment_is_ignored_not_zero_pallet_staged()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"waitrose-low-detail-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = "<waitrose-low-detail@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "loads@example.com",
            senderName = "Loads",
            subject = "Re: WAITROSE PALLET ESTIMATE for 26.08.2026",
            receivedAtUtc = "2026-08-25T10:47:00Z",
            bodyText = "Please check these for tomorrow. Waitrose Leythorne.",
            webLink = "https://outlook.office.com/mail/test"
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ignored\":true", body);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, item => item.EntityType == "order" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(db.StagedImports, item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Bartrums_available_loads_is_ignored_not_mapping_exception()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var messageId = $"bartrums-loads-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            messageId,
            internetMessageId = "<bartrums-loads@example.test>",
            mailbox = "info@lyonshaulage.com",
            senderAddress = "traffic@bartrums.com",
            senderName = "Bartrums Haulage & Storage",
            subject = "BARTRUMS AVAILABLE LOADS",
            receivedAtUtc = "2026-08-25T10:46:51Z",
            bodyText = "We currently have the following full load work available. If you are interested and able to assist, please contact us.",
            webLink = "https://outlook.office.com/mail/test"
        });

        var response = await client.PostAsync("/api/v1/order-intake/email", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ignored\":true", body);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.DoesNotContain(db.StagedImports, item => item.EntityType == "order" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(db.StagedImports, item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId, StringComparison.OrdinalIgnoreCase));
    }
}
