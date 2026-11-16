/*
    usp_GetTopProducts - best-selling products by revenue for a date range.

    Window functions used:
      RANK()        overall revenue rank; ties share a rank and leave a gap (1,2,2,4).
                    We filter on RANK so tied products are never arbitrarily cut off,
                    which means the result can contain slightly more than @Top rows.
      DENSE_RANK()  rank by units sold, no gaps (1,2,2,3) - a second "popularity" view.
      ROW_NUMBER()  position inside the product's category; deterministic thanks to the
                    ProductId tie-breaker, so "#1 in Laser Scanners" is always unique.
      SUM() OVER () share of total revenue without a second pass / subquery.

    @CategoryId is optional. "Catch-all" predicates (@p IS NULL OR col = @p) produce poor
    cached plans, so OPTION (RECOMPILE) builds a plan for the actual parameter values.
    This is an analytics query executed rarely, so compile cost is irrelevant.
*/
CREATE OR ALTER PROCEDURE dbo.usp_GetTopProducts
    @FromUtc    DATETIME2(3),
    @ToUtc      DATETIME2(3),
    @Top        INT = 10,
    @CategoryId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Top IS NULL OR @Top < 1 OR @Top > 100
        THROW 50020, N'@Top must be between 1 and 100.', 1;

    IF @FromUtc IS NULL OR @ToUtc IS NULL OR @FromUtc >= @ToUtc
        THROW 50021, N'@FromUtc must be earlier than @ToUtc.', 1;

    WITH ProductSales AS
    (
        SELECT
            oi.ProductId,
            SUM(oi.Quantity)           AS UnitsSold,
            SUM(oi.LineTotal)          AS Revenue,
            COUNT(DISTINCT oi.OrderId) AS OrderCount
        FROM dbo.Orders     AS o
        JOIN dbo.OrderItems AS oi ON oi.OrderId = o.OrderId
        WHERE o.OrderDateUtc >= @FromUtc      -- half-open range: [From, To)
          AND o.OrderDateUtc <  @ToUtc
          AND o.Status <> 'Cancelled'
        GROUP BY oi.ProductId
    ),
    Ranked AS
    (
        SELECT
            p.ProductId,
            p.Sku,
            p.Name,
            c.CategoryId,
            c.Name AS CategoryName,
            ps.UnitsSold,
            ps.OrderCount,
            CAST(ps.Revenue AS DECIMAL(19,2)) AS Revenue,
            RANK()       OVER (ORDER BY ps.Revenue DESC)                              AS RevenueRank,
            DENSE_RANK() OVER (ORDER BY ps.UnitsSold DESC)                            AS UnitsRank,
            ROW_NUMBER() OVER (PARTITION BY p.CategoryId ORDER BY ps.Revenue DESC, p.ProductId) AS RankInCategory,
            CAST(100.0 * ps.Revenue / NULLIF(SUM(ps.Revenue) OVER (), 0) AS DECIMAL(5,2))   AS RevenueSharePct
        FROM ProductSales   AS ps
        JOIN dbo.Products   AS p ON p.ProductId  = ps.ProductId
        JOIN dbo.Categories AS c ON c.CategoryId = p.CategoryId
        WHERE @CategoryId IS NULL OR p.CategoryId = @CategoryId
    )
    SELECT
        ProductId, Sku, Name, CategoryId, CategoryName,
        UnitsSold, OrderCount, Revenue,
        RevenueRank, UnitsRank, RankInCategory, RevenueSharePct
    FROM Ranked
    WHERE RevenueRank <= @Top
    ORDER BY RevenueRank, ProductId
    OPTION (RECOMPILE);
END;
GO
