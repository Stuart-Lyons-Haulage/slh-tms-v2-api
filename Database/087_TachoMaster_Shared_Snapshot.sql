IF OBJECT_ID(N'dbo.TachoMasterSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TachoMasterSnapshot
    (
        SnapshotId int NOT NULL CONSTRAINT PK_TachoMasterSnapshot PRIMARY KEY,
        Payload nvarchar(max) NOT NULL,
        RefreshedAtUtc datetimeoffset(7) NOT NULL,
        CONSTRAINT CK_TachoMasterSnapshot_Singleton CHECK (SnapshotId = 1)
    );
END;
