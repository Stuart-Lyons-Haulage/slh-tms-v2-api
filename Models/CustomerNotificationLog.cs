namespace Slh.Tms.Api.Models;

public enum NotificationType { MorningBriefing, LiveUpdate }
public enum NotificationChannel { Email, Sms }

public sealed class CustomerNotificationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public string OrderReference { get; set; } = string.Empty;
    public Guid StopId { get; set; }
    public NotificationType Type { get; set; }
    public DateTimeOffset SentAtUtc { get; set; }
    public string EtaWindowCommunicated { get; set; } = string.Empty;
    public DateTimeOffset? ActualArrivalUtc { get; set; }
    public int? ActualVarianceMinutes { get; set; }
    public NotificationChannel Channel { get; set; }
    public string RecipientAddress { get; set; } = string.Empty;
    public bool DeliveryConfirmed { get; set; }
    public string? FailureReason { get; set; }
}
