/*
    0900 - Demo seed data (applied only with `Migrator --seed`).

    Idempotent: every reference row is inserted only if its natural key (Name / Sku / Code /
    Email) is missing, and order history is generated only while dbo.Orders is empty. Running
    the script twice (or against a database that already has live orders) changes nothing.

    Deterministic: the same script always produces the same orders, so the reports are
    reproducible. Order history is generated set-based from a tally CTE (no loops / cursors)
    for calendar year 2025. Keys are resolved by natural key, never by assuming IDENTITY values.
*/
-- Pinned explicitly so the result never depends on the client's defaults (sqlcmd without -I runs
-- with QUOTED_IDENTIFIER OFF). Filtered indexes, indexes on computed columns and the modules that
-- write to those tables all require ANSI_NULLS and QUOTED_IDENTIFIER ON.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

SET NOCOUNT ON;

-- Reference data --------------------------------------------------------------------------
INSERT INTO dbo.Categories (Name, ParentCategoryId)
SELECT v.Name, NULL
FROM (VALUES (N'Reality Capture'), (N'Positioning'), (N'Software'), (N'Accessories')) AS v (Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Categories AS c WHERE c.Name = v.Name);

INSERT INTO dbo.Categories (Name, ParentCategoryId)
SELECT v.Name, parent.CategoryId
FROM (VALUES
        (N'Laser Scanners',    N'Reality Capture'),
        (N'Mobile Mapping',    N'Reality Capture'),
        (N'GNSS Receivers',    N'Positioning'),
        (N'Total Stations',    N'Positioning'),
        (N'Cloud Licenses',    N'Software'),
        (N'Desktop Licenses',  N'Software'),
        (N'Tripods & Mounts',  N'Accessories'),
        (N'Batteries & Power', N'Accessories')
     ) AS v (Name, ParentName)
JOIN dbo.Categories AS parent ON parent.Name = v.ParentName
WHERE NOT EXISTS (SELECT 1 FROM dbo.Categories AS c WHERE c.Name = v.Name);

INSERT INTO dbo.Suppliers (Name, ContactEmail, Country)
SELECT v.Name, v.ContactEmail, v.Country
FROM (VALUES
        (N'Northwind Optics',     'orders@northwind-optics.example',     N'Germany'),
        (N'Contoso Geospatial',   'supply@contoso-geo.example',          N'United States'),
        (N'Fabrikam Electronics', 'sales@fabrikam-electronics.example',  N'Sweden'),
        (N'Tailspin Software',    'licensing@tailspin.example',          N'India')
     ) AS v (Name, ContactEmail, Country)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Suppliers AS s WHERE s.Name = v.Name);

INSERT INTO dbo.Warehouses (Code, Name, City, Country)
SELECT v.Code, v.Name, v.City, v.Country
FROM (VALUES
        ('AMS01', N'Amsterdam Distribution Centre', N'Amsterdam', N'Netherlands'),
        ('CHI01', N'Chicago Fulfilment Hub',        N'Chicago',   N'United States'),
        ('PNQ01', N'Pune Regional Warehouse',       N'Pune',      N'India')
     ) AS v (Code, Name, City, Country)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Warehouses AS w WHERE w.Code = v.Code);

