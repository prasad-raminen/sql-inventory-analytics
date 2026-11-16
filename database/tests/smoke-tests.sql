/*
    SQL smoke tests - a MANUAL check, run AFTER `Migrator --seed` against a throwaway database
    (for example the SQL Server container from docker-compose.yml). It is not wired into CI.

        sqlcmd -C -I -b -S localhost -U sa -P "$SA_PASSWORD" -d InventoryAnalytics -i database/tests/smoke-tests.sql

    Each check THROWs (error 51000+) on failure, so `sqlcmd -b` exits non-zero.
    The script creates its own product + inventory rows so assertions never depend on what
    earlier runs (or API calls) did to the seeded stock. It is safe to run repeatedly.
*/
SET NOCOUNT ON;

DECLARE @Suffix      VARCHAR(12) = RIGHT(CONVERT(VARCHAR(36), NEWID()), 12);
DECLARE @CustomerId  INT = (SELECT MIN(CustomerId) FROM dbo.Customers);
DECLARE @WarehouseId INT = (SELECT MIN(WarehouseId) FROM dbo.Warehouses WHERE IsActive = 1);
DECLARE @ProductId INT, @OrderId INT, @Qty INT, @OrderCountBefore INT, @ErrorNumber INT;
DECLARE @Lines dbo.OrderLineList;

PRINT 'Seed data present';
IF (SELECT COUNT(*) FROM dbo.Orders) < 100
    THROW 51000, 'Expected seeded orders. Run the migrator with --seed first.', 1;

-- Arrange: a dedicated product with exactly 5 units in one warehouse.
INSERT INTO dbo.Products (Sku, Name, CategoryId, SupplierId, UnitPrice, ReorderLevel)
SELECT 'TEST-' + @Suffix, N'Smoke test product', MIN(CategoryId), (SELECT MIN(SupplierId) FROM dbo.Suppliers), 100.00, 2
FROM dbo.Categories;
SET @ProductId = CAST(SCOPE_IDENTITY() AS INT);
INSERT INTO dbo.Inventory (ProductId, WarehouseId, QuantityOnHand) VALUES (@ProductId, @WarehouseId, 5);

PRINT 'usp_PlaceOrder: happy path decrements stock and records totals';
INSERT INTO @Lines (ProductId, Quantity) VALUES (@ProductId, 2), (@ProductId, 1);   -- duplicate lines get merged
DECLARE @Placed TABLE (OrderId INT, CustomerId INT, WarehouseId INT, OrderDateUtc DATETIME2(3), Status VARCHAR(20), TotalAmount DECIMAL(19,4));
INSERT INTO @Placed EXEC dbo.usp_PlaceOrder @CustomerId = @CustomerId, @WarehouseId = @WarehouseId, @Lines = @Lines;
SELECT @OrderId = OrderId FROM @Placed;

SELECT @Qty = QuantityOnHand FROM dbo.Inventory WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId;
IF @Qty <> 2 THROW 51001, 'Stock was not decremented from 5 to 2.', 1;
IF (SELECT TotalAmount FROM dbo.Orders WHERE OrderId = @OrderId) <> 300.00
    THROW 51002, 'Order total should be 3 x 100.00.', 1;
IF (SELECT COUNT(*) FROM dbo.OrderItems WHERE OrderId = @OrderId) <> 1
    THROW 51003, 'Duplicate lines should be merged into one order item.', 1;

PRINT 'usp_PlaceOrder: insufficient stock throws 50010 and rolls back';
SELECT @OrderCountBefore = COUNT(*) FROM dbo.Orders;
UPDATE @Lines SET Quantity = 3;   -- only 2 units are left
SET @ErrorNumber = NULL;
BEGIN TRY
    EXEC dbo.usp_PlaceOrder @CustomerId = @CustomerId, @WarehouseId = @WarehouseId, @Lines = @Lines;
END TRY
BEGIN CATCH
    SET @ErrorNumber = ERROR_NUMBER();
    PRINT '  caught: ' + ERROR_MESSAGE();
END CATCH;
IF ISNULL(@ErrorNumber, 0) <> 50010 THROW 51004, 'Expected error 50010 for insufficient stock.', 1;
IF @@TRANCOUNT <> 0 THROW 51005, 'Transaction left open after failure.', 1;
IF (SELECT COUNT(*) FROM dbo.Orders) <> @OrderCountBefore THROW 51006, 'A failed order must not be persisted.', 1;
IF (SELECT QuantityOnHand FROM dbo.Inventory WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId) <> 2
    THROW 51007, 'Stock must be unchanged after a failed order.', 1;

PRINT 'usp_PlaceOrder: validation errors';
SET @ErrorNumber = NULL;
BEGIN TRY EXEC dbo.usp_PlaceOrder @CustomerId = -1, @WarehouseId = @WarehouseId, @Lines = @Lines; END TRY
BEGIN CATCH SET @ErrorNumber = ERROR_NUMBER(); END CATCH;
IF ISNULL(@ErrorNumber, 0) <> 50001 THROW 51008, 'Expected 50001 for an unknown customer.', 1;

