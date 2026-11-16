/*
    fn_GetProductStock - inline table-valued function: stock of one product in every active warehouse.

    Inline (single RETURN SELECT) rather than multi-statement on purpose: the optimizer expands
    an inline TVF into the calling query like a parameterized view, so it gets real cardinality
    estimates and can seek PK_Inventory. Multi-statement TVFs are opaque to the optimizer.

    Warehouses with no Inventory row are returned with 0 on hand (LEFT JOIN), which is what a
    "where can I ship this from?" screen needs.
*/
CREATE OR ALTER FUNCTION dbo.fn_GetProductStock (@ProductId INT)
RETURNS TABLE
AS
RETURN
    SELECT
        w.WarehouseId,
        w.Code                              AS WarehouseCode,
        w.Name                              AS WarehouseName,
        w.City,
        ISNULL(i.QuantityOnHand, 0)         AS QuantityOnHand,
        CAST(CASE WHEN ISNULL(i.QuantityOnHand, 0) <= p.ReorderLevel THEN 1 ELSE 0 END AS BIT) AS IsLowStock
    FROM dbo.Products AS p
    CROSS JOIN dbo.Warehouses AS w
    LEFT JOIN dbo.Inventory AS i
           ON i.ProductId   = p.ProductId
          AND i.WarehouseId = w.WarehouseId
    WHERE p.ProductId = @ProductId
      AND w.IsActive  = 1;
GO
