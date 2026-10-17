using System.Data;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class IntegrationLeaseNames
{
    public const string TachoMaster = "integration:tachomaster";
    public const string Fleetio = "integration:fleetio";
    public const string SageHr = "integration:sagehr";
}

/// <summary>
/// SQL-backed lease used to serialise cross-replica integration writers. Expiry provides
/// crash recovery; release is owner-qualified so an expired/reacquired lease cannot be
/// accidentally removed by the previous owner finishing late.
/// </summary>
public sealed record DistributedLeaseStatus(string LeaseId, string OwnerInstanceId, string RunId, DateTimeOffset AcquiredAtUtc, DateTimeOffset HeartbeatUtc, DateTimeOffset ExpiresAtUtc, bool IsStale);

public sealed class DistributedLeaseManager(TmsDbContext db, ILogger<DistributedLeaseManager> logger)
{
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<DistributedLeaseHandle?> TryAcquireAsync(string leaseId, TimeSpan duration, CancellationToken ct)
    {
        Validate(leaseId, duration);
        var runId = Guid.NewGuid().ToString("N");
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;
DECLARE @now datetime2(7) = SYSUTCDATETIME();
DECLARE @expires datetime2(7) = DATEADD(SECOND, @leaseSeconds, @now);
DECLARE @acquired bit = 0;

UPDATE dbo.DistributedLease WITH (UPDLOCK, HOLDLOCK)
SET AcquiredAt = @now,
    HeartbeatAt = @now,
    ExpiresAt = @expires,
    InstanceId = @instanceId,
    RunId = @runId
WHERE LeaseId = @leaseId
  AND ExpiresAt <= @now;

IF @@ROWCOUNT = 1
BEGIN
    SET @acquired = 1;
END
ELSE IF NOT EXISTS (SELECT 1 FROM dbo.DistributedLease WITH (UPDLOCK, HOLDLOCK) WHERE LeaseId = @leaseId)
BEGIN
    INSERT dbo.DistributedLease (LeaseId, AcquiredAt, HeartbeatAt, ExpiresAt, InstanceId, RunId)
    VALUES (@leaseId, @now, @now, @expires, @instanceId, @runId);
    SET @acquired = 1;
END;

COMMIT TRANSACTION;
SELECT @acquired;
""";
            AddParameter(command, "@leaseId", leaseId);
            AddParameter(command, "@instanceId", _instanceId);
            AddParameter(command, "@runId", runId);
            AddParameter(command, "@leaseSeconds", checked((int)Math.Ceiling(duration.TotalSeconds)));
            var result = await command.ExecuteScalarAsync(ct);
            var acquired = result is not null && result != DBNull.Value && Convert.ToBoolean(result);
            if (!acquired)
            {
                logger.LogInformation("DistributedLeaseBusy LeaseId={LeaseId} InstanceId={InstanceId}", leaseId, _instanceId);
                return null;
            }

            logger.LogInformation("DistributedLeaseAcquired LeaseId={LeaseId} InstanceId={InstanceId} DurationSeconds={DurationSeconds}",
                leaseId, _instanceId, duration.TotalSeconds);
            return new DistributedLeaseHandle(this, leaseId, _instanceId, runId, duration);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    public async Task<DistributedLeaseStatus?> GetStatusAsync(string leaseId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT LeaseId, InstanceId, RunId, AcquiredAt, HeartbeatAt, ExpiresAt, CASE WHEN ExpiresAt <= SYSUTCDATETIME() THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END FROM dbo.DistributedLease WHERE LeaseId = @leaseId;";
            AddParameter(command, "@leaseId", leaseId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new DistributedLeaseStatus(reader.GetString(0), reader.GetString(1), reader.GetString(2), new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero), new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero), new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero), reader.GetBoolean(6));
        }
        finally { if (openedHere) await connection.CloseAsync(); }
    }

    internal async Task<bool> RenewAsync(string leaseId, string instanceId, string runId, TimeSpan duration, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DECLARE @now datetime2(7) = SYSUTCDATETIME(); UPDATE dbo.DistributedLease SET HeartbeatAt = @now, ExpiresAt = DATEADD(SECOND, @leaseSeconds, @now) WHERE LeaseId = @leaseId AND InstanceId = @instanceId AND RunId = @runId AND ExpiresAt > @now;";
            AddParameter(command, "@leaseId", leaseId); AddParameter(command, "@instanceId", instanceId); AddParameter(command, "@runId", runId); AddParameter(command, "@leaseSeconds", checked((int)Math.Ceiling(duration.TotalSeconds)));
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }
        finally { if (openedHere) await connection.CloseAsync(); }
    }

    internal async Task ReleaseAsync(string leaseId, string instanceId, string runId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM dbo.DistributedLease WHERE LeaseId = @leaseId AND InstanceId = @instanceId AND RunId = @runId;";
            AddParameter(command, "@leaseId", leaseId);
            AddParameter(command, "@instanceId", instanceId);
            AddParameter(command, "@runId", runId);
            var released = await command.ExecuteNonQueryAsync(ct);
            logger.LogInformation("DistributedLeaseReleased LeaseId={LeaseId} InstanceId={InstanceId} ReleasedRows={ReleasedRows}",
                leaseId, instanceId, released);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    internal static void Validate(string leaseId, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) throw new ArgumentException("LeaseId is required.", nameof(leaseId));
        if (leaseId.Length > 160) throw new ArgumentOutOfRangeException(nameof(leaseId), "LeaseId cannot exceed 160 characters.");
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(6))
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be greater than zero and no more than six hours.");
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed class DistributedLeaseHandle : IAsyncDisposable
{
    private readonly DistributedLeaseManager _manager;
    private readonly string _leaseId;
    private readonly string _instanceId;
    private readonly string _runId;
    private readonly TimeSpan _duration;
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _renewalStop = new();
    private readonly Task _renewal;
    private int _released;

    internal DistributedLeaseHandle(DistributedLeaseManager manager, string leaseId, string instanceId, string runId, TimeSpan duration)
    {
        _manager = manager;
        _leaseId = leaseId;
        _instanceId = instanceId;
        _runId = runId;
        _duration = duration;
        _renewal = RenewUntilReleasedAsync();
    }

    /// <summary>Cancelled if SQL refuses a conditional heartbeat; callers must stop work promptly.</summary>
    public CancellationToken LostToken => _lost.Token;

    private async Task RenewUntilReleasedAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, Math.Min(30, _duration.TotalSeconds / 3)));
        try
        {
            while (!_renewalStop.IsCancellationRequested)
            {
                await Task.Delay(interval, _renewalStop.Token);
                if (!await _manager.RenewAsync(_leaseId, _instanceId, _runId, _duration, _renewalStop.Token))
                {
                    _lost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_renewalStop.IsCancellationRequested) { }
        catch { _lost.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _renewalStop.Cancel();
        try { await _renewal; } catch { }
        try { await _manager.ReleaseAsync(_leaseId, _instanceId, _runId, CancellationToken.None); }
        catch { /* Expiry still guarantees recovery if release cannot reach SQL. */ }
        _renewalStop.Dispose();
        _lost.Dispose();
    }
}
