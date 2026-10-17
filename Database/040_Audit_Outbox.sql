IF OBJECT_ID(N'dbo.AuditOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AuditOutbox
    (
        OutboxId uniqueidentifier NOT NULL CONSTRAINT PK_AuditOutbox PRIMARY KEY,
        EventType nvarchar(120) NOT NULL,
        Payload nvarchar(max) NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL,
        ProcessedAt datetimeoffset(7) NULL,
        FailedAt datetimeoffset(7) NULL,
        RetryCount int NOT NULL CONSTRAINT DF_AuditOutbox_RetryCount DEFAULT (0)
    );
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_AuditOutbox_Pending'
      AND object_id = OBJECT_ID(N'dbo.AuditOutbox')
)
BEGIN
    CREATE INDEX IX_AuditOutbox_Pending
        ON dbo.AuditOutbox(ProcessedAt, FailedAt, CreatedAt);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_AuditOutbox_CreatedAt'
      AND object_id = OBJECT_ID(N'dbo.AuditOutbox')
)
BEGIN
    CREATE INDEX IX_AuditOutbox_CreatedAt
        ON dbo.AuditOutbox(CreatedAt);
END;
