IF OBJECT_ID(N'dbo.TmsUsers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TmsUsers
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_TmsUsers PRIMARY KEY,
        Username nvarchar(120) NOT NULL,
        DisplayName nvarchar(160) NOT NULL,
        PasswordHash nvarchar(512) NOT NULL,
        Role nvarchar(80) NOT NULL,
        Active bit NOT NULL CONSTRAINT DF_TmsUsers_Active DEFAULT (1),
        CreatedAtUtc datetimeoffset NOT NULL,
        LastLoginAtUtc datetimeoffset NULL,
        PasswordChangedAtUtc datetimeoffset NULL
    );
    CREATE UNIQUE INDEX UX_TmsUsers_Username ON dbo.TmsUsers(Username);
    CREATE INDEX IX_TmsUsers_Active_Role ON dbo.TmsUsers(Active, Role);
END;
