using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Slh.Tms.Api.Services;

public sealed class SqlLatencyInterceptor(TmsMetrics metrics) : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "reader");
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "reader");
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "scalar");
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "scalar");
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "non_query");
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        metrics.RecordSqlQueryLatency(eventData.Duration.TotalMilliseconds, "non_query");
        return ValueTask.FromResult(result);
    }
}
