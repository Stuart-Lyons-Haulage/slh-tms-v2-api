-- ============================================================================
-- MSSQL to PostgreSQL Migration Script
-- ============================================================================
-- This script creates PostgreSQL schemas and tables equivalent to your
-- MSSQL database. Run this FIRST on your PostgreSQL instance.
--
-- Connection String for PostgreSQL:
-- Server=localhost;Port=5432;Database=slh_tms_v2;User Id=slh_user;Password=YourPassword;
-- ============================================================================

-- Create schemas
CREATE SCHEMA IF NOT EXISTS master;
CREATE SCHEMA IF NOT EXISTS intake;
CREATE SCHEMA IF NOT EXISTS ops;
CREATE SCHEMA IF NOT EXISTS live;
CREATE SCHEMA IF NOT EXISTS integration;

-- Set search path
SET search_path TO public, master, intake, ops, live, integration;

-- ============================================================================
-- Master Schema Tables
-- ============================================================================

CREATE TABLE IF NOT EXISTS master.Customers (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    Code VARCHAR(50) NOT NULL UNIQUE,
    Name VARCHAR(255),
    Active BOOLEAN DEFAULT TRUE,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS master.Sites (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    CustomerCode VARCHAR(50) NOT NULL REFERENCES master.Customers(Code),
    SiteCode VARCHAR(50) NOT NULL,
    SiteName VARCHAR(255),
    Location VARCHAR(255),
    Active BOOLEAN DEFAULT TRUE,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(CustomerCode, SiteCode)
);

CREATE TABLE IF NOT EXISTS master.Drivers (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    TachoMasterDriverId VARCHAR(50) UNIQUE,
    FullName VARCHAR(255) NOT NULL,
    EmployeeNumber VARCHAR(50),
    Active BOOLEAN DEFAULT TRUE,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS master.Vehicles (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    Registration VARCHAR(20) NOT NULL UNIQUE,
    NormalizedRegistration VARCHAR(20) GENERATED ALWAYS AS (UPPER(REPLACE(REPLACE(Registration, ' ', ''), '-', ''))) STORED,
    FleetioId VARCHAR(50) UNIQUE,
    VehicleType VARCHAR(50),
    Capacity INT,
    Active BOOLEAN DEFAULT TRUE,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS master.SiteGeofences (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    SiteId UUID REFERENCES master.Sites(Id),
    GeofenceName VARCHAR(255),
    GeofencePolygon JSONB,
    Active BOOLEAN DEFAULT TRUE,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================================
-- Intake Schema Tables
-- ============================================================================

CREATE TABLE IF NOT EXISTS intake.StagedImports (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    SourceType VARCHAR(50), -- 'EMAIL', 'API', 'FILE'
    SourceData JSONB,
    Status VARCHAR(50) DEFAULT 'PendingReview', -- PendingReview, Approved, Rejected
    ReviewedByUserId UUID,
    ReviewedAtUtc TIMESTAMPTZ,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS intake.StagedImportEvents (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    StagedImportId UUID NOT NULL REFERENCES intake.StagedImports(Id),
    EventType VARCHAR(100),
    EventData JSONB,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================================
-- Operations Schema Tables
-- ============================================================================

CREATE TABLE IF NOT EXISTS ops.TransportOrders (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    OrderReference VARCHAR(100) NOT NULL UNIQUE,
    CustomerCode VARCHAR(50) NOT NULL REFERENCES master.Customers(Code),
    OrderStatus VARCHAR(50) DEFAULT 'Pending', -- Pending, Confirmed, InTransit, Delivered
    OriginSiteId UUID REFERENCES master.Sites(Id),
    DestinationSiteId UUID REFERENCES master.Sites(Id),
    PickupDateTimeUtc TIMESTAMPTZ,
    DeliveryDateTimeUtc TIMESTAMPTZ,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS ops.OrderMovements (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    OrderId UUID NOT NULL REFERENCES ops.TransportOrders(Id),
    SequenceNumber INT,
    SiteId UUID REFERENCES master.Sites(Id),
    MovementType VARCHAR(50), -- Collection, Delivery, Transhipment
    PlannedDateTimeUtc TIMESTAMPTZ,
    ActualDateTimeUtc TIMESTAMPTZ,
    Status VARCHAR(50),
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UpdatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================================
-- Live Tracking Schema Tables
-- ============================================================================

CREATE TABLE IF NOT EXISTS live.VehicleTrackingEvents (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    VehicleId UUID NOT NULL REFERENCES master.Vehicles(Id),
    Latitude DECIMAL(10, 8),
    Longitude DECIMAL(11, 8),
    Speed INT,
    Heading INT,
    EventTimeUtc TIMESTAMPTZ NOT NULL,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_vehicle_tracking_vehicle_time 
    ON live.VehicleTrackingEvents(VehicleId, EventTimeUtc DESC);

CREATE TABLE IF NOT EXISTS live.GeofenceVisits (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    VehicleId UUID NOT NULL REFERENCES master.Vehicles(Id),
    GeofenceId UUID NOT NULL REFERENCES master.SiteGeofences(Id),
    EnteredAtUtc TIMESTAMPTZ,
    ExitedAtUtc TIMESTAMPTZ,
    DurationMinutes INT,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS live.DriverStatusLogs (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    DriverId UUID NOT NULL REFERENCES master.Drivers(Id),
    Status VARCHAR(50), -- Available, OnDuty, OffDuty, Resting
    CapturedAtUtc TIMESTAMPTZ NOT NULL,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================================
-- Integration Schema Tables
-- ============================================================================

CREATE TABLE IF NOT EXISTS integration.IntegrationSyncLog (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    IntegrationType VARCHAR(100), -- TachoMaster, Fleetio, SageHR, RoadTech
    SyncStartUtc TIMESTAMPTZ,
    SyncEndUtc TIMESTAMPTZ,
    RecordsProcessed INT,
    RecordsCreated INT,
    RecordsUpdated INT,
    Status VARCHAR(50), -- Success, Failed, PartialSuccess
    ErrorMessage TEXT,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS integration.AuditOutbox (
    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    AggregateType VARCHAR(100),
    AggregateId UUID,
    EventType VARCHAR(100),
    Payload JSONB,
    Status VARCHAR(50) DEFAULT 'Pending', -- Pending, Processed
    ProcessedAtUtc TIMESTAMPTZ,
    RetryCount INT DEFAULT 0,
    CreatedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================================
-- Schema Migration Tracking
-- ============================================================================

CREATE TABLE IF NOT EXISTS public.SchemaMigration (
    Version INT PRIMARY KEY,
    Description VARCHAR(255),
    ExecutedAtUtc TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- Record this migration
INSERT INTO public.SchemaMigration (Version, Description) 
VALUES (1, 'Initial PostgreSQL schema from MSSQL migration')
ON CONFLICT (Version) DO NOTHING;

-- ============================================================================
-- Create default user and roles
-- ============================================================================

-- Create application user (if not exists)
DO
$$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'slh_app') THEN
        CREATE ROLE slh_app WITH LOGIN PASSWORD 'ChangeMe123!SecurePassword';
    END IF;
END
$$;

-- Grant permissions
GRANT CONNECT ON DATABASE slh_tms_v2 TO slh_app;
GRANT USAGE ON SCHEMA master, intake, ops, live, integration TO slh_app;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA master, intake, ops, live, integration TO slh_app;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA master, intake, ops, live, integration TO slh_app;

-- ============================================================================
-- Seed Data (Example)
-- ============================================================================

-- Insert example customer
INSERT INTO master.Customers (Code, Name, Active)
VALUES ('NWF', 'North West Foods', TRUE)
ON CONFLICT (Code) DO NOTHING;

-- Insert example sites
INSERT INTO master.Sites (CustomerCode, SiteCode, SiteName, Location, Active)
VALUES 
    ('NWF', 'MERSTON', 'Merston Distribution Centre', 'Merston, UK', TRUE),
    ('NWF', 'SELSEY', 'Selsey Warehouse', 'Selsey, UK', TRUE)
ON CONFLICT (CustomerCode, SiteCode) DO NOTHING;

COMMIT;
