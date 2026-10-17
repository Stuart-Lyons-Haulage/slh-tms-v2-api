-- Barfoots' workbook distinguishes North and South collection points.
-- Keep the existing combined Sefter site for historical rows; new parses resolve
-- to these distinct physical sites instead.
IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'BARFOOTS-NORTH')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, CollectionAddress, OperationalRegion, Active)
        VALUES (NEWID(), N'BARFOOTS-NORTH', N'BARFOOTS', N'Barfoots North', N'Barfoots North', N'Pagham Road, Bognor Regis, PO21 3PX', N'Barfoots', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'BARFOOTS-SOUTH')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, CollectionAddress, OperationalRegion, Active)
        VALUES (NEWID(), N'BARFOOTS-SOUTH', N'BARFOOTS', N'Barfoots South', N'Barfoots South', N'Pagham Road, Bognor Regis, PO21 3PX', N'Barfoots', 1);
END;