DECLARE @NoLines dbo.OrderLineList;
SET @ErrorNumber = NULL;
BEGIN TRY EXEC dbo.usp_PlaceOrder @CustomerId = @CustomerId, @WarehouseId = @WarehouseId, @Lines = @NoLines; END TRY
BEGIN CATCH SET @ErrorNumber = ERROR_NUMBER(); END CATCH;
IF ISNULL(@ErrorNumber, 0) <> 50003 THROW 51009, 'Expected 50003 for an empty order.', 1;

DECLARE @UnknownProduct dbo.OrderLineList;
INSERT INTO @UnknownProduct (ProductId, Quantity) VALUES (-42, 1);
SET @ErrorNumber = NULL;
BEGIN TRY EXEC dbo.usp_PlaceOrder @CustomerId = @CustomerId, @WarehouseId = @WarehouseId, @Lines = @UnknownProduct; END TRY
BEGIN CATCH SET @ErrorNumber = ERROR_NUMBER(); END CATCH;
IF ISNULL(@ErrorNumber, 0) <> 50004 THROW 51010, 'Expected 50004 for an unknown product.', 1;

PRINT 'usp_GetTopProducts: ranks are consistent';
DECLARE @Top TABLE (ProductId INT, Sku VARCHAR(32), Name NVARCHAR(200), CategoryId INT, CategoryName NVARCHAR(100),
                    UnitsSold INT, OrderCount INT, Revenue DECIMAL(19,2), RevenueRank BIGINT, UnitsRank BIGINT,
                    RankInCategory BIGINT, RevenueSharePct DECIMAL(5,2));
INSERT INTO @Top EXEC dbo.usp_GetTopProducts @FromUtc = '2025-01-01', @ToUtc = '2026-01-01', @Top = 5;
IF (SELECT COUNT(*) FROM @Top) < 5 THROW 51011, 'Expected at least 5 top products.', 1;
IF (SELECT MIN(RevenueRank) FROM @Top) <> 1 THROW 51012, 'Top product must have rank 1.', 1;
IF EXISTS (SELECT 1 FROM @Top AS a JOIN @Top AS b ON a.RevenueRank < b.RevenueRank AND a.Revenue < b.Revenue)
    THROW 51013, 'RevenueRank is not ordered by revenue.', 1;

PRINT 'usp_SalesByCategory: shares add up to 100%';
DECLARE @ByCategory TABLE (CategoryId INT, CategoryName NVARCHAR(100), UnitsSold INT, OrderCount INT, DistinctProducts INT,
                           Revenue DECIMAL(19,2), RevenuePerOrder DECIMAL(19,2), RevenueSharePct DECIMAL(5,2), CumulativeSharePct DECIMAL(5,2));
INSERT INTO @ByCategory EXEC dbo.usp_SalesByCategory @FromUtc = '2025-01-01', @ToUtc = '2026-01-01', @RollUpToRoot = 1;
IF (SELECT COUNT(*) FROM @ByCategory) <> 4 THROW 51014, 'Expected the 4 root categories.', 1;
IF ABS((SELECT SUM(RevenueSharePct) FROM @ByCategory) - 100) > 0.1 THROW 51015, 'Revenue shares do not sum to 100.', 1;
IF (SELECT MAX(CumulativeSharePct) FROM @ByCategory) < 99.9 THROW 51016, 'Cumulative share must end at 100.', 1;

DECLARE @ByLeafCategory TABLE (CategoryId INT, CategoryName NVARCHAR(100), UnitsSold INT, OrderCount INT, DistinctProducts INT,
                               Revenue DECIMAL(19,2), RevenuePerOrder DECIMAL(19,2), RevenueSharePct DECIMAL(5,2), CumulativeSharePct DECIMAL(5,2));
INSERT INTO @ByLeafCategory EXEC dbo.usp_SalesByCategory @FromUtc = '2025-01-01', @ToUtc = '2026-01-01', @RollUpToRoot = 0;
IF (SELECT COUNT(*) FROM @ByLeafCategory) <= 4 THROW 51017, 'Leaf-level report should have more rows than roots.', 1;

PRINT 'Views and table-valued function';
IF (SELECT COUNT(*) FROM dbo.vw_MonthlySales WHERE [Year] = 2025) <> 12 THROW 51018, 'Expected 12 months of 2025 sales.', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.vw_LowStock) THROW 51019, 'Expected low-stock rows from the seed.', 1;
IF (SELECT COUNT(*) FROM dbo.fn_GetProductStock(@ProductId)) <> (SELECT COUNT(*) FROM dbo.Warehouses WHERE IsActive = 1)
    THROW 51020, 'fn_GetProductStock must return one row per active warehouse.', 1;
IF (SELECT SUM(QuantityOnHand) FROM dbo.fn_GetProductStock(@ProductId)) <> 2 THROW 51021, 'TVF stock mismatch.', 1;

PRINT 'CHECK constraint prevents negative stock';
SET @ErrorNumber = NULL;
BEGIN TRY UPDATE dbo.Inventory SET QuantityOnHand = -1 WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId; END TRY
BEGIN CATCH SET @ErrorNumber = ERROR_NUMBER(); END CATCH;
IF ISNULL(@ErrorNumber, 0) <> 547 THROW 51022, 'Expected a CHECK constraint violation (547).', 1;

PRINT 'ALL SQL SMOKE TESTS PASSED';
