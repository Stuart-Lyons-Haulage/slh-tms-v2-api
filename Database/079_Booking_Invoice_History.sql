IF OBJECT_ID(N'dbo.BookingReservations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingReservations
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_BookingReservations PRIMARY KEY,
        CustomerCode nvarchar(40) NOT NULL,
        BookingType nvarchar(80) NOT NULL,
        StableBookingKey nvarchar(240) NOT NULL,
        CollectionDate date NOT NULL,
        DeliveryDate date NULL,
        CollectionDepot nvarchar(200) NULL,
        DeliverySite nvarchar(200) NULL,
        CollectionReference nvarchar(120) NULL,
        CratePurchaseOrder nvarchar(120) NULL,
        TransportPurchaseOrder nvarchar(120) NULL,
        ReservedUnits decimal(12,2) NOT NULL,
        UnitType nvarchar(40) NOT NULL,
        CompositionJson nvarchar(max) NOT NULL,
        PlannerNotes nvarchar(1000) NULL,
        Status int NOT NULL,
        CurrentRevisionNumber int NOT NULL,
        SourceStagedImportId uniqueidentifier NULL,
        SourceMovementId uniqueidentifier NULL,
        CreatedAtUtc datetimeoffset(7) NOT NULL,
        UpdatedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT UX_BookingReservations_Customer_Key UNIQUE (CustomerCode, StableBookingKey),
        CONSTRAINT FK_BookingReservations_StagedImport FOREIGN KEY (SourceStagedImportId) REFERENCES dbo.StagedImports(Id),
        CONSTRAINT FK_BookingReservations_OrderMovement FOREIGN KEY (SourceMovementId) REFERENCES dbo.OrderMovements(Id)
    );
    CREATE INDEX IX_BookingReservations_Date_Status ON dbo.BookingReservations(CollectionDate, Status);
    CREATE INDEX IX_BookingReservations_Customer_Reference ON dbo.BookingReservations(CustomerCode, CollectionReference);
END;

IF OBJECT_ID(N'dbo.BookingReservationRevisions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingReservationRevisions
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_BookingReservationRevisions PRIMARY KEY,
        BookingReservationId uniqueidentifier NOT NULL,
        RevisionNumber int NOT NULL,
        Status int NOT NULL,
        SourceRowKey nvarchar(120) NULL,
        SourceMessageId nvarchar(500) NULL,
        SourceAttachmentIdentity nvarchar(500) NULL,
        PayloadJson nvarchar(max) NOT NULL,
        ChangeNote nvarchar(1000) NULL,
        Actor nvarchar(200) NULL,
        CreatedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT UX_BookingReservationRevisions_Key UNIQUE (BookingReservationId, RevisionNumber),
        CONSTRAINT FK_BookingReservationRevisions_Reservation FOREIGN KEY (BookingReservationId) REFERENCES dbo.BookingReservations(Id) ON DELETE CASCADE
    );
END;

IF OBJECT_ID(N'dbo.BookingReservationAllocations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BookingReservationAllocations
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_BookingReservationAllocations PRIMARY KEY,
        BookingReservationId uniqueidentifier NOT NULL,
        TransportOrderId uniqueidentifier NULL,
        Destination nvarchar(200) NULL,
        Units decimal(12,2) NOT NULL,
        UnitType nvarchar(40) NOT NULL,
        Note nvarchar(200) NULL,
        CreatedAtUtc datetimeoffset(7) NOT NULL,
        CreatedBy nvarchar(200) NULL,
        CONSTRAINT FK_BookingReservationAllocations_Reservation FOREIGN KEY (BookingReservationId) REFERENCES dbo.BookingReservations(Id) ON DELETE CASCADE,
        CONSTRAINT FK_BookingReservationAllocations_Order FOREIGN KEY (TransportOrderId) REFERENCES dbo.TransportOrders(Id)
    );
    CREATE INDEX IX_BookingReservationAllocations_Reservation ON dbo.BookingReservationAllocations(BookingReservationId);
END;

IF OBJECT_ID(N'dbo.OperationalHistoryEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OperationalHistoryEvents
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OperationalHistoryEvents PRIMARY KEY,
        EntityType nvarchar(80) NOT NULL,
        EntityId uniqueidentifier NOT NULL,
        EventType nvarchar(80) NOT NULL,
        Actor nvarchar(200) NULL,
        PayloadJson nvarchar(max) NOT NULL,
        OccurredAtUtc datetimeoffset(7) NOT NULL
    );
    CREATE INDEX IX_OperationalHistoryEvents_Entity ON dbo.OperationalHistoryEvents(EntityType, EntityId, OccurredAtUtc);
END;

IF OBJECT_ID(N'dbo.InvoiceRecords', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceRecords
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_InvoiceRecords PRIMARY KEY,
        InvoiceNumber nvarchar(120) NULL,
        CustomerCode nvarchar(40) NOT NULL,
        LoadId uniqueidentifier NULL,
        TransportOrderId uniqueidentifier NULL,
        BookingReservationId uniqueidentifier NULL,
        InvoiceDate date NULL,
        NetAmount decimal(18,2) NULL,
        VatAmount decimal(18,2) NULL,
        GrossAmount decimal(18,2) NULL,
        Status int NOT NULL,
        Notes nvarchar(1000) NULL,
        PayloadJson nvarchar(max) NOT NULL,
        CreatedAtUtc datetimeoffset(7) NOT NULL,
        UpdatedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT FK_InvoiceRecords_Load FOREIGN KEY (LoadId) REFERENCES dbo.Loads(Id),
        CONSTRAINT FK_InvoiceRecords_Order FOREIGN KEY (TransportOrderId) REFERENCES dbo.TransportOrders(Id),
        CONSTRAINT FK_InvoiceRecords_Reservation FOREIGN KEY (BookingReservationId) REFERENCES dbo.BookingReservations(Id)
    );
    CREATE INDEX IX_InvoiceRecords_Number ON dbo.InvoiceRecords(InvoiceNumber);
    CREATE INDEX IX_InvoiceRecords_Customer_Status ON dbo.InvoiceRecords(CustomerCode, Status);
END;

IF OBJECT_ID(N'dbo.InvoiceRecordLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceRecordLines
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_InvoiceRecordLines PRIMARY KEY,
        InvoiceRecordId uniqueidentifier NOT NULL,
        LoadId uniqueidentifier NULL,
        TransportOrderId uniqueidentifier NULL,
        Description nvarchar(120) NULL,
        Quantity decimal(18,2) NULL,
        UnitAmount decimal(18,2) NULL,
        NetAmount decimal(18,2) NULL,
        OperationalReferenceSnapshot nvarchar(1000) NULL,
        CONSTRAINT FK_InvoiceRecordLines_Invoice FOREIGN KEY (InvoiceRecordId) REFERENCES dbo.InvoiceRecords(Id) ON DELETE CASCADE,
        CONSTRAINT FK_InvoiceRecordLines_Load FOREIGN KEY (LoadId) REFERENCES dbo.Loads(Id),
        CONSTRAINT FK_InvoiceRecordLines_Order FOREIGN KEY (TransportOrderId) REFERENCES dbo.TransportOrders(Id)
    );
    CREATE INDEX IX_InvoiceRecordLines_Invoice ON dbo.InvoiceRecordLines(InvoiceRecordId);
END;
