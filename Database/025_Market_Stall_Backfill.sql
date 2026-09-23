/*
   Backfill clear market stall/stand identifiers that were historically stored
   inside MarketContacts.Name. The original Name is deliberately preserved so
   source evidence is not destroyed; the portal normalises the display/import
   shape separately. This script is idempotent and only touches blank stall data.
*/
IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NOT NULL
BEGIN
    ;WITH SourceRows AS (
        SELECT
            mc.Id,
            mc.Market,
            mc.Name,
            TrimmedName = LTRIM(RTRIM(mc.Name)),
            UpperName = UPPER(LTRIM(RTRIM(mc.Name)))
        FROM dbo.MarketContacts mc
        WHERE (mc.StandOrLocation IS NULL OR LTRIM(RTRIM(mc.StandOrLocation)) = N'')
          AND UPPER(ISNULL(mc.Market, N'')) <> N'SENDER'
          AND NULLIF(LTRIM(RTRIM(mc.Name)), N'') IS NOT NULL
    ),
    Extracted AS (
        SELECT
            s.Id,
            Candidate = LTRIM(RTRIM(
                CASE
                    WHEN RIGHT(s.TrimmedName, 1) = N')'
                         AND CHARINDEX(N'(', REVERSE(s.TrimmedName)) > 1
                    THEN SUBSTRING(
                        s.TrimmedName,
                        LEN(s.TrimmedName) - CHARINDEX(N'(', REVERSE(s.TrimmedName)) + 2,
                        CHARINDEX(N'(', REVERSE(s.TrimmedName)) - 2
                    )
                    WHEN CHARINDEX(N' STALL ', s.UpperName) > 0
                    THEN SUBSTRING(
                        s.TrimmedName,
                        CHARINDEX(N' STALL ', s.UpperName) + 7,
                        200
                    )
                    WHEN CHARINDEX(N' STAND ', s.UpperName) > 0
                    THEN SUBSTRING(
                        s.TrimmedName,
                        CHARINDEX(N' STAND ', s.UpperName) + 7,
                        200
                    )
                    WHEN CHARINDEX(N' ', REVERSE(s.TrimmedName)) > 0
                    THEN RIGHT(s.TrimmedName, CHARINDEX(N' ', REVERSE(s.TrimmedName)) - 1)
                    ELSE N''
                END
            ))
        FROM SourceRows s
    ),
    Validated AS (
        SELECT
            e.Id,
            e.Candidate,
            IsValid = CASE
                WHEN NULLIF(e.Candidate, N'') IS NULL THEN 0
                WHEN UPPER(e.Candidate) LIKE N'STALL [A-Z0-9]%'
                  OR UPPER(e.Candidate) LIKE N'STAND [A-Z0-9]%'
                  OR UPPER(e.Candidate) LIKE N'UNIT [A-Z0-9]%'
                  OR UPPER(e.Candidate) LIKE N'BLOCK [A-Z0-9]%'
                  OR UPPER(e.Candidate) LIKE N'RAIL ARCH [A-Z0-9]%'
                THEN 1
                WHEN LEN(e.Candidate) BETWEEN 1 AND 3
                     AND TRY_CONVERT(int, e.Candidate) IS NOT NULL
                THEN 1
                WHEN LEN(e.Candidate) BETWEEN 2 AND 4
                     AND RIGHT(e.Candidate, 1) LIKE N'[A-Za-z]'
                     AND TRY_CONVERT(int, LEFT(e.Candidate, LEN(e.Candidate) - 1)) IS NOT NULL
                THEN 1
                WHEN LEN(e.Candidate) BETWEEN 2 AND 4
                     AND LEFT(e.Candidate, 1) LIKE N'[A-Za-z]'
                     AND TRY_CONVERT(int, SUBSTRING(e.Candidate, 2, 3)) IS NOT NULL
                THEN 1
                WHEN e.Candidate LIKE N'%[0-9]%[-/]%[0-9]%'
                THEN 1
                ELSE 0
            END
        FROM Extracted e
    )
    UPDATE mc
       SET mc.StandOrLocation = v.Candidate
    FROM dbo.MarketContacts mc
    INNER JOIN Validated v ON v.Id = mc.Id
    WHERE v.IsValid = 1
      AND (mc.StandOrLocation IS NULL OR LTRIM(RTRIM(mc.StandOrLocation)) = N'');
END
