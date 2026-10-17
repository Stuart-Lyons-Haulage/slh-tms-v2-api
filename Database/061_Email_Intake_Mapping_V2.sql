/*
   Email intake mapping v2.
   SQL is authoritative. Sender/customer identity is separate from route rules.
   Duplicate protection uses SHA-256 keys so indexes remain safely inside SQL
   Server index key limits.
*/

IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NOT NULL
BEGIN
    ;WITH ranked AS
    (
        SELECT Id,
               ROW_NUMBER() OVER
               (
                   PARTITION BY LOWER(CustomerCode),
                                LOWER(ISNULL(SenderEmail, N'')),
                                LOWER(ISNULL(SenderDomain, N'')),
                                LOWER(ISNULL(SubjectContains, N''))
                   ORDER BY CASE WHEN RequiresReview = 0 THEN 0 ELSE 1 END, Id
               ) AS rn
        FROM dbo.CustomerEmailRoutes
        WHERE Active = 1
    )
    UPDATE target
       SET Active = 0
    FROM dbo.CustomerEmailRoutes target
    INNER JOIN ranked source ON source.Id = target.Id
    WHERE source.rn > 1;

    IF COL_LENGTH(N'dbo.CustomerEmailRoutes', N'NormalizedMappingHash') IS NULL
    BEGIN
        ALTER TABLE dbo.CustomerEmailRoutes ADD NormalizedMappingHash AS
            CONVERT(binary(32), HASHBYTES('SHA2_256', LOWER(CONCAT(
                CustomerCode, N'|', ISNULL(SenderEmail, N''), N'|',
                ISNULL(SenderDomain, N''), N'|', ISNULL(SubjectContains, N''))))) PERSISTED;
    END;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE name = N'UX_CustomerEmailRoutes_Active_NormalizedHash'
          AND object_id = OBJECT_ID(N'dbo.CustomerEmailRoutes')
    )
    BEGIN
        CREATE UNIQUE INDEX UX_CustomerEmailRoutes_Active_NormalizedHash
            ON dbo.CustomerEmailRoutes (NormalizedMappingHash)
            WHERE Active = 1;
    END;
END;

