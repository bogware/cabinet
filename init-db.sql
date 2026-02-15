-- Cabinet PostgreSQL initialization script
-- Runs automatically on first container start via docker-entrypoint-initdb.d
-- The main schema (log_events table, indexes) is created by PostgresLogStorage.InitializeAsync()
-- This file handles pre-requisites that benefit from running at database creation time.

-- Enable extensions useful for Cabinet queries
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Grant full permissions on the cabinet database to the cabinet user
-- (the postgres entrypoint already creates the DB and user from env vars,
-- but grants may not cover future tables created by the app)
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO cabinet;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO cabinet;
