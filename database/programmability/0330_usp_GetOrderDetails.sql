/*
    usp_GetOrderDetails - order header + lines in a single round trip (two result sets).
    The API reads both with Dapper's QueryMultipleAsync. Seeks: PK_Orders, IX_OrderItems_OrderId.
*/
CREATE OR ALTER PROCEDURE dbo.usp_GetOrderDetails
    @OrderId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        o.OrderId,
        o.CustomerId,
        CONCAT(c.FirstName, N' ', c.LastName) AS CustomerName,
        o.WarehouseId,
        w.Code AS WarehouseCode,
        o.OrderDateUtc,
        o.Status,
        CAST(o.TotalAmount AS DECIMAL(19,2)) AS TotalAmount
    FROM dbo.Orders     AS o
    JOIN dbo.Customers  AS c ON c.CustomerId  = o.CustomerId
    JOIN dbo.Warehouses AS w ON w.WarehouseId = o.WarehouseId
    WHERE o.OrderId = @OrderId;

    SELECT
        oi.ProductId,
        p.Sku,
        p.Name AS ProductName,
        oi.Quantity,
        oi.UnitPrice,
        CAST(oi.LineTotal AS DECIMAL(19,2)) AS LineTotal
    FROM dbo.OrderItems AS oi
    JOIN dbo.Products   AS p ON p.ProductId = oi.ProductId
    WHERE oi.OrderId = @OrderId
    ORDER BY oi.OrderItemId;
END;
GO
