namespace Slh.Tms.Api.Models;

public sealed record CustomerLoadPlanRow(
    Guid LoadId,
    string LoadReference,
    Guid OrderId,
    string OrderReference,
    string CustomerCode,
    string CollectionSite,
    string DeliverySite,
    int? Pallets,
    string? PlannedCollectTime,
    string? DeliveryDeadline,
    string? DriverName,
    string? VehicleRegistration,
    string? TrailerNumber,
    string Status,
    string? Notes);

public sealed record CustomerLoadPlanPreview(
    string CustomerCode,
    string CustomerName,
    DateOnly PlanningDate,
    string Subject,
    string Body,
    string AttachmentName,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<CustomerLoadPlanRow> Rows,
    int TotalPallets,
    string? LastSendStatus,
    DateTimeOffset? LastSentAtUtc);

public sealed record CustomerLoadPlanPreviewResponse(
    DateOnly PlanningDate,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<CustomerLoadPlanPreview> Plans,
    IReadOnlyList<string> Warnings);

public sealed record QueueCustomerLoadPlanRequest(
    DateOnly PlanningDate,
    string CustomerCode,
    IReadOnlyList<string>? To,
    IReadOnlyList<string>? Cc,
    string? Subject,
    string? Body);

public sealed record MarkCustomerLoadPlanSentRequest(string? ProviderMessageId);

public sealed record CustomerLoadPlanOutboxAttachment(string Name, string ContentType, string ContentBytes);

public sealed record CustomerLoadPlanOutboxItem(
    Guid Id,
    string Mailbox,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string Body,
    IReadOnlyList<CustomerLoadPlanOutboxAttachment> Attachments,
    DateOnly PlanningDate,
    string CustomerCode,
    string CustomerName);