-- Catalog ----------------------------------------------------------------------------------
INSERT INTO dbo.Products (Sku, Name, CategoryId, SupplierId, UnitPrice, ReorderLevel, IsActive)
SELECT v.Sku, v.Name, c.CategoryId, s.SupplierId, v.UnitPrice, v.ReorderLevel, v.IsActive
FROM (VALUES
        ('LS-PRO-360',  N'Terrestrial Laser Scanner Pro 360',  N'Laser Scanners',    N'Northwind Optics',     48500.00,  3, 1),
        ('LS-LITE-120', N'Compact Laser Scanner Lite',         N'Laser Scanners',    N'Northwind Optics',     18900.00,  5, 1),
        ('MM-BKP-01',   N'Backpack Mobile Mapping Kit',        N'Mobile Mapping',    N'Contoso Geospatial',   62000.00,  2, 1),
        ('MM-VHC-02',   N'Vehicle Mapping Sensor Rig',         N'Mobile Mapping',    N'Contoso Geospatial',   89000.00,  2, 1),
        ('GN-RTK-500',  N'RTK GNSS Smart Antenna',             N'GNSS Receivers',    N'Contoso Geospatial',    9800.00, 10, 1),
        ('GN-BASE-10',  N'GNSS Base Station',                  N'GNSS Receivers',    N'Contoso Geospatial',   14200.00,  4, 1),
        ('TS-ROBO-1S',  N'Robotic Total Station 1"',           N'Total Stations',    N'Northwind Optics',     32500.00,  4, 1),
        ('TS-MAN-5S',   N'Manual Total Station 5"',            N'Total Stations',    N'Northwind Optics',     11400.00,  6, 1),
        ('SW-CLD-STD',  N'Cloud Point-Cloud Viewer (annual)',  N'Cloud Licenses',    N'Tailspin Software',     1200.00, 25, 1),
        ('SW-CLD-ENT',  N'Cloud Collaboration Suite (annual)', N'Cloud Licenses',    N'Tailspin Software',     4800.00, 15, 1),
        ('SW-DSK-REG',  N'Desktop Registration Software',      N'Desktop Licenses',  N'Tailspin Software',     6500.00, 10, 1),
        ('AC-TRP-CF',   N'Carbon Fibre Survey Tripod',         N'Tripods & Mounts',  N'Fabrikam Electronics',   650.00, 20, 1),
        ('AC-TRB-01',   N'Precision Tribrach Adapter',         N'Tripods & Mounts',  N'Fabrikam Electronics',   240.00, 30, 1),
        ('AC-BAT-LI',   N'Li-ion Smart Battery 7.2V',          N'Batteries & Power', N'Fabrikam Electronics',   189.00, 40, 1),
        ('AC-CHG-QD',   N'Quad Battery Charger',               N'Batteries & Power', N'Fabrikam Electronics',   420.00, 15, 1),
        ('AC-LEG-OLD',  N'Legacy Aluminium Tripod (retired)',  N'Tripods & Mounts',  N'Fabrikam Electronics',   120.00,  5, 0)
     ) AS v (Sku, Name, CategoryName, SupplierName, UnitPrice, ReorderLevel, IsActive)
JOIN dbo.Categories AS c ON c.Name = v.CategoryName
JOIN dbo.Suppliers  AS s ON s.Name = v.SupplierName
WHERE NOT EXISTS (SELECT 1 FROM dbo.Products AS p WHERE p.Sku = v.Sku);

-- Stock: every product in every warehouse, with a deterministic spread of quantities.
-- A handful of (Sku, warehouse) pairs start at/below the reorder level so vw_LowStock
-- has something to report. Existing rows are never touched, so live stock is preserved.
INSERT INTO dbo.Inventory (ProductId, WarehouseId, QuantityOnHand)
SELECT p.ProductId,
       w.WarehouseId,
       COALESCE(low.Qty, p.ReorderLevel + 1 + ((p.ProductId * 37 + w.WarehouseId * 11) % 60))
FROM dbo.Products AS p
CROSS JOIN dbo.Warehouses AS w
LEFT JOIN (VALUES ('LS-PRO-360', 'AMS01', 1),
                  ('MM-VHC-02',  'CHI01', 0),
                  ('GN-RTK-500', 'PNQ01', 6),
                  ('AC-BAT-LI',  'AMS01', 12),
                  ('AC-TRB-01',  'CHI01', 30)) AS low (Sku, Code, Qty)
       ON low.Sku = p.Sku AND low.Code = w.Code
WHERE NOT EXISTS (SELECT 1 FROM dbo.Inventory AS i
                  WHERE i.ProductId = p.ProductId AND i.WarehouseId = w.WarehouseId);

