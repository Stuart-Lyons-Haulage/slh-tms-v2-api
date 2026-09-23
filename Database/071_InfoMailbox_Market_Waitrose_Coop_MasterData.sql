/*
   Info mailbox intake recovery for Market / Waitrose / CO-OP orders.

   Purpose:
   - SQL remains the authoritative source for customer, site and email-route master data.
   - The intake API must never rely on hard-coded Outlook assumptions only.
   - These seeds are idempotent and safe to rerun.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'BARFOOTS')
        INSERT dbo.Customers (Id, Code, Name, Active) VALUES (NEWID(), N'BARFOOTS', N'Barfoots', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'WAITROSE')
        INSERT dbo.Customers (Id, Code, Name, Active) VALUES (NEWID(), N'WAITROSE', N'Waitrose', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'COOP')
        INSERT dbo.Customers (Id, Code, Name, Active) VALUES (NEWID(), N'COOP', N'CO-OP', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'LANGMEADS')
        INSERT dbo.Customers (Id, Code, Name, Active) VALUES (NEWID(), N'LANGMEADS', N'Langmead Herbs', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'SUMMERBERRY')
        INSERT dbo.Customers (Id, Code, Name, Active) VALUES (NEWID(), N'SUMMERBERRY', N'The Summer Berry Company', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'WEALMOOR')
        INSERT dbo.Customers (Id, Code, Name, Active) VALUES (NEWID(), N'WEALMOOR', N'Wealmoor', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE Code = N'PRIMAFRUIT')
        INSERT dbo.Customers (Id, Code, Name, Active) VALUES (NEWID(), N'PRIMAFRUIT', N'Primafruit / Hall Hunter', 1);
END;

IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'MARKET-COVENT')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'MARKET-COVENT', N'BARFOOTS', N'New Covent Garden Market', N'Covent Garden Market', N'Markets', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'MARKET-SPITALFIELDS')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'MARKET-SPITALFIELDS', N'BARFOOTS', N'Spitalfields Market', N'SPIT / Spitalfields', N'Markets', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'MARKET-WESTERN')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'MARKET-WESTERN', N'BARFOOTS', N'Western International Market', N'Western Market', N'Markets', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'WAITROSE-AYLESFORD')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'WAITROSE-AYLESFORD', N'WAITROSE', N'Waitrose Aylesford', N'Aylesford', N'Waitrose', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'WAITROSE-BRACKNELL')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'WAITROSE-BRACKNELL', N'WAITROSE', N'Waitrose Bracknell', N'Bracknell', N'Waitrose', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'WAITROSE-BRINKLOW')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'WAITROSE-BRINKLOW', N'WAITROSE', N'Waitrose Brinklow', N'Brinklow', N'Waitrose', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'WAITROSE-LEYLAND')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'WAITROSE-LEYLAND', N'WAITROSE', N'Waitrose Leyland', N'Leyland', N'Waitrose', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'BARFOOTS-SEFTER')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'BARFOOTS-SEFTER', N'BARFOOTS', N'Barfoots Sefter', N'Sefter', N'Barfoots', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'BARFOOTS-LEYTHORNE')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'BARFOOTS-LEYTHORNE', N'BARFOOTS', N'Barfoots Leythorne', N'Leythorne', N'Barfoots', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'HAM-FARM')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'HAM-FARM', N'LANGMEADS', N'Ham Farm', N'Ham Farm', N'Langmead', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'HALL-HUNTER')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'HALL-HUNTER', N'PRIMAFRUIT', N'Hall Hunter', N'Hall Hunter', N'Primafruit', 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sites WHERE ExternalCode = N'SUMMERBERRY-COLWORTH')
        INSERT dbo.Sites (Id, ExternalCode, CustomerCode, Name, DriverTextName, OperationalRegion, Active)
        VALUES (NEWID(), N'SUMMERBERRY-COLWORTH', N'SUMMERBERRY', N'Summer Berry Colworth', N'Colworth', N'Summer Berry', 1);
END;

IF OBJECT_ID(N'dbo.CustomerEmailRoutes', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active = 1 AND SenderDomain = N'barfoots.co.uk' AND SubjectContains = N'Wholesale Market')
        INSERT dbo.CustomerEmailRoutes (Id, CustomerCode, SenderDomain, SubjectContains, ParserType, MarketKey, RequiresReview, Active)
        VALUES (NEWID(), N'BARFOOTS', N'barfoots.co.uk', N'Wholesale Market', N'Market', N'Wholesale Market', 0, 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active = 1 AND SenderEmail = N'mariela.popova@barfoots.co.uk' AND SubjectContains = N'COOP')
        INSERT dbo.CustomerEmailRoutes (Id, CustomerCode, SenderEmail, SenderDomain, SubjectContains, ParserType, RequiresReview, Active)
        VALUES (NEWID(), N'BARFOOTS', N'mariela.popova@barfoots.co.uk', N'barfoots.co.uk', N'COOP', N'COOP', 0, 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active = 1 AND SenderEmail = N'agnieszka.zawislan@barfoots.co.uk' AND SubjectContains = N'Waitrose')
        INSERT dbo.CustomerEmailRoutes (Id, CustomerCode, SenderEmail, SenderDomain, SubjectContains, ParserType, DefaultSiteCode, RequiresReview, Active)
        VALUES (NEWID(), N'BARFOOTS', N'agnieszka.zawislan@barfoots.co.uk', N'barfoots.co.uk', N'Waitrose', N'Waitrose', N'BARFOOTS-SEFTER', 0, 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active = 1 AND SenderDomain = N'wealmoor.co.uk' AND SubjectContains = N'WAITROSE PALLET ESTIMATE')
        INSERT dbo.CustomerEmailRoutes (Id, CustomerCode, SenderDomain, SubjectContains, ParserType, RequiresReview, Active)
        VALUES (NEWID(), N'WEALMOOR', N'wealmoor.co.uk', N'WAITROSE PALLET ESTIMATE', N'Waitrose', 0, 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active = 1 AND SenderEmail = N'chris.benning@primafruit.co.uk' AND SubjectContains = N'HHP WAITROSE')
        INSERT dbo.CustomerEmailRoutes (Id, CustomerCode, SenderEmail, SenderDomain, SubjectContains, ParserType, DefaultSiteCode, DefaultDeliverySiteCode, RequiresReview, Active)
        VALUES (NEWID(), N'PRIMAFRUIT', N'chris.benning@primafruit.co.uk', N'primafruit.co.uk', N'HHP WAITROSE', N'Hall Hunter Waitrose', N'HALL-HUNTER', N'WAITROSE-LEYLAND', 0, 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active = 1 AND SenderDomain = N'summerberry.co.uk' AND SubjectContains = N'TSBC  COOP')
        INSERT dbo.CustomerEmailRoutes (Id, CustomerCode, SenderDomain, SubjectContains, ParserType, DefaultSiteCode, RequiresReview, Active)
        VALUES (NEWID(), N'SUMMERBERRY', N'summerberry.co.uk', N'TSBC  COOP', N'COOP', N'SUMMERBERRY-COLWORTH', 0, 1);

    IF NOT EXISTS (SELECT 1 FROM dbo.CustomerEmailRoutes WHERE Active = 1 AND SenderEmail = N'bartosztopolewski@langmeadherbs.co.uk' AND SubjectContains = N'CO-OP')
        INSERT dbo.CustomerEmailRoutes (Id, CustomerCode, SenderEmail, SenderDomain, SubjectContains, ParserType, DefaultSiteCode, RequiresReview, Active)
        VALUES (NEWID(), N'LANGMEADS', N'bartosztopolewski@langmeadherbs.co.uk', N'langmeadherbs.co.uk', N'CO-OP', N'COOP', N'HAM-FARM', 0, 1);
END;

IF OBJECT_ID(N'dbo.OrderIntakeRouteRules', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active = 1 AND CustomerCode = N'BARFOOTS' AND RetailerCode = N'MARKET' AND DestinationSiteCode = N'MARKET-COVENT')
        INSERT dbo.OrderIntakeRouteRules (CustomerCode, RetailerCode, DestinationSiteCode, DestinationName, Priority, ConfidenceScore, Notes)
        VALUES (N'BARFOOTS', N'MARKET', N'MARKET-COVENT', N'New Covent Garden Market', 5, 95, N'Market intake: Covent / New Covent Garden / Wholesale Market.');

    IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active = 1 AND CustomerCode = N'BARFOOTS' AND RetailerCode = N'MARKET' AND DestinationSiteCode = N'MARKET-SPITALFIELDS')
        INSERT dbo.OrderIntakeRouteRules (CustomerCode, RetailerCode, DestinationSiteCode, DestinationName, Priority, ConfidenceScore, Notes)
        VALUES (N'BARFOOTS', N'MARKET', N'MARKET-SPITALFIELDS', N'Spitalfields Market', 5, 95, N'Market intake: Spitalfields / SPIT.');

    IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active = 1 AND CustomerCode = N'BARFOOTS' AND RetailerCode = N'MARKET' AND DestinationSiteCode = N'MARKET-WESTERN')
        INSERT dbo.OrderIntakeRouteRules (CustomerCode, RetailerCode, DestinationSiteCode, DestinationName, Priority, ConfidenceScore, Notes)
        VALUES (N'BARFOOTS', N'MARKET', N'MARKET-WESTERN', N'Western International Market', 5, 95, N'Market intake: Western.');

    IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active = 1 AND CustomerCode = N'BARFOOTS' AND OriginSiteCode = N'BARFOOTS-SEFTER' AND RetailerCode = N'WAITROSE' AND DestinationSiteCode = N'WAITROSE-LEYLAND')
        INSERT dbo.OrderIntakeRouteRules (CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationSiteCode, DestinationName, Priority, ConfidenceScore, Notes)
        VALUES (N'BARFOOTS', N'BARFOOTS-SEFTER', N'Barfoots Sefter', N'WAITROSE', N'WAITROSE-LEYLAND', N'Waitrose Leyland', 5, 95, N'Waitrose Sefter/Leythorne depot mapping. Wave 3 remains PM/overnight candidate.');

    IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active = 1 AND CustomerCode = N'PRIMAFRUIT' AND OriginSiteCode = N'HALL-HUNTER' AND RetailerCode = N'WAITROSE' AND DestinationSiteCode = N'WAITROSE-LEYLAND')
        INSERT dbo.OrderIntakeRouteRules (CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationSiteCode, DestinationName, Priority, ConfidenceScore, Notes)
        VALUES (N'PRIMAFRUIT', N'HALL-HUNTER', N'Hall Hunter', N'WAITROSE', N'WAITROSE-LEYLAND', N'Waitrose Leyland', 5, 95, N'HHP Waitrose direct depot mapping. Collection day before delivery is a PM/overnight candidate.');

    IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active = 1 AND CustomerCode = N'SUMMERBERRY' AND OriginSiteCode = N'SUMMERBERRY-COLWORTH' AND RetailerCode = N'COOP')
        INSERT dbo.OrderIntakeRouteRules (CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationName, Priority, ConfidenceScore, Notes)
        VALUES (N'SUMMERBERRY', N'SUMMERBERRY-COLWORTH', N'Summer Berry Colworth', N'COOP', N'CO-OP', 5, 95, N'TSBC CO-OP intake mapping.');

    IF NOT EXISTS (SELECT 1 FROM dbo.OrderIntakeRouteRules WHERE Active = 1 AND CustomerCode = N'LANGMEADS' AND OriginSiteCode = N'HAM-FARM' AND RetailerCode = N'COOP')
        INSERT dbo.OrderIntakeRouteRules (CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationName, Priority, ConfidenceScore, Notes)
        VALUES (N'LANGMEADS', N'HAM-FARM', N'Ham Farm', N'COOP', N'CO-OP', 5, 95, N'Ham Farm to CO-OP intake mapping.');
END;

COMMIT TRANSACTION;
