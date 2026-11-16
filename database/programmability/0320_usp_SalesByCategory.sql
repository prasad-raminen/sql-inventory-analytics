/*
    usp_SalesByCategory - revenue per category with share and Pareto (cumulative) analysis.

    CTE pipeline:
      CategoryTree  recursive CTE: maps every category to its top-level ancestor, so sales
                    of "Laser Scanners" roll up into "Reality Capture" when @RollUpToRoot = 1.
      SalesLines    order lines in the date range (non-cancelled), already tagged with the
                    reporting category (leaf or root) so the next step groups on plain columns.
      Grouped       aggregate per reporting category.
    Final SELECT adds window aggregates:
      RevenueSharePct      = revenue / total revenue
      CumulativeSharePct   = running total ordered by revenue DESC (the "80/20" curve)

    MAXRECURSION guards against a cyclic hierarchy (CK_Categories_NotOwnParent only blocks
    a self-loop; a longer cycle would otherwise recurse up to the default limit of 100).
*/
-- Pinned explicitly so the result never depends on the client's defaults (sqlcmd without -I runs
-- with QUOTED_IDENTIFIER OFF). Filtered indexes, indexes on computed columns and the modules that
-- write to those tables all require ANSI_NULLS and QUOTED_IDENTIFIER ON.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.usp_SalesByCategory
    @FromUtc      DATETIME2(3),
    @ToUtc        DATETIME2(3),
    @RollUpToRoot BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    IF @FromUtc IS NULL OR @ToUtc IS NULL OR @FromUtc >= @ToUtc
        THROW 50021, N'@FromUtc must be earlier than @ToUtc.', 1;

    WITH CategoryTree AS
    (
        SELECT c.CategoryId, c.Name, c.CategoryId AS RootCategoryId, c.Name AS RootName, 0 AS Depth
        FROM dbo.Categories AS c
        WHERE c.ParentCategoryId IS NULL

        UNION ALL

        SELECT child.CategoryId, child.Name, parent.RootCategoryId, parent.RootName, parent.Depth + 1
        FROM dbo.Categories AS child
        JOIN CategoryTree   AS parent ON child.ParentCategoryId = parent.CategoryId
    ),
    SalesLines AS
    (
        SELECT
            CASE WHEN @RollUpToRoot = 1 THEN ct.RootCategoryId ELSE ct.CategoryId END AS ReportCategoryId,
            CASE WHEN @RollUpToRoot = 1 THEN ct.RootName       ELSE ct.Name       END AS ReportCategoryName,
            oi.OrderId,
            oi.ProductId,
            oi.Quantity,
            oi.LineTotal
        FROM dbo.Orders     AS o
        JOIN dbo.OrderItems AS oi ON oi.OrderId    = o.OrderId
        JOIN dbo.Products   AS p  ON p.ProductId   = oi.ProductId
        JOIN CategoryTree   AS ct ON ct.CategoryId = p.CategoryId
        WHERE o.OrderDateUtc >= @FromUtc          -- half-open range: [From, To)
          AND o.OrderDateUtc <  @ToUtc
          AND o.Status <> 'Cancelled'
    ),
    Grouped AS
    (
        SELECT
            sl.ReportCategoryId           AS CategoryId,
            sl.ReportCategoryName         AS CategoryName,
            SUM(sl.Quantity)              AS UnitsSold,
            SUM(sl.LineTotal)             AS Revenue,
            COUNT(DISTINCT sl.OrderId)    AS OrderCount,
            COUNT(DISTINCT sl.ProductId)  AS DistinctProducts
        FROM SalesLines AS sl
        GROUP BY sl.ReportCategoryId, sl.ReportCategoryName
    )
    SELECT
        g.CategoryId,
        g.CategoryName,
        g.UnitsSold,
        g.OrderCount,
        g.DistinctProducts,
        CAST(g.Revenue AS DECIMAL(19,2))                                            AS Revenue,
        CAST(g.Revenue / NULLIF(g.OrderCount, 0) AS DECIMAL(19,2))                  AS RevenuePerOrder,
        CAST(100.0 * g.Revenue / NULLIF(SUM(g.Revenue) OVER (), 0) AS DECIMAL(5,2)) AS RevenueSharePct,
        CAST(100.0 * SUM(g.Revenue) OVER (ORDER BY g.Revenue DESC, g.CategoryId ROWS UNBOUNDED PRECEDING)
                   / NULLIF(SUM(g.Revenue) OVER (), 0) AS DECIMAL(5,2))             AS CumulativeSharePct
    FROM Grouped AS g
    ORDER BY g.Revenue DESC, g.CategoryId
    OPTION (MAXRECURSION 32);
END;
GO
