/*
  Provision the governed user-assigned identity used by production Container Apps jobs.
  Idempotent and deliberately limited to runtime DML/execute rights; no DDL/admin role.
*/
SET XACT_ABORT ON;

DECLARE @JobUser sysname = N'slh-tms-jobs-prod-id';
DECLARE @JobObjectId uniqueidentifier = '75197000-83d6-47e5-b797-7532db12c508';

IF DATABASE_PRINCIPAL_ID(@JobUser) IS NULL
BEGIN
    DECLARE @SidHex nvarchar(34) = CONVERT(nvarchar(34), CONVERT(varbinary(16), @JobObjectId), 1);
    EXEC(N'CREATE USER [' + @JobUser + N'] WITH SID = ' + @SidHex + N', TYPE = E;');
END;

IF IS_ROLEMEMBER(N'db_datareader', @JobUser) <> 1
    EXEC(N'ALTER ROLE [db_datareader] ADD MEMBER [' + @JobUser + N'];');

IF IS_ROLEMEMBER(N'db_datawriter', @JobUser) <> 1
    EXEC(N'ALTER ROLE [db_datawriter] ADD MEMBER [' + @JobUser + N'];');

EXEC(N'GRANT EXECUTE TO [' + @JobUser + N'];');
