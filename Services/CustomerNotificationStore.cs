using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class CustomerNotificationStore
{
    public static async Task EnsureSchemaAsync(TmsDbContext db, CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID(N'[dbo].[CustomerNotificationLogs]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[CustomerNotificationLogs](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [CustomerId] uniqueidentifier NOT NULL,
        [OrderReference] nvarchar(120) NOT NULL,
        [StopId] uniqueidentifier NOT NULL,
        [Type] int NOT NULL,
        [SentAtUtc] datetimeoffset NOT NULL,
        [EtaWindowCommunicated] nvarchar(120) NOT NULL,
        [ActualArrivalUtc] datetimeoffset NULL,
        [ActualVarianceMinutes] int NULL,
        [Channel] int NOT NULL,
        [RecipientAddress] nvarchar(320) NOT NULL,
        [DeliveryConfirmed] bit NOT NULL CONSTRAINT [DF_CustomerNotificationLogs_DeliveryConfirmed] DEFAULT 0,
        [FailureReason] nvarchar(1000) NULL
    );
    CREATE INDEX [IX_CustomerNotificationLogs_Customer_Sent] ON [dbo].[CustomerNotificationLogs]([CustomerId],[SentAtUtc] DESC);
    CREATE INDEX [IX_CustomerNotificationLogs_Stop_Sent] ON [dbo].[CustomerNotificationLogs]([StopId],[SentAtUtc] DESC);
    CREATE INDEX [IX_CustomerNotificationLogs_PendingAccuracy] ON [dbo].[CustomerNotificationLogs]([DeliveryConfirmed],[SentAtUtc]) INCLUDE ([StopId],[ActualArrivalUtc],[ActualVarianceMinutes]);
END;
""";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public static async Task InsertAsync(TmsDbContext db, CustomerNotificationLog log, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [dbo].[CustomerNotificationLogs]
            ([Id],[CustomerId],[OrderReference],[StopId],[Type],[SentAtUtc],[EtaWindowCommunicated],[ActualArrivalUtc],[ActualVarianceMinutes],[Channel],[RecipientAddress],[DeliveryConfirmed],[FailureReason])
            VALUES
            ({log.Id},{log.CustomerId},{log.OrderReference},{log.StopId},{(int)log.Type},{log.SentAtUtc},{log.EtaWindowCommunicated},{log.ActualArrivalUtc},{log.ActualVarianceMinutes},{(int)log.Channel},{log.RecipientAddress},{log.DeliveryConfirmed},{log.FailureReason})
            """, ct);
    }

    public static async Task<List<CustomerNotificationLog>> ListAsync(
        TmsDbContext db,
        Guid? customerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        if (customerId is Guid id)
        {
            return await db.Database.SqlQuery<CustomerNotificationLog>($"""
                SELECT [Id],[CustomerId],[OrderReference],[StopId],[Type],[SentAtUtc],[EtaWindowCommunicated],
                       [ActualArrivalUtc],[ActualVarianceMinutes],[Channel],[RecipientAddress],[DeliveryConfirmed],[FailureReason]
                FROM [dbo].[CustomerNotificationLogs]
                WHERE [CustomerId] = {id} AND [SentAtUtc] >= {fromUtc} AND [SentAtUtc] < {toUtc}
                ORDER BY [SentAtUtc] DESC
                """).ToListAsync(ct);
        }

        return await db.Database.SqlQuery<CustomerNotificationLog>($"""
            SELECT [Id],[CustomerId],[OrderReference],[StopId],[Type],[SentAtUtc],[EtaWindowCommunicated],
                   [ActualArrivalUtc],[ActualVarianceMinutes],[Channel],[RecipientAddress],[DeliveryConfirmed],[FailureReason]
            FROM [dbo].[CustomerNotificationLogs]
            WHERE [SentAtUtc] >= {fromUtc} AND [SentAtUtc] < {toUtc}
            ORDER BY [SentAtUtc] DESC
            """).ToListAsync(ct);
    }

    public static async Task<List<CustomerNotificationLog>> PendingAccuracyAsync(TmsDbContext db, DateTimeOffset olderThanUtc, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        return await db.Database.SqlQuery<CustomerNotificationLog>($"""
            SELECT [Id],[CustomerId],[OrderReference],[StopId],[Type],[SentAtUtc],[EtaWindowCommunicated],
                   [ActualArrivalUtc],[ActualVarianceMinutes],[Channel],[RecipientAddress],[DeliveryConfirmed],[FailureReason]
            FROM [dbo].[CustomerNotificationLogs]
            WHERE [DeliveryConfirmed] = CAST(0 AS bit) AND [SentAtUtc] < {olderThanUtc}
            ORDER BY [SentAtUtc]
            """).ToListAsync(ct);
    }

    public static async Task UpdateActualAsync(
        TmsDbContext db,
        Guid logId,
        DateTimeOffset actualArrivalUtc,
        int? varianceMinutes,
        CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [dbo].[CustomerNotificationLogs]
            SET [ActualArrivalUtc] = {actualArrivalUtc},
                [ActualVarianceMinutes] = {varianceMinutes},
                [DeliveryConfirmed] = CAST(1 AS bit)
            WHERE [Id] = {logId}
            """, ct);
    }
}