-- Customers --------------------------------------------------------------------------------
INSERT INTO dbo.Customers (Email, FirstName, LastName, City, Country)
SELECT v.Email, v.FirstName, v.LastName, v.City, v.Country
FROM (VALUES
        ('ava.jansen@example.com',     N'Ava',    N'Jansen',   N'Rotterdam', N'Netherlands'),
        ('liam.becker@example.com',    N'Liam',   N'Becker',   N'Munich',    N'Germany'),
        ('noah.smith@example.com',     N'Noah',   N'Smith',    N'Denver',    N'United States'),
        ('emma.garcia@example.com',    N'Emma',   N'Garcia',   N'Austin',    N'United States'),
        ('arjun.mehta@example.com',    N'Arjun',  N'Mehta',    N'Bengaluru', N'India'),
        ('priya.nair@example.com',     N'Priya',  N'Nair',     N'Hyderabad', N'India'),
        ('lucas.martin@example.com',   N'Lucas',  N'Martin',   N'Lyon',      N'France'),
        ('sofia.rossi@example.com',    N'Sofia',  N'Rossi',    N'Milan',     N'Italy'),
        ('oliver.brown@example.com',   N'Oliver', N'Brown',    N'Leeds',     N'United Kingdom'),
        ('mia.kowalski@example.com',   N'Mia',    N'Kowalski', N'Krakow',    N'Poland'),
        ('ethan.lee@example.com',      N'Ethan',  N'Lee',      N'Toronto',   N'Canada'),
        ('isla.wilson@example.com',    N'Isla',   N'Wilson',   N'Auckland',  N'New Zealand'),
        ('rohan.kulkarni@example.com', N'Rohan',  N'Kulkarni', N'Pune',      N'India'),
        ('hannah.fischer@example.com', N'Hannah', N'Fischer',  N'Zurich',    N'Switzerland'),
        ('mateo.lopez@example.com',    N'Mateo',  N'Lopez',    N'Madrid',    N'Spain'),
        ('chloe.dubois@example.com',   N'Chloe',  N'Dubois',   N'Brussels',  N'Belgium'),
        ('james.taylor@example.com',   N'James',  N'Taylor',   N'Chicago',   N'United States'),
        ('aisha.khan@example.com',     N'Aisha',  N'Khan',     N'Dubai',     N'United Arab Emirates'),
        ('erik.nilsson@example.com',   N'Erik',   N'Nilsson',  N'Stockholm', N'Sweden'),
        ('yuki.tanaka@example.com',    N'Yuki',   N'Tanaka',   N'Osaka',     N'Japan')
     ) AS v (Email, FirstName, LastName, City, Country)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Customers AS c WHERE c.Email = v.Email);