IF OBJECT_ID(N'dbo.OrderIntakeRouteRules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OrderIntakeRouteRules
    (
        Id uniqueidentifier NOT NULL CONSTRAINT DF_OrderIntakeRouteRules_Id DEFAULT NEWID(),
        CustomerCode nvarchar(40) NOT NULL,
        OriginSiteCode nvarchar(80) NULL,
        OriginSiteName nvarchar(200) NULL,
        RetailerCode nvarchar(80) NULL,
        DestinationSiteCode nvarchar(80) NULL,
        DestinationCode nvarchar(80) NULL,
        DestinationName nvarchar(200) NULL,
        DestinationPostcode nvarchar(20) NULL,
        Priority int NOT NULL CONSTRAINT DF_OrderIntakeRouteRules_Priority DEFAULT 100,
        ConfidenceScore int NOT NULL CONSTRAINT DF_OrderIntakeRouteRules_Confidence DEFAULT 80,
        Active bit NOT NULL CONSTRAINT DF_OrderIntakeRouteRules_Active DEFAULT 1,
        EffectiveFrom date NULL,
        EffectiveTo date NULL,
        Notes nvarchar(1000) NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_OrderIntakeRouteRules_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_OrderIntakeRouteRules_Updated DEFAULT SYSUTCDATETIME(),
        NormalizedRuleHash AS CONVERT(binary(32), HASHBYTES('SHA2_256', LOWER(CONCAT(
            CustomerCode, N'|', ISNULL(OriginSiteCode, N''), N'|', ISNULL(OriginSiteName, N''), N'|',
            ISNULL(RetailerCode, N''), N'|', ISNULL(DestinationSiteCode, N''), N'|',
            ISNULL(DestinationCode, N''), N'|', ISNULL(DestinationName, N''), N'|',
            ISNULL(DestinationPostcode, N''))))) PERSISTED,
        CONSTRAINT PK_OrderIntakeRouteRules PRIMARY KEY (Id),
        CONSTRAINT CK_OrderIntakeRouteRules_Confidence CHECK (ConfidenceScore BETWEEN 0 AND 100),
        CONSTRAINT CK_OrderIntakeRouteRules_Priority CHECK (Priority BETWEEN 0 AND 10000),
        CONSTRAINT CK_OrderIntakeRouteRules_EffectiveDates CHECK
            (EffectiveTo IS NULL OR EffectiveFrom IS NULL OR EffectiveTo >= EffectiveFrom)
    );

    CREATE UNIQUE INDEX UX_OrderIntakeRouteRules_Active_NormalizedHash
        ON dbo.OrderIntakeRouteRules (NormalizedRuleHash)
        WHERE Active = 1;

    CREATE INDEX IX_OrderIntakeRouteRules_Match
        ON dbo.OrderIntakeRouteRules (CustomerCode, Active, Priority)
        INCLUDE (OriginSiteCode, OriginSiteName, RetailerCode, DestinationSiteCode,
                 DestinationCode, DestinationName, DestinationPostcode,
                 ConfidenceScore, EffectiveFrom, EffectiveTo);
END;

/* Seed sender mappings only where a real active SQL Customer master record exists. */
IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
BEGIN
    DECLARE @NwfCode nvarchar(40) =
        (SELECT TOP (1) Code FROM dbo.Customers WHERE Active = 1 AND (Code = N'NWF' OR Name LIKE N'%Natures Way%') ORDER BY CASE WHEN Code = N'NWF' THEN 0 ELSE 1 END, Code);
    DECLARE @BarfootsCode nvarchar(40) =
        (SELECT TOP (1) Code FROM dbo.Customers WHERE Active = 1 AND (Code = N'BARFOOTS' OR Name LIKE N'%Barfoots%') ORDER BY CASE WHEN Code = N'BARFOOTS' THEN 0 ELSE 1 END, Code);
    DECLARE @LangmeadCode nvarchar(40) =
        (SELECT TOP (1) Code FROM dbo.Customers WHERE Active = 1 AND (Code IN (N'LANGMEADS', N'LANGMEAD') OR Name LIKE N'%Langmead%') ORDER BY CASE WHEN Code = N'LANGMEADS' THEN 0 WHEN Code = N'LANGMEAD' THEN 1 ELSE 2 END, Code);
    DECLARE @SummerBerryCode nvarchar(40) =
        (SELECT TOP (1) Code FROM dbo.Customers WHERE Active = 1 AND (Code LIKE N'SUMMER%BERRY%' OR Name LIKE N'%Summer Berry%') ORDER BY Code);
    DECLARE @ApsCode nvarchar(40) =
        (SELECT TOP (1) Code FROM dbo.Customers WHERE Active = 1 AND (Code = N'APS' OR Name LIKE N'%APS%') ORDER BY CASE WHEN Code = N'APS' THEN 0 ELSE 1 END, Code);

    IF @NwfCode IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@NwfCode AND SenderDomain=N'nwfltd.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@NwfCode,N'nwfltd.co.uk',0,1);
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@NwfCode AND SenderEmail=N'shiftlogisticalplanner@nwfltd.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderEmail,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@NwfCode,N'shiftlogisticalplanner@nwfltd.co.uk',N'nwfltd.co.uk',0,1);
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@NwfCode AND SenderEmail=N'mariuszurbanski@nwfltd.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderEmail,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@NwfCode,N'mariuszurbanski@nwfltd.co.uk',N'nwfltd.co.uk',0,1);
    END;

    IF @BarfootsCode IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@BarfootsCode AND SenderDomain=N'barfoots.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@BarfootsCode,N'barfoots.co.uk',0,1);
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@BarfootsCode AND SenderEmail=N'agnieszka.zawislan@barfoots.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderEmail,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@BarfootsCode,N'agnieszka.zawislan@barfoots.co.uk',N'barfoots.co.uk',0,1);
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@BarfootsCode AND SenderEmail=N'pawel.mlodawski@barfoots.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderEmail,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@BarfootsCode,N'pawel.mlodawski@barfoots.co.uk',N'barfoots.co.uk',0,1);
    END;

    IF @LangmeadCode IS NOT NULL
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@LangmeadCode AND SenderDomain=N'langmeadherbs.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@LangmeadCode,N'langmeadherbs.co.uk',0,1);
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@LangmeadCode AND SenderDomain=N'langmeadfarms.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@LangmeadCode,N'langmeadfarms.co.uk',0,1);
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@LangmeadCode AND SenderEmail=N'bartosztopolewski@langmeadherbs.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderEmail,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@LangmeadCode,N'bartosztopolewski@langmeadherbs.co.uk',N'langmeadherbs.co.uk',0,1);
        IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@LangmeadCode AND SenderEmail=N'kateryna.zhylchuk@langmeadherbs.co.uk' AND SubjectContains IS NULL)
            INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderEmail,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@LangmeadCode,N'kateryna.zhylchuk@langmeadherbs.co.uk',N'langmeadherbs.co.uk',0,1);
    END;

    IF @SummerBerryCode IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@SummerBerryCode AND SenderDomain=N'summerberry.co.uk' AND SubjectContains IS NULL)
        INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@SummerBerryCode,N'summerberry.co.uk',0,1);

    IF @ApsCode IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active=1 AND CustomerCode=@ApsCode AND SenderDomain=N'apsgroup.uk.com' AND SubjectContains IS NULL)
        INSERT dbo.CustomerEmailRoutes (Id,CustomerCode,SenderDomain,RequiresReview,Active) VALUES (NEWID(),@ApsCode,N'apsgroup.uk.com',0,1);
