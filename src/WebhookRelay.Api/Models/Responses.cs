namespace WebhookRelay.Api.Models;

// What the API sends back. These are separate from the database classes so a
// change to a table doesn't silently change the API.

public record EventSummary(
    int Id,
    string Source,
    string DeliveryId,
    EventStatus Status,
    int AttemptCount,
    DateTime ReceivedAt,
    DateTime NextAttemptAt,
    DateTime? DeliveredAt,
    string? LastError);

public record EventDetail(
    int Id,
    string Source,
    string DeliveryId,
    EventStatus Status,
    int AttemptCount,
    DateTime ReceivedAt,
    DateTime NextAttemptAt,
    DateTime? DeliveredAt,
    string? LastError,
    string Payload,
    List<AttemptSummary> Attempts);

public record AttemptSummary(
    DateTime AttemptedAt,
    int? StatusCode,
    bool Succeeded,
    string? Error);

public record EventPage(
    int Total,
    int Page,
    int PageSize,
    List<EventSummary> Items);

public record StatsResponse(
    int Pending,
    int Delivered,
    int Failed,
    int CallsUsedToday,
    int DailyCallBudget);

public record BackfillRequest(
    DateTime From,
    DateTime To,
    string? Source);

public record BackfillResponse(int Queued);
