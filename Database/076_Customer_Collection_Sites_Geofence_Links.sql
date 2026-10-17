/*
  Customer collection sites + geofence linkage repair.

  Purpose:
  - make regular collection locations visible in Site Master;
  - keep Customer and physical Site as separate identities;
  - link approved/live RoadTech geofences to the canonical Site where the physical
    location is unambiguous;
  - preserve any existing explicit/manual SiteId link to a different Site.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'GHS')
        INSERT dbo.Customers (Id, Code, Name, Active)
        VALUES (NEWID(), N'GHS', N'Greenhouse Growers', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'NWF')
        INSERT dbo.Customers (Id, Code, Name, Active)
        VALUES (NEWID(), N'NWF', N'Natures Way Foods', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'LANGMEADS')
        INSERT dbo.Customers (Id, Code, Name, Active)
        VALUES (NEWID(), N'LANGMEADS', N'Langmead Herbs', 1);
END;

IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    DECLARE @Greenhouse uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites
         WHERE Active = 1 AND
               (CustomerCode = N'GHS' OR Name LIKE N'%Greenhouse%')
         ORDER BY CASE WHEN CustomerCode = N'GHS' THEN 0 ELSE 1 END, Name);

    IF @Greenhouse IS NULL
    BEGIN
        SET @Greenhouse = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (@Greenhouse, N'GHS-GREENHOUSE', N'GHS', N'Greenhouse Growers', N'Greenhouse', N'South East', 1);
    END
    ELSE
    BEGIN
        UPDATE dbo.Sites
           SET CustomerCode = N'GHS',
               DriverTextName = CASE WHEN NULLIF(LTRIM(RTRIM(DriverTextName)), '') IS NULL THEN N'Greenhouse' ELSE DriverTextName END,
               Active = 1
         WHERE Id = @Greenhouse;
    END;

    DECLARE @Langmeads uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites
         WHERE Active = 1 AND
               (CustomerCode = N'LANGMEADS' OR Name LIKE N'%Ham Farm%' OR Name LIKE N'%Langmead%')
         ORDER BY CASE WHEN CustomerCode = N'LANGMEADS' THEN 0 ELSE 1 END, Name);

    IF @Langmeads IS NULL
    BEGIN
        SET @Langmeads = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (@Langmeads, N'LANGMEADS-HAM-FARM', N'LANGMEADS', N'Ham Farm', N'Langmeads / Ham Farm', N'South East', 1);
    END
    ELSE
    BEGIN
        UPDATE dbo.Sites
           SET CustomerCode = N'LANGMEADS',
               DriverTextName = N'Langmeads / Ham Farm',
               Active = 1
         WHERE Id = @Langmeads;
    END;

    DECLARE @NwfSelsey uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites
         WHERE Active = 1 AND CustomerCode = N'NWF' AND Name LIKE N'%Selsey%');
    IF @NwfSelsey IS NULL
    BEGIN
        SET @NwfSelsey = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (@NwfSelsey, N'NWF-SELSEY', N'NWF', N'NWF Selsey', N'NWF-Selsey', N'South East', 1);
    END;

    DECLARE @NwfRuncton uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites
         WHERE Active = 1 AND CustomerCode = N'NWF' AND Name LIKE N'%Runcton%');
    IF @NwfRuncton IS NULL
    BEGIN
        SET @NwfRuncton = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (@NwfRuncton, N'NWF-RUNCTON', N'NWF', N'NWF Runcton', N'NWF-Runcton', N'South East', 1);
    END;

    DECLARE @NwfMerston uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites
         WHERE Active = 1 AND CustomerCode = N'NWF' AND Name LIKE N'%Merston%');
    IF @NwfMerston IS NULL
    BEGIN
        SET @NwfMerston = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (@NwfMerston, N'NWF-MERSTON', N'NWF', N'NWF Merston', N'NWF-Merston', N'South East', 1);
    END;

    DECLARE @NwfDrayton uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites
         WHERE Active = 1 AND CustomerCode = N'NWF' AND Name LIKE N'%Drayton%');
    IF @NwfDrayton IS NULL
    BEGIN
        SET @NwfDrayton = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (@NwfDrayton, N'NWF-DRAYTON', N'NWF', N'NWF Drayton', N'NWF-Drayton', N'South East', 1);
    END;

    -- Keep already-existing NWF rows clearly owned by the NWF customer.
    UPDATE dbo.Sites
       SET CustomerCode = N'NWF',
           Active = 1
     WHERE Id IN (@NwfSelsey, @NwfRuncton, @NwfMerston, @NwfDrayton);

    IF OBJECT_ID(N'dbo.SiteGeofences', N'U') IS NOT NULL
    BEGIN
        DECLARE @GreenhouseCode nvarchar(40) = (SELECT ExternalCode FROM dbo.Sites WHERE Id = @Greenhouse);
        DECLARE @LangmeadsCode nvarchar(40) = (SELECT ExternalCode FROM dbo.Sites WHERE Id = @Langmeads);
        DECLARE @SelseyCode nvarchar(40) = (SELECT ExternalCode FROM dbo.Sites WHERE Id = @NwfSelsey);
        DECLARE @RunctonCode nvarchar(40) = (SELECT ExternalCode FROM dbo.Sites WHERE Id = @NwfRuncton);
        DECLARE @MerstonCode nvarchar(40) = (SELECT ExternalCode FROM dbo.Sites WHERE Id = @NwfMerston);
        DECLARE @DraytonCode nvarchar(40) = (SELECT ExternalCode FROM dbo.Sites WHERE Id = @NwfDrayton);

        UPDATE dbo.SiteGeofences
           SET SiteId = @Greenhouse,
               SiteNumber = @GreenhouseCode,
               UpdatedAtUtc = SYSUTCDATETIME()
         WHERE Active = 1
           AND (SiteId IS NULL OR SiteId = @Greenhouse)
           AND (UPPER(Name) LIKE N'%GREENHOUSE%' OR UPPER(Name) LIKE N'%GREEN HOUSE%');

        UPDATE dbo.SiteGeofences
           SET SiteId = @Langmeads,
               SiteNumber = @LangmeadsCode,
               UpdatedAtUtc = SYSUTCDATETIME()
         WHERE Active = 1
           AND (SiteId IS NULL OR SiteId = @Langmeads)
           AND (UPPER(Name) LIKE N'%LANGMEAD%' OR UPPER(Name) LIKE N'%HAM FARM%');

        UPDATE dbo.SiteGeofences
           SET SiteId = @NwfSelsey,
               SiteNumber = @SelseyCode,
               UpdatedAtUtc = SYSUTCDATETIME()
         WHERE Active = 1
           AND (SiteId IS NULL OR SiteId = @NwfSelsey)
           AND UPPER(Name) LIKE N'%SELSEY%'
           AND UPPER(Name) LIKE N'%NATURE%WAY%';

        UPDATE dbo.SiteGeofences
           SET SiteId = @NwfRuncton,
               SiteNumber = @RunctonCode,
               UpdatedAtUtc = SYSUTCDATETIME()
         WHERE Active = 1
           AND (SiteId IS NULL OR SiteId = @NwfRuncton)
           AND UPPER(Name) LIKE N'%RUNCTON%'
           AND UPPER(Name) LIKE N'%NATURE%WAY%';

        UPDATE dbo.SiteGeofences
           SET SiteId = @NwfMerston,
               SiteNumber = @MerstonCode,
               UpdatedAtUtc = SYSUTCDATETIME()
         WHERE Active = 1
           AND (SiteId IS NULL OR SiteId = @NwfMerston)
           AND UPPER(Name) LIKE N'%MERSTON%'
           AND UPPER(Name) LIKE N'%NATURE%WAY%';

        UPDATE dbo.SiteGeofences
           SET SiteId = @NwfDrayton,
               SiteNumber = @DraytonCode,
               UpdatedAtUtc = SYSUTCDATETIME()
         WHERE Active = 1
           AND (SiteId IS NULL OR SiteId = @NwfDrayton)
           AND UPPER(Name) LIKE N'%DRAYTON%'
           AND UPPER(Name) LIKE N'%NATURE%WAY%';
    END;
END;

COMMIT TRANSACTION;
