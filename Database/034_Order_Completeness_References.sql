IF OBJECT_ID(N'dbo.OrderReferenceIssues', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OrderReferenceIssues
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OrderReferenceIssues PRIMARY KEY,
        MovementId uniqueidentifier NOT NULL,
        TransportOrderId uniqueidentifier NULL,
        ReferenceType nvarchar(40) NOT NULL,
        Status int NOT NULL CONSTRAINT DF_OrderReferenceIssues_Status DEFAULT(0),
        Owner nvarchar(200) NULL,
        Notes nvarchar(1000) NULL,
        DetectedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_OrderReferenceIssues_DetectedAtUtc DEFAULT(SYSUTCDATETIME()),
        ResolvedAtUtc datetimeoffset NULL,
        ResolvedBy nvarchar(200) NULL,
        CONSTRAINT FK_OrderReferenceIssues_OrderMovements_MovementId FOREIGN KEY(MovementId) REFERENCES dbo.OrderMovements(Id),
        CONSTRAINT FK_OrderReferenceIssues_TransportOrders_TransportOrderId FOREIGN KEY(TransportOrderId) REFERENCES dbo.TransportOrders(Id)
    );
    CREATE INDEX IX_OrderReferenceIssues_MovementId_ReferenceType_Status ON dbo.OrderReferenceIssues(MovementId, ReferenceType, Status);
END;

IF OBJECT_ID(N'dbo.ReferenceChaseEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReferenceChaseEvents
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_ReferenceChaseEvents PRIMARY KEY,
        ReferenceIssueId uniqueidentifier NOT NULL,
        EventType nvarchar(40) NOT NULL,
        Recipient nvarchar(320) NULL,
        ProviderMessageId nvarchar(500) NULL,
        ProviderThreadId nvarchar(500) NULL,
        Note nvarchar(1000) NULL,
        Actor nvarchar(200) NULL,
        OccurredAtUtc datetimeoffset NOT NULL CONSTRAINT DF_ReferenceChaseEvents_OccurredAtUtc DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_ReferenceChaseEvents_OrderReferenceIssues_ReferenceIssueId FOREIGN KEY(ReferenceIssueId) REFERENCES dbo.OrderReferenceIssues(Id)
    );
    CREATE INDEX IX_ReferenceChaseEvents_ReferenceIssueId_OccurredAtUtc ON dbo.ReferenceChaseEvents(ReferenceIssueId, OccurredAtUtc);
END;