-- Order history (calendar year 2025), generated only into an empty Orders table -----------
IF NOT EXISTS (SELECT 1 FROM dbo.Orders)
BEGIN
    DECLARE @Customers  TABLE (Rn INT PRIMARY KEY, CustomerId INT NOT NULL);
    DECLARE @Warehouses TABLE (Rn INT PRIMARY KEY, WarehouseId INT NOT NULL);
    DECLARE @Products   TABLE (Rn INT PRIMARY KEY, ProductId INT NOT NULL, UnitPrice DECIMAL(10,2) NOT NULL, IsHardware BIT NOT NULL);

    INSERT INTO @Customers (Rn, CustomerId)
    SELECT ROW_NUMBER() OVER (ORDER BY CustomerId) - 1, CustomerId FROM dbo.Customers;

    INSERT INTO @Warehouses (Rn, WarehouseId)
    SELECT ROW_NUMBER() OVER (ORDER BY WarehouseId) - 1, WarehouseId FROM dbo.Warehouses WHERE IsActive = 1;

    INSERT INTO @Products (Rn, ProductId, UnitPrice, IsHardware)
    SELECT ROW_NUMBER() OVER (ORDER BY p.ProductId) - 1, p.ProductId, p.UnitPrice,
           CASE WHEN p.UnitPrice >= 5000 THEN 1 ELSE 0 END
    FROM dbo.Products AS p
    WHERE p.IsActive = 1;

    DECLARE @CustomerCount  INT = (SELECT COUNT(*) FROM @Customers);
    DECLARE @WarehouseCount INT = (SELECT COUNT(*) FROM @Warehouses);
    DECLARE @ProductCount   INT = (SELECT COUNT(*) FROM @Products);
    DECLARE @OrderCount     INT = 600;

    -- MERGE (instead of INSERT) so OUTPUT can return the generator's N next to the new OrderId.
    DECLARE @NewOrders TABLE (N INT PRIMARY KEY, OrderId INT NOT NULL UNIQUE);

    WITH Tally AS
    (
        SELECT TOP (@OrderCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
        FROM sys.all_columns AS a
        CROSS JOIN sys.all_columns AS b
    ),
    Generated AS
    (
        SELECT
            t.N,
            c.CustomerId,
            w.WarehouseId,
            -- Spread across 2025 with more volume later in the year (square-root ramp on a
            -- golden-ratio sequence), plus a deterministic time of day.
            DATEADD(MINUTE, (t.N * 7919) % 1440,
                DATEADD(DAY, CAST(364.0 * SQRT((t.N * 0.6180339887) % 1.0) AS INT),
                    CAST('2025-01-01' AS DATETIME2(3)))) AS OrderDateUtc,
            CASE WHEN t.N % 29 = 0 THEN 'Cancelled' ELSE 'Delivered' END AS Status
        FROM Tally AS t
        JOIN @Customers  AS c ON c.Rn = (t.N * 7) % @CustomerCount
        JOIN @Warehouses AS w ON w.Rn = t.N % @WarehouseCount
    )
    MERGE dbo.Orders AS target
    USING Generated AS src
       ON 1 = 0                                   -- never matches: every source row is inserted
    WHEN NOT MATCHED THEN
        INSERT (CustomerId, WarehouseId, OrderDateUtc, Status, TotalAmount)
        VALUES (src.CustomerId, src.WarehouseId, src.OrderDateUtc, src.Status, 0)
    OUTPUT src.N, inserted.OrderId INTO @NewOrders (N, OrderId);

    -- Orders placed in the last weeks of the year have not been delivered yet.
    UPDATE o
    SET    o.Status = CASE WHEN o.OrderDateUtc >= '2025-12-24' THEN 'Placed' ELSE 'Shipped' END
    FROM   dbo.Orders AS o
    JOIN   @NewOrders AS n ON n.OrderId = o.OrderId
    WHERE  o.Status = 'Delivered'
      AND  o.OrderDateUtc >= '2025-12-01';

    -- 1 to 3 lines per order; the offsets 0/5/10 modulo the product count (15) are always
    -- distinct, so UQ_OrderItems_Order_Product can never be violated.
    WITH Lines AS
    (
        SELECT v.LineNumber FROM (VALUES (1), (2), (3)) AS v (LineNumber)
    )
    INSERT INTO dbo.OrderItems (OrderId, ProductId, Quantity, UnitPrice)
    SELECT
        o.OrderId,
        p.ProductId,
        CASE WHEN p.IsHardware = 1 THEN 1 + (o.N + l.LineNumber) % 2       -- expensive kit: 1-2 units
             ELSE 1 + (o.N * 3 + l.LineNumber) % 8 END,                     -- consumables / licences: 1-8
        p.UnitPrice
    FROM @NewOrders AS o
    JOIN Lines      AS l ON l.LineNumber <= 1 + (o.N % 3)
    JOIN @Products  AS p ON p.Rn = (o.N * 13 + (l.LineNumber - 1) * 5) % @ProductCount;

    UPDATE o
    SET    o.TotalAmount = t.Total
    FROM   dbo.Orders AS o
    JOIN  (SELECT oi.OrderId, SUM(oi.LineTotal) AS Total
           FROM dbo.OrderItems AS oi
           JOIN @NewOrders     AS n ON n.OrderId = oi.OrderId
           GROUP BY oi.OrderId) AS t
           ON t.OrderId = o.OrderId;
END;
GO
