IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Sites', N'OperationalRegion') IS NULL
    EXEC(N'ALTER TABLE dbo.Sites ADD OperationalRegion nvarchar(80) NULL;');

