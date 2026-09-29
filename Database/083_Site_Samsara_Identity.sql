/*
  Canonical Site identity for Samsara route dispatch.

  Site.ExternalCode is the stable SLH reference used as the Samsara address
  external ID. Existing SITE### values are retained when unique; blank,
  legacy, malformed, and repeated values receive the next unused SITE###
  number in deterministic Id order. No Site row is deleted or merged here.
  Duplicate physical locations remain visible to the existing review/merge
  workflow so an operationally distinct site is never removed by a migration.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    CREATE TABLE #SiteCodePlan
    (
        Id uniqueidentifier NOT NULL PRIMARY KEY,
        NewCode nvarchar(40) NOT NULL UNIQUE
    );

    ;WITH ExistingCodes AS
    (
        SELECT
            Id,
            ExternalCode,
            ROW_NUMBER() OVER
            (
                PARTITION BY UPPER(LTRIM(RTRIM(ExternalCode)))
                ORDER BY Id
            ) AS CodeRank
        FROM dbo.Sites
    )
    INSERT #SiteCodePlan (Id, NewCode)
    SELECT Id, UPPER(LTRIM(RTRIM(ExternalCode)))
    FROM ExistingCodes
    WHERE CodeRank = 1
      AND LEN(LTRIM(RTRIM(ExternalCode))) = 7
      AND UPPER(LEFT(LTRIM(RTRIM(ExternalCode)), 4)) = N'SITE'
      AND LTRIM(RTRIM(ExternalCode)) NOT LIKE N'%[^A-Za-z0-9]%'
      AND SUBSTRING(LTRIM(RTRIM(ExternalCode)), 5, 3) NOT LIKE N'%[^0-9]%'
      AND TRY_CONVERT(int, SUBSTRING(LTRIM(RTRIM(ExternalCode)), 5, 3)) > 0;

    DECLARE @Id uniqueidentifier;
    DECLARE @Next int = 1;
    DECLARE @Code nvarchar(40);
    DECLARE SiteCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT Id
        FROM dbo.Sites
        WHERE Id NOT IN (SELECT Id FROM #SiteCodePlan)
        ORDER BY Id;

    OPEN SiteCursor;
    FETCH NEXT FROM SiteCursor INTO @Id;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        WHILE EXISTS (SELECT 1 FROM #SiteCodePlan WHERE NewCode = CONCAT(N'SITE', RIGHT(CONCAT(N'000', @Next), 3)))
            SET @Next += 1;

        SET @Code = CONCAT(N'SITE', RIGHT(CONCAT(N'000', @Next), 3));
        INSERT #SiteCodePlan (Id, NewCode) VALUES (@Id, @Code);
        SET @Next += 1;
        FETCH NEXT FROM SiteCursor INTO @Id;
    END;
    CLOSE SiteCursor;
    DEALLOCATE SiteCursor;

    -- Free the unique index before assigning the planned values.
    UPDATE dbo.Sites
       SET ExternalCode = CONCAT(N'TMP-', CONVERT(nvarchar(36), Id));

    UPDATE site
       SET ExternalCode = codePlan.NewCode
    FROM dbo.Sites site
    INNER JOIN #SiteCodePlan codePlan ON codePlan.Id = site.Id;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Sites')
          AND name = N'UX_Sites_ExternalCode_Canonical'
    )
        CREATE UNIQUE INDEX UX_Sites_ExternalCode_Canonical ON dbo.Sites(ExternalCode);

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID(N'dbo.Sites')
          AND name = N'CK_Sites_ExternalCode_NotBlank'
    )
        ALTER TABLE dbo.Sites ADD CONSTRAINT CK_Sites_ExternalCode_NotBlank
            CHECK (LEN(LTRIM(RTRIM(ExternalCode))) > 0);

    DROP TABLE #SiteCodePlan;
END;

COMMIT TRANSACTION;
