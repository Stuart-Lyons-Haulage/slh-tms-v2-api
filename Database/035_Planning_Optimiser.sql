IF OBJECT_ID(N'dbo.PlanProposals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlanProposals
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PlanProposals PRIMARY KEY,
        PlanningDate date NOT NULL,
        Period nvarchar(10) NOT NULL,
        Version int NOT NULL,
        Status nvarchar(40) NOT NULL,
        Classification nvarchar(40) NOT NULL,
        InputHash nvarchar(128) NOT NULL,
        EvidenceJson nvarchar(max) NOT NULL,
        WarningsJson nvarchar(max) NOT NULL,
        EvidenceCapturedAtUtc datetimeoffset NOT NULL,
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_PlanProposals_CreatedAtUtc DEFAULT(SYSUTCDATETIME()),
        CreatedBy nvarchar(200) NULL
    );
    CREATE UNIQUE INDEX IX_PlanProposals_PlanningDate_Period_Version ON dbo.PlanProposals(PlanningDate, Period, Version);
    CREATE INDEX IX_PlanProposals_InputHash ON dbo.PlanProposals(InputHash);
END;

IF OBJECT_ID(N'dbo.PlanProposalRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlanProposalRuns
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PlanProposalRuns PRIMARY KEY,
        ProposalId uniqueidentifier NOT NULL,
        Sequence int NOT NULL,
        Reference nvarchar(80) NOT NULL,
        IsLocked bit NOT NULL CONSTRAINT DF_PlanProposalRuns_IsLocked DEFAULT(0),
        LiveLoadId uniqueidentifier NULL,
        Classification nvarchar(40) NOT NULL,
        DriverId uniqueidentifier NULL,
        VehicleId uniqueidentifier NULL,
        TrailerId uniqueidentifier NULL,
        PositionSource nvarchar(40) NULL,
        CapacityPallets int NOT NULL,
        PlannedPallets int NOT NULL,
        Score decimal(10,2) NOT NULL,
        ScoreComponentsJson nvarchar(max) NOT NULL,
        ExplanationJson nvarchar(max) NOT NULL,
        CONSTRAINT FK_PlanProposalRuns_PlanProposals_ProposalId FOREIGN KEY(ProposalId) REFERENCES dbo.PlanProposals(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PlanProposalRuns_Drivers_DriverId FOREIGN KEY(DriverId) REFERENCES dbo.Drivers(Id),
        CONSTRAINT FK_PlanProposalRuns_Vehicles_VehicleId FOREIGN KEY(VehicleId) REFERENCES dbo.Vehicles(Id),
        CONSTRAINT FK_PlanProposalRuns_Trailers_TrailerId FOREIGN KEY(TrailerId) REFERENCES dbo.Trailers(Id)
    );
    CREATE UNIQUE INDEX IX_PlanProposalRuns_ProposalId_Sequence ON dbo.PlanProposalRuns(ProposalId, Sequence);
END;

IF OBJECT_ID(N'dbo.PlanProposalCandidates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlanProposalCandidates
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PlanProposalCandidates PRIMARY KEY,
        ProposalRunId uniqueidentifier NOT NULL,
        DriverId uniqueidentifier NOT NULL,
        VehicleId uniqueidentifier NOT NULL,
        Selected bit NOT NULL CONSTRAINT DF_PlanProposalCandidates_Selected DEFAULT(0),
        Classification nvarchar(40) NOT NULL,
        PositionSource nvarchar(40) NOT NULL,
        Score decimal(10,2) NOT NULL,
        ScoreComponentsJson nvarchar(max) NOT NULL,
        ConstraintResultsJson nvarchar(max) NOT NULL,
        ExplanationJson nvarchar(max) NOT NULL,
        CONSTRAINT FK_PlanProposalCandidates_PlanProposalRuns_ProposalRunId FOREIGN KEY(ProposalRunId) REFERENCES dbo.PlanProposalRuns(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PlanProposalCandidates_Drivers_DriverId FOREIGN KEY(DriverId) REFERENCES dbo.Drivers(Id),
        CONSTRAINT FK_PlanProposalCandidates_Vehicles_VehicleId FOREIGN KEY(VehicleId) REFERENCES dbo.Vehicles(Id)
    );
    CREATE UNIQUE INDEX IX_PlanProposalCandidates_Run_Driver_Vehicle ON dbo.PlanProposalCandidates(ProposalRunId, DriverId, VehicleId);
END;

IF OBJECT_ID(N'dbo.PlanProposalAllocations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlanProposalAllocations
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PlanProposalAllocations PRIMARY KEY,
        ProposalRunId uniqueidentifier NOT NULL,
        SourceLineId uniqueidentifier NOT NULL,
        Pallets int NOT NULL,
        PalletType nvarchar(40) NULL,
        CollectionSite nvarchar(200) NULL,
        DeliverySite nvarchar(200) NULL,
        CollectionSequence int NOT NULL,
        DeliverySequence int NOT NULL,
        CONSTRAINT FK_PlanProposalAllocations_PlanProposalRuns_ProposalRunId FOREIGN KEY(ProposalRunId) REFERENCES dbo.PlanProposalRuns(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PlanProposalAllocations_OrderSourceLines_SourceLineId FOREIGN KEY(SourceLineId) REFERENCES dbo.OrderSourceLines(Id)
    );
    CREATE INDEX IX_PlanProposalAllocations_ProposalRunId_SourceLineId ON dbo.PlanProposalAllocations(ProposalRunId, SourceLineId);
END;
