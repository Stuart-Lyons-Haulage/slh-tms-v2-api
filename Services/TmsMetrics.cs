using System.Diagnostics.Metrics;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Low-cardinality SLH TMS operational metrics. The shared process instance is registered in DI
/// and is also used by static resilience helpers that cannot receive scoped services.
/// </summary>
public sealed class TmsMetrics
{
    public const string MeterName = "Slh.Tms";
    public static TmsMetrics Shared { get; } = new();

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Histogram<double> _apiEndpointLatency;
    private readonly Histogram<double> _sqlQueryLatency;
    private readonly Histogram<double> _dependencyLatency;
    private readonly Counter<long> _emailOrderIntakeSuccess;
    private readonly Counter<long> _emailOrderIntakeFailure;
    private readonly Counter<long> _importsProcessed;
    private readonly Counter<long> _importDuplicates;
    private readonly Counter<long> _planningRecoveryUsed;
    private readonly Counter<long> _logicalDuplicateCollapse;

    private long _roadTechObservedTicks;
    private long _fleetioContactTicks;
    private long _tachoMasterContactTicks;
    private long _sageHrContactTicks;

    private TmsMetrics()
    {
        _apiEndpointLatency = _meter.CreateHistogram<double>("api_endpoint_latency_ms", "ms", "SLH TMS API endpoint latency.");
        _sqlQueryLatency = _meter.CreateHistogram<double>("sql_query_latency_ms", "ms", "EF Core SQL command latency.");
        _dependencyLatency = _meter.CreateHistogram<double>("dependency_request_latency_ms", "ms", "Outbound provider request latency.");
        _emailOrderIntakeSuccess = _meter.CreateCounter<long>("email_order_intake_success_total", "requests", "Successful mailbox order intake requests.");
        _emailOrderIntakeFailure = _meter.CreateCounter<long>("email_order_intake_failure_total", "requests", "Failed mailbox order intake requests.");
        _importsProcessed = _meter.CreateCounter<long>("import_processed_total", "records", "Validated import records evaluated for staging.");
        _importDuplicates = _meter.CreateCounter<long>("import_duplicate_total", "records", "Import records resolved as an existing idempotent record. Divide by import_processed_total for duplicate rate.");
        _planningRecoveryUsed = _meter.CreateCounter<long>("planning_recovery_used_total", "runs", "Runs supplied by a secondary PlanningResilience source.");
        _logicalDuplicateCollapse = _meter.CreateCounter<long>("logical_duplicate_collapse_total", "runs", "Duplicate persisted run copies collapsed into one logical run.");

        _meter.CreateObservableGauge<double>("roadtech_data_age_seconds", ObserveRoadTechAge, "s", "Age of the newest RoadTech vehicle event.");
        _meter.CreateObservableGauge<double>("fleetio_last_successful_sync_age_seconds", ObserveFleetioAge, "s", "Age of the latest durable Fleetio sync receipt.");
        _meter.CreateObservableGauge<double>("tachomaster_last_successful_sync_age_seconds", ObserveTachoMasterAge, "s", "Age of the latest durable TachoMaster sync receipt.");
        _meter.CreateObservableGauge<double>("sagehr_last_successful_sync_age_seconds", ObserveSageHrAge, "s", "Age of the latest durable Sage HR sync receipt.");
    }

    public void RecordApiEndpointLatency(double milliseconds, string method, string route, int statusCode) =>
        _apiEndpointLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("http.route", route),
            new KeyValuePair<string, object?>("http.response.status_code", statusCode));

    public void RecordSqlQueryLatency(double milliseconds, string operation) =>
        _sqlQueryLatency.Record(milliseconds, new KeyValuePair<string, object?>("db.operation.name", operation));

    public void RecordDependencyLatency(double milliseconds, string dependency, string method, int statusCode) =>
        _dependencyLatency.Record(milliseconds,
            new KeyValuePair<string, object?>("dependency.name", dependency),
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("http.response.status_code", statusCode));

    public void RecordEmailOrderIntake(bool success)
    {
        if (success) _emailOrderIntakeSuccess.Add(1);
        else _emailOrderIntakeFailure.Add(1);
    }

    public void RecordImportBatch(long processed, long duplicates, string source)
    {
        if (processed > 0)
            _importsProcessed.Add(processed, new KeyValuePair<string, object?>("import.source", source));
        if (duplicates > 0)
            _importDuplicates.Add(duplicates, new KeyValuePair<string, object?>("import.source", source));
    }

    public void RecordPlanningRecovery(long count, string source)
    {
        if (count > 0)
            _planningRecoveryUsed.Add(count, new KeyValuePair<string, object?>("planning.recovery.source", source));
    }

    public void RecordLogicalDuplicateCollapse(long count)
    {
        if (count > 0) _logicalDuplicateCollapse.Add(count);
    }

    public void UpdateFreshness(DateTimeOffset? roadTechObservedUtc, DateTimeOffset? fleetioContactUtc, DateTimeOffset? tachoMasterContactUtc, DateTimeOffset? sageHrContactUtc)
    {
        Update(ref _roadTechObservedTicks, roadTechObservedUtc);
        Update(ref _fleetioContactTicks, fleetioContactUtc);
        Update(ref _tachoMasterContactTicks, tachoMasterContactUtc);
        Update(ref _sageHrContactTicks, sageHrContactUtc);
    }

    private IEnumerable<Measurement<double>> ObserveRoadTechAge() => ObserveAge(Interlocked.Read(ref _roadTechObservedTicks));
    private IEnumerable<Measurement<double>> ObserveFleetioAge() => ObserveAge(Interlocked.Read(ref _fleetioContactTicks));
    private IEnumerable<Measurement<double>> ObserveTachoMasterAge() => ObserveAge(Interlocked.Read(ref _tachoMasterContactTicks));
    private IEnumerable<Measurement<double>> ObserveSageHrAge() => ObserveAge(Interlocked.Read(ref _sageHrContactTicks));

    private static IEnumerable<Measurement<double>> ObserveAge(long utcTicks)
    {
        if (utcTicks <= 0) return [];
        var timestamp = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        return [new Measurement<double>(Math.Max(0, (DateTimeOffset.UtcNow - timestamp).TotalSeconds))];
    }

    private static void Update(ref long target, DateTimeOffset? value)
    {
        if (value is not null)
            Interlocked.Exchange(ref target, value.Value.UtcDateTime.Ticks);
    }
}
