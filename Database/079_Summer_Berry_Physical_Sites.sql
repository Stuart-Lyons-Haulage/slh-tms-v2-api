/*
   Summer Berry physical Site Master separation.

   SITE177 was previously used as a generic Summer Berry / Colworth record and
   had multiple farm geofences attached to it. Keep that identity as the
   Groves Farm / Colworth site, add the other physical farms, and re-link only
   unambiguous named geofences. No geofence history is deleted.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    DECLARE @Groves uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites WHERE ExternalCode = N'SITE177');

    IF @Groves IS NULL
    BEGIN
        SET @Groves = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, CollectionAddress, OperationalRegion, Active)
        VALUES (@Groves, N'SUMMER-BERRY-GROVES', N'SUMMER-BERRY', N'Summer Berry Groves Farm', N'Groves Farm', N'Groves Farm, Colworth, Chichester, West Sussex, PO20 2DX', N'Summer Berry', 1);
    END
    ELSE
    BEGIN
        UPDATE dbo.Sites
           SET CustomerCode = N'SUMMER-BERRY',
               Name = N'Summer Berry Groves Farm',
               DriverTextName = N'Groves Farm',
               CollectionAddress = COALESCE(NULLIF(CollectionAddress, N''), N'Groves Farm, Colworth, Chichester, West Sussex, PO20 2DX'),
               OperationalRegion = N'Summer Berry',
               Active = 1
         WHERE Id = @Groves;
    END;

    DECLARE @Manor uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites WHERE ExternalCode = N'SUMMER-BERRY-MANOR');
    IF @Manor IS NULL
    BEGIN
        SET @Manor = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, CollectionAddress, OperationalRegion, Active)
        VALUES (@Manor, N'SUMMER-BERRY-MANOR', N'SUMMER-BERRY', N'Summer Berry Manor Farm', N'Manor Farm', N'Colworth Manor Farm, Colworth Lane, Colworth, Chichester, West Sussex, PO20 2DU', N'Summer Berry', 1);
    END
    ELSE
        UPDATE dbo.Sites SET CustomerCode=N'SUMMER-BERRY', Name=N'Summer Berry Manor Farm', DriverTextName=N'Manor Farm', CollectionAddress=COALESCE(NULLIF(CollectionAddress,N''),N'Colworth Manor Farm, Colworth Lane, Colworth, Chichester, West Sussex, PO20 2DU'), OperationalRegion=N'Summer Berry', Active=1 WHERE Id=@Manor;

    DECLARE @Leythorne uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites WHERE ExternalCode = N'SUMMER-BERRY-LEYTHORNE');
    IF @Leythorne IS NULL
    BEGIN
        SET @Leythorne = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, CollectionAddress, OperationalRegion, Active)
        VALUES (@Leythorne, N'SUMMER-BERRY-LEYTHORNE', N'SUMMER-BERRY', N'Summer Berry Leythorne / Donaldsons', N'Leythorne / Donaldsons', N'Leythorne Nurseries, Vinnetrow Road, Runcton, Chichester, West Sussex, PO20 1QB', N'Summer Berry', 1);
    END
    ELSE
        UPDATE dbo.Sites SET CustomerCode=N'SUMMER-BERRY', Name=N'Summer Berry Leythorne / Donaldsons', DriverTextName=N'Leythorne / Donaldsons', CollectionAddress=COALESCE(NULLIF(CollectionAddress,N''),N'Leythorne Nurseries, Vinnetrow Road, Runcton, Chichester, West Sussex, PO20 1QB'), OperationalRegion=N'Summer Berry', Active=1 WHERE Id=@Leythorne;

    DECLARE @Kives uniqueidentifier =
        (SELECT TOP (1) Id FROM dbo.Sites WHERE ExternalCode = N'SUMMER-BERRY-KIVES');
    IF @Kives IS NULL
    BEGIN
        SET @Kives = NEWID();
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, CollectionAddress, OperationalRegion, Active)
        VALUES (@Kives, N'SUMMER-BERRY-KIVES', N'SUMMER-BERRY', N'Summer Berry Kives Farm', N'Kives Farm', N'Kives Farm, Bognor Road, Chichester, West Sussex, PO20 1EH', N'Summer Berry', 1);
    END
    ELSE
        UPDATE dbo.Sites SET CustomerCode=N'SUMMER-BERRY', Name=N'Summer Berry Kives Farm', DriverTextName=N'Kives Farm', CollectionAddress=COALESCE(NULLIF(CollectionAddress,N''),N'Kives Farm, Bognor Road, Chichester, West Sussex, PO20 1EH'), OperationalRegion=N'Summer Berry', Active=1 WHERE Id=@Kives;

    IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
    BEGIN
        -- Master-detail is the audited home for aliases and source evidence because
        -- these fields are intentionally not stored on dbo.Sites.
        UPDATE dbo.StagedImports
           SET PayloadJson = JSON_MODIFY(JSON_MODIFY(JSON_MODIFY(PayloadJson, '$.Name', N'Summer Berry Groves Farm'), '$.DriverTextName', N'Groves Farm'), '$.Aliases', N'Colworth, Colworth Farm, Summer Berry, Summer Berry Colworth, SB-Groves Farm'),
               Source = N'Summer Berry physical site reconciliation',
               ReviewedAtUtc = SYSUTCDATETIME(),
               ReviewNote = N'Physical Summer Berry sites separated; Colworth is retained as the Groves Farm alias.'
         WHERE EntityType=N'masterdetail:site' AND IdempotencyKey=N'masterdetail:site:site177';

        IF NOT EXISTS (SELECT 1 FROM dbo.StagedImports WHERE EntityType=N'masterdetail:site' AND IdempotencyKey=N'masterdetail:site:summer-berry-manor')
            INSERT dbo.StagedImports (Id, EntityType, IdempotencyKey, PayloadJson, Status, Source, ReceivedAtUtc, ReviewedAtUtc, ReviewNote)
            VALUES (NEWID(), N'masterdetail:site', N'masterdetail:site:summer-berry-manor',
                    (SELECT (SELECT @Manor AS Id, N'SUMMER-BERRY-MANOR' AS ExternalCode, N'SUMMER-BERRY' AS CustomerCode, N'Summer Berry Manor Farm' AS Name, N'Manor Farm' AS DriverTextName, N'Colworth Manor Farm, Colworth Lane, Colworth, Chichester, West Sussex, PO20 2DU' AS CollectionAddress, N'Manor Farm, Colworth Manor Farm' AS Aliases FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)), 3, N'Summer Berry physical site reconciliation', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Physical Summer Berry site.');

        IF NOT EXISTS (SELECT 1 FROM dbo.StagedImports WHERE EntityType=N'masterdetail:site' AND IdempotencyKey=N'masterdetail:site:summer-berry-leythorne')
            INSERT dbo.StagedImports (Id, EntityType, IdempotencyKey, PayloadJson, Status, Source, ReceivedAtUtc, ReviewedAtUtc, ReviewNote)
            VALUES (NEWID(), N'masterdetail:site', N'masterdetail:site:summer-berry-leythorne',
                    (SELECT (SELECT @Leythorne AS Id, N'SUMMER-BERRY-LEYTHORNE' AS ExternalCode, N'SUMMER-BERRY' AS CustomerCode, N'Summer Berry Leythorne / Donaldsons' AS Name, N'Leythorne / Donaldsons' AS DriverTextName, N'Leythorne Nurseries, Vinnetrow Road, Runcton, Chichester, West Sussex, PO20 1QB' AS CollectionAddress, N'Leythorne, Leythorne Nurseries, Donaldsons, Donaldsons C Block' AS Aliases FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)), 3, N'Summer Berry physical site reconciliation', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Physical Summer Berry site.');

        IF NOT EXISTS (SELECT 1 FROM dbo.StagedImports WHERE EntityType=N'masterdetail:site' AND IdempotencyKey=N'masterdetail:site:summer-berry-kives')
            INSERT dbo.StagedImports (Id, EntityType, IdempotencyKey, PayloadJson, Status, Source, ReceivedAtUtc, ReviewedAtUtc, ReviewNote)
            VALUES (NEWID(), N'masterdetail:site', N'masterdetail:site:summer-berry-kives',
                    (SELECT (SELECT @Kives AS Id, N'SUMMER-BERRY-KIVES' AS ExternalCode, N'SUMMER-BERRY' AS CustomerCode, N'Summer Berry Kives Farm' AS Name, N'Kives Farm' AS DriverTextName, N'Kives Farm, Bognor Road, Chichester, West Sussex, PO20 1EH' AS CollectionAddress, N'Kives' AS Aliases FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)), 3, N'Summer Berry physical site reconciliation', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Physical Summer Berry site.');
    END;

    IF OBJECT_ID(N'dbo.SiteGeofences', N'U') IS NOT NULL
    BEGIN
        UPDATE dbo.SiteGeofences SET SiteId=@Groves, SiteNumber=(SELECT ExternalCode FROM dbo.Sites WHERE Id=@Groves), UpdatedAtUtc=SYSUTCDATETIME()
         WHERE Active=1 AND (UPPER(Name) LIKE N'%GROVES FARM%' OR UPPER(Name) LIKE N'%COLWORTH%') AND (SiteId IS NULL OR SiteId=@Groves);
        UPDATE dbo.SiteGeofences SET SiteId=@Manor, SiteNumber=(SELECT ExternalCode FROM dbo.Sites WHERE Id=@Manor), UpdatedAtUtc=SYSUTCDATETIME()
         WHERE Active=1 AND UPPER(Name) LIKE N'%MANOR FARM%' AND SiteId<>@Manor;
        UPDATE dbo.SiteGeofences SET SiteId=@Leythorne, SiteNumber=(SELECT ExternalCode FROM dbo.Sites WHERE Id=@Leythorne), UpdatedAtUtc=SYSUTCDATETIME()
         WHERE Active=1 AND (UPPER(Name) LIKE N'%LEYTHORNE%' OR UPPER(Name) LIKE N'%DONALDSON%') AND SiteId<>@Leythorne;
        UPDATE dbo.SiteGeofences SET SiteId=@Kives, SiteNumber=(SELECT ExternalCode FROM dbo.Sites WHERE Id=@Kives), UpdatedAtUtc=SYSUTCDATETIME()
         WHERE Active=1 AND UPPER(Name) LIKE N'%KIVES FARM%' AND SiteId<>@Kives;
    END;
END;

COMMIT TRANSACTION;
