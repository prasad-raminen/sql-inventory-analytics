/*
    vw_MonthlySales - one row per calendar month (UTC) of non-cancelled orders.

    * Orders are first reduced to one row per order with its unit count (CTE OrderFacts), so
      SUM(TotalAmount) is never inflated by the order -> lines fan-out.
    * LAG() over the grouped result gives month-over-month revenue growth without a self-join.
      Note: LAG is evaluated before any outer WHERE on Year, so January still compares
      against the previous December.
*/
-- Pinned explicitly so the result never depends on the client's defaults (sqlcmd without -I runs
-- with QUOTED_IDENTIFIER OFF). Filtered indexes, indexes on computed columns and the modules that
-- write to those tables all require ANSI_NULLS and QUOTED_IDENTIFIER ON.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER VIEW dbo.vw_MonthlySales
AS
WITH OrderFacts AS
(
    SELECT
        o.OrderId,
        o.CustomerId,
        o.TotalAmount,
        DATEFROMPARTS(YEAR(o.OrderDateUtc), MONTH(o.OrderDateUtc), 1) AS MonthStart,
        lines.Units
    FROM dbo.Orders AS o
    CROSS APPLY
    (
        SELECT SUM(oi.Quantity) AS Units
        FROM dbo.OrderItems AS oi
        WHERE oi.OrderId = o.OrderId
    ) AS lines
    WHERE o.Status <> 'Cancelled'
)
SELECT
    YEAR(f.MonthStart)                                    AS [Year],
    MONTH(f.MonthStart)                                   AS [Month],
    f.MonthStart,
    COUNT(*)                                              AS OrderCount,
    COUNT(DISTINCT f.CustomerId)                          AS UniqueCustomers,
    ISNULL(SUM(f.Units), 0)                               AS UnitsSold,
    CAST(SUM(f.TotalAmount) AS DECIMAL(19,2))             AS Revenue,
    CAST(AVG(f.TotalAmount) AS DECIMAL(19,2))             AS AverageOrderValue,
    CAST(
        100.0 * (SUM(f.TotalAmount) - LAG(SUM(f.TotalAmount)) OVER (ORDER BY f.MonthStart))
        / NULLIF(LAG(SUM(f.TotalAmount)) OVER (ORDER BY f.MonthStart), 0)
        AS DECIMAL(9,2))                                  AS RevenueChangePct
FROM OrderFacts AS f
GROUP BY f.MonthStart;
GO
