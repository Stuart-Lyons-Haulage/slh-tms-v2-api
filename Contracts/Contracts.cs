using System.Text.Json;
namespace Slh.Tms.Api.Contracts;
public sealed record StageImportRequest(string EntityType, string IdempotencyKey, JsonElement Payload, string? Source);
public sealed record StageImportResponse(Guid StagingId, string Status, DateTimeOffset ReceivedAtUtc, string ReviewUrl);
public sealed record ReviewRequest(string? Note);
public sealed record ErrorResponse(string Code, string Message, string CorrelationId);
