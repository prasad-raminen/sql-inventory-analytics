/*
    vw_ProductCatalog - denormalized read model for the product endpoints.

    Joins the catalog to its reference data and the total stock across all warehouses.
    OUTER APPLY (instead of LEFT JOIN + GROUP BY over the whole view) keeps the aggregate
    correlated to each product, so a query that filters on ProductId only sums that
    product's Inventory rows via a clustered-index seek on PK_Inventory.
*/
-- Pinned explicitly so the result never depends on the client's defaults (sqlcmd without -I runs
-- with QUOTED_IDENTIFIER OFF). Filtered indexes, indexes on computed columns and the modules that
-- write to those tables all require ANSI_NULLS and QUOTED_IDENTIFIER ON.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER VIEW dbo.vw_ProductCatalog
AS
SELECT
    p.ProductId,
    p.Sku,
    p.Name,
    p.CategoryId,
    c.Name                          AS CategoryName,
    p.SupplierId,
    s.Name                          AS SupplierName,
    p.UnitPrice,
    p.ReorderLevel,
    p.IsActive,
    ISNULL(stock.TotalOnHand, 0)    AS TotalOnHand
FROM dbo.Products   AS p
JOIN dbo.Categories AS c ON c.CategoryId = p.CategoryId
JOIN dbo.Suppliers  AS s ON s.SupplierId = p.SupplierId
OUTER APPLY
(
    SELECT SUM(i.QuantityOnHand) AS TotalOnHand
    FROM dbo.Inventory AS i
    WHERE i.ProductId = p.ProductId
) AS stock;
GO
