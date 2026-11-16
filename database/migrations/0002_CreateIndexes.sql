/*
    0002 - Nonclustered indexes

    Rule of thumb used here: every index must serve a named query. Indexes cost write
    throughput (usp_PlaceOrder touches Inventory, Orders and OrderItems) and storage,
    so we only add ones that turn a scan into a seek for a real access path.
    See docs/queries.md for the plan-level reasoning.
*/
-- Pinned explicitly so the result never depends on the client's defaults (sqlcmd without -I runs
-- with QUOTED_IDENTIFIER OFF). Filtered indexes, indexes on computed columns and the modules that
-- write to those tables all require ANSI_NULLS and QUOTED_IDENTIFIER ON.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


-- SQL Server does NOT index foreign keys automatically. Without these, every join from
-- Products to its parents and every DELETE/UPDATE on a parent key scans dbo.Products.
CREATE NONCLUSTERED INDEX IX_Products_CategoryId ON dbo.Products (CategoryId)
    INCLUDE (Name, Sku, UnitPrice, IsActive);   -- covers catalog browsing by category
GO
CREATE NONCLUSTERED INDEX IX_Products_SupplierId ON dbo.Products (SupplierId);
GO

-- Self-referencing FK used by the recursive category roll-up in usp_SalesByCategory.
CREATE NONCLUSTERED INDEX IX_Categories_ParentCategoryId ON dbo.Categories (ParentCategoryId);
GO

-- The Inventory PK is (ProductId, WarehouseId), which is perfect for "stock of product X",
-- but useless for "everything in warehouse Y" (vw_LowStock filtered by warehouse).
-- Leading on WarehouseId and INCLUDE-ing QuantityOnHand makes that query a covering seek.
CREATE NONCLUSTERED INDEX IX_Inventory_WarehouseId ON dbo.Inventory (WarehouseId)
    INCLUDE (QuantityOnHand);
GO

-- Customer order history ("my orders, newest first"): equality on CustomerId then range/sort
-- on OrderDateUtc. The composite key returns rows already sorted, so no Sort operator.
CREATE NONCLUSTERED INDEX IX_Orders_CustomerId_OrderDateUtc ON dbo.Orders (CustomerId, OrderDateUtc DESC)
    INCLUDE (Status, TotalAmount);
GO

-- Every analytics query filters orders by a date range and excludes cancelled ones.
-- Keyed on OrderDateUtc (range seek) and covering Status/TotalAmount/CustomerId so
-- vw_MonthlySales never has to look up the clustered index (no Key Lookup).
CREATE NONCLUSTERED INDEX IX_Orders_OrderDateUtc ON dbo.Orders (OrderDateUtc)
    INCLUDE (Status, TotalAmount, CustomerId, WarehouseId);
GO

-- Order -> lines join (order details, monthly units sold). Covering the quantities and the
-- persisted LineTotal lets aggregates run entirely from this index.
CREATE NONCLUSTERED INDEX IX_OrderItems_OrderId ON dbo.OrderItems (OrderId)
    INCLUDE (ProductId, Quantity, UnitPrice, LineTotal);
GO

-- Product-centric analytics (usp_GetTopProducts, usp_SalesByCategory) aggregate lines per
-- product. Leading on ProductId gives the optimizer pre-grouped input for a Stream Aggregate.
CREATE NONCLUSTERED INDEX IX_OrderItems_ProductId ON dbo.OrderItems (ProductId)
    INCLUDE (OrderId, Quantity, LineTotal);
GO

-- Filtered index: only active products are ever sold or browsed, so a small index over
-- the active subset is cheaper to scan/maintain than one over the whole table.
CREATE NONCLUSTERED INDEX IX_Products_Active_Name ON dbo.Products (Name)
    INCLUDE (Sku, CategoryId, UnitPrice)
    WHERE IsActive = 1;
GO
