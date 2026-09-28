IF COL_LENGTH(N'dbo.InvoiceRecordLines', N'BookingReservationId') IS NULL
BEGIN
    ALTER TABLE dbo.InvoiceRecordLines ADD BookingReservationId uniqueidentifier NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_InvoiceRecordLines_Reservation' AND parent_object_id = OBJECT_ID(N'dbo.InvoiceRecordLines'))
BEGIN
    ALTER TABLE dbo.InvoiceRecordLines ADD CONSTRAINT FK_InvoiceRecordLines_Reservation
        FOREIGN KEY (BookingReservationId) REFERENCES dbo.BookingReservations(Id);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InvoiceRecordLines_Reservation' AND object_id = OBJECT_ID(N'dbo.InvoiceRecordLines'))
BEGIN
    CREATE INDEX IX_InvoiceRecordLines_Reservation ON dbo.InvoiceRecordLines(BookingReservationId);
END;