END;

/* Seed route examples. They are examples, not exclusive rules. */
IF OBJECT_ID(N'dbo.OrderIntakeRouteRules', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Customers WHERE Active=1 AND Code=N'NWF')
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Drayton' AND RetailerCode=N'ALDI' AND DestinationCode=N'ALD20')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Drayton',N'ALDI',N'ALD20',N'Aldi Sawley',10,95,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Drayton' AND RetailerCode=N'ALDI' AND DestinationCode=N'ALD21')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Drayton',N'ALDI',N'ALD21',N'Aldi Goldthorpe',10,95,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Drayton' AND RetailerCode=N'ALDI' AND DestinationCode=N'ALD23')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Drayton',N'ALDI',N'ALD23',N'Aldi Cardiff',10,95,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Drayton' AND RetailerCode=N'ALDI' AND DestinationCode=N'ALD24')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Drayton',N'ALDI',N'ALD24',N'Aldi Bolton',10,95,N'Seed route example; planner editable.');

        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Merston' AND RetailerCode=N'MORRISONS' AND DestinationCode=N'MOR06')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Merston',N'MORRISONS',N'MOR06',N'Bridgwater',10,95,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Merston' AND RetailerCode=N'MORRISONS' AND DestinationCode=N'MOR07')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Merston',N'MORRISONS',N'MOR07',N'Gadbrook',10,95,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Merston' AND RetailerCode=N'MORRISONS' AND DestinationCode=N'MOR08')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Merston',N'MORRISONS',N'MOR08',N'Latimer',10,95,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Merston' AND RetailerCode=N'MORRISONS' AND DestinationCode=N'MOR09')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Merston',N'MORRISONS',N'MOR09',N'Sittingbourne',10,95,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'NWF' AND OriginSiteName=N'Selsey' AND RetailerCode=N'MORRISONS' AND DestinationName=N'Dordon')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'NWF',N'Selsey',N'MORRISONS',N'Dordon',20,90,N'Seed route example; planner editable.');
    END;

    IF EXISTS (SELECT 1 FROM dbo.Customers WHERE Active=1 AND Code=N'BARFOOTS')
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'BARFOOTS' AND OriginSiteName=N'Sefter' AND RetailerCode=N'WAITROSE' AND DestinationName=N'Aylesford')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'BARFOOTS',N'Sefter',N'WAITROSE',N'Aylesford',20,90,N'Seed route example; planner editable.');
        IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active=1 AND CustomerCode=N'BARFOOTS' AND OriginSiteName=N'Leythorne' AND RetailerCode=N'WAITROSE' AND DestinationName=N'Leyland')
            INSERT dbo.OrderIntakeRouteRules (CustomerCode,OriginSiteName,RetailerCode,DestinationName,Priority,ConfidenceScore,Notes) VALUES (N'BARFOOTS',N'Leythorne',N'WAITROSE',N'Leyland',20,90,N'Seed route example; planner editable.');
    END;
END;
