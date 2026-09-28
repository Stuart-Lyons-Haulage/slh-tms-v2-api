/*
   Repair the 079 Summer Berry site split without editing its immutable SQL.
   The historical Barfoots Leythorne geofence must remain linked to the
   Barfoots physical site; only the explicitly Summer Berry / Donaldsons
   geofence belongs to the Summer Berry Leythorne site.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.SiteGeofences', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    DECLARE @BarfootsLeythorne uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites WHERE ExternalCode = N'SITE169' OR ExternalCode = N'BARFOOTS-LEYTHORNE' ORDER BY CASE WHEN ExternalCode=N'BARFOOTS-LEYTHORNE' THEN 0 ELSE 1 END);

    IF @BarfootsLeythorne IS NOT NULL
    BEGIN
        UPDATE dbo.SiteGeofences
           SET SiteId = @BarfootsLeythorne,
               SiteNumber = (SELECT ExternalCode FROM dbo.Sites WHERE Id=@BarfootsLeythorne),
               UpdatedAtUtc = SYSUTCDATETIME()
         WHERE Active = 1
           AND UPPER(Name) LIKE N'%LEYTHORNE%'
           AND UPPER(Name) LIKE N'%BARFOOTS%';
    END;
END;

COMMIT TRANSACTION;
