-- Hardens the shared Sites dependency used by geofence repair/reload.
-- Keeps malformed legacy site rows but prevents null required identifiers from
-- taking down geofence maintenance and tracking progression.

IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.Sites', 'ExternalCode') IS NULL ALTER TABLE dbo.Sites ADD ExternalCode nvarchar(40) NULL;
    IF COL_LENGTH('dbo.Sites', 'Name') IS NULL ALTER TABLE dbo.Sites ADD Name nvarchar(200) NULL;
    IF COL_LENGTH('dbo.Sites', 'DriverTextName') IS NULL ALTER TABLE dbo.Sites ADD DriverTextName nvarchar(200) NULL;
    IF COL_LENGTH('dbo.Sites', 'Active') IS NULL ALTER TABLE dbo.Sites ADD Active bit NULL;

    -- Do not invent operational site identities. Rows without a usable name are
    -- simply made inactive so they cannot participate in planning/geofence links.
    UPDATE dbo.Sites SET Active = COALESCE(Active, 1);
    UPDATE dbo.Sites
       SET Active = 0
     WHERE NULLIF(LTRIM(RTRIM(Name)), '') IS NULL;

    -- ExternalCode is required by the current model but older imports could leave
    -- it null. Use a deterministic legacy code so EF can materialise the row while
    -- preserving the original record for manual cleanup.
    UPDATE dbo.Sites
       SET ExternalCode = CONCAT('LEGACY-', LEFT(CONVERT(varchar(36), Id), 8))
     WHERE NULLIF(LTRIM(RTRIM(ExternalCode)), '') IS NULL;
END;
