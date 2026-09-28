IF COL_LENGTH(N'dbo.BookingReservationAllocations', N'IsActive') IS NULL
BEGIN
    ALTER TABLE dbo.BookingReservationAllocations
        ADD IsActive bit NOT NULL CONSTRAINT DF_BookingReservationAllocations_IsActive DEFAULT (1);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BookingReservationAllocations_Reservation_Active' AND object_id = OBJECT_ID(N'dbo.BookingReservationAllocations'))
BEGIN
    CREATE INDEX IX_BookingReservationAllocations_Reservation_Active
        ON dbo.BookingReservationAllocations(BookingReservationId, IsActive);
END;
