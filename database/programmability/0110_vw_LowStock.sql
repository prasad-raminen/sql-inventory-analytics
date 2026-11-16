/*
    vw_LowStock - every active (product, warehouse) pair at or below its reorder level.

    SuggestedReorderQty uses a simple "order up to 2 x ReorderLevel" policy so the buyer
    gets an actionable number, and the supplier columns tell them who to call.
*/
CREATE OR ALTER VIEW dbo.vw_LowStock
AS
SELECT
    p.ProductId,
    p.Sku,
    p.Name                                    AS ProductName,
    c.Name                                    AS CategoryName,
    w.WarehouseId,
    w.Code                                    AS WarehouseCode,
    w.Name                                    AS WarehouseName,
    i.QuantityOnHand,
    p.ReorderLevel,
    (2 * p.ReorderLevel) - i.QuantityOnHand   AS SuggestedReorderQty,
    s.SupplierId,
    s.Name                                    AS SupplierName,
    s.ContactEmail                            AS SupplierEmail
FROM dbo.Inventory  AS i
JOIN dbo.Products   AS p ON p.ProductId   = i.ProductId
JOIN dbo.Warehouses AS w ON w.WarehouseId = i.WarehouseId
JOIN dbo.Categories AS c ON c.CategoryId  = p.CategoryId
JOIN dbo.Suppliers  AS s ON s.SupplierId  = p.SupplierId
WHERE p.IsActive = 1
  AND w.IsActive = 1
  AND i.QuantityOnHand <= p.ReorderLevel;
GO
