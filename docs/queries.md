# Key queries and index choices

This document explains *why* the SQL looks the way it does: what each important query does,
which index it is meant to use, and what plan shape we expect from the optimizer.

> **Honesty note.** The plan shapes below are the *expected* plans, reasoned from the index
> definitions and how SQL Server's optimizer treats these constructs. They were not captured
> from a live server. The `sql-integration` CI job runs the scripts against SQL Server 2022 and
> checks query *results*, not plans (see the README's "Validation status"). To confirm the plans,
> run the stack with `docker compose up` and use `SET STATISTICS IO, TIME ON` and the actual
> execution plan (Ctrl+M in SSMS / Azure Data Studio).

## Schema at a glance

```
Categories (self-referencing)      Suppliers          Warehouses
        \                             /                    |
         +--------- Products --------+                     |
                       |  \                                |
                       |   +------- Inventory (ProductId, WarehouseId) PK
                       |
Customers --- Orders --+-- OrderItems (LineTotal = Quantity * UnitPrice, PERSISTED)
```

* Third normal form: product prices live on `Products`; the price *at order time* is copied to
  `OrderItems.UnitPrice` on purpose (historical fact, not a duplicate).
* Constraints carry business rules every client must respect: `CK_Inventory_QuantityOnHand`
  (stock can never go negative), `CK_Orders_Status` (closed set of statuses),
  `UQ_OrderItems_Order_Product` (one line per product per order), positive quantities and prices.
* Money is `DECIMAL`, never `FLOAT`. Timestamps are UTC `DATETIME2(3)`.

## Index design

Rule: every nonclustered index must serve a named query. Each index slows down
`usp_PlaceOrder` (which writes `Inventory`, `Orders` and `OrderItems`), so we avoid speculative ones.

| Index | Key (INCLUDE) | Serves | Why |
|---|---|---|---|
| `PK_Inventory` (clustered) | `ProductId, WarehouseId` | `usp_PlaceOrder` decrement, `fn_GetProductStock`, `vw_ProductCatalog` stock total | Composite natural key: a single-row seek locks exactly the row being decremented. |
| `IX_Inventory_WarehouseId` | `WarehouseId` (`QuantityOnHand`) | `vw_LowStock` filtered by warehouse | The PK leads on `ProductId`, so it can't seek by warehouse. Including the quantity makes it covering. |
| `IX_Products_CategoryId` | `CategoryId` (`Name, Sku, UnitPrice, IsActive`) | catalog by category, FK checks | SQL Server does **not** index foreign keys automatically. |
| `IX_Products_SupplierId` | `SupplierId` | FK checks, supplier joins | Same FK reasoning. |
| `IX_Products_Active_Name` | `Name` (`Sku, CategoryId, UnitPrice`) `WHERE IsActive = 1` | catalog browsing | **Filtered index**: smaller and cheaper to maintain because retired products are never browsed. |
| `IX_Categories_ParentCategoryId` | `ParentCategoryId` | recursive CTE in `usp_SalesByCategory` | Each recursion step seeks children by parent. |
| `IX_Orders_OrderDateUtc` | `OrderDateUtc` (`Status, TotalAmount, CustomerId, WarehouseId`) | every date-ranged report | A range seek on the date and covering every column the reports read, so no Key Lookup. |
| `IX_Orders_CustomerId_OrderDateUtc` | `CustomerId, OrderDateUtc DESC` (`Status, TotalAmount`) | "my orders, newest first" | Equality then sort column: rows come back pre-sorted, so there is no Sort operator. |
| `IX_OrderItems_OrderId` | `OrderId` (`ProductId, Quantity, UnitPrice, LineTotal`) | order details, joins from `Orders` | Covering index for the order-to-lines join. |
| `IX_OrderItems_ProductId` | `ProductId` (`OrderId, Quantity, LineTotal`) | product-centric aggregates | Rows arrive grouped by product, which suits a Stream Aggregate. |

`LineTotal` is a **persisted computed column**, so it can be stored in index INCLUDE lists and
aggregated without recomputing `Quantity * UnitPrice` for every row.

## `usp_PlaceOrder`: correctness under concurrency

```sql
UPDATE i
SET    i.QuantityOnHand = i.QuantityOnHand - r.Quantity
OUTPUT inserted.ProductId INTO @Decremented
FROM   dbo.Inventory AS i
JOIN   @Requested    AS r ON r.ProductId = i.ProductId
WHERE  i.WarehouseId = @WarehouseId
  AND  i.QuantityOnHand >= r.Quantity;
```

* **Check-and-decrement is one statement.** The classic bug is `SELECT stock; IF enough UPDATE`:
  two sessions can both read "1 left" and both sell it. Here the `UPDATE` takes an update/exclusive
  lock on each `Inventory` row *before* evaluating `QuantityOnHand >= Quantity`, so concurrent
  orders for the same row serialize and the second one sees the already-decremented value.
  This gives the correct result under the default READ COMMITTED isolation, without SERIALIZABLE.
* **Partial success is rolled back.** If fewer rows were decremented than products requested,
  the procedure `THROW`s `50010`. With `XACT_ABORT ON` and `TRY/CATCH`, the `CATCH` block rolls the
  whole transaction back (including rows already decremented) and re-throws the original error.
  The API maps `50010` to **HTTP 409 Conflict**.
* **Defence in depth.** `CK_Inventory_QuantityOnHand CHECK (QuantityOnHand >= 0)` guarantees that
  no future code path can oversell, even if it forgets the predicate.
* **Validation happens before `BEGIN TRANSACTION`**, so requests that will be rejected
  (unknown customer, inactive warehouse, empty order) never hold locks.
* **Table-valued parameter (`dbo.OrderLineList`).** All lines arrive in one round trip and are
  processed set-based. Duplicate product lines are collapsed with `GROUP BY` first, so each
  inventory row is decremented exactly once.
* Expected plan: a Clustered Index **Seek** on `PK_Inventory` per requested product (nested
  loops from the small `@Requested` table variable), then inserts into `Orders` and `OrderItems`.

Error contract (THROW numbers, all >= 50000):

| Number | Meaning | HTTP |
|---|---|---|
| 50001 | customer not found | 404 |
| 50002 | warehouse not found / inactive | 404 |
| 50003 | order has no lines | 400 |
| 50004 | product not found / inactive | 404 |
| 50010 | insufficient stock (transaction rolled back) | 409 |
| 50020 / 50021 | invalid `@Top` / date range in reports | 400 |

## `usp_GetTopProducts`: window functions

```sql
RANK()       OVER (ORDER BY ps.Revenue DESC)                               AS RevenueRank,
DENSE_RANK() OVER (ORDER BY ps.UnitsSold DESC)                             AS UnitsRank,
ROW_NUMBER() OVER (PARTITION BY p.CategoryId ORDER BY ps.Revenue DESC, p.ProductId) AS RankInCategory,
100.0 * ps.Revenue / SUM(ps.Revenue) OVER ()                               AS RevenueSharePct
```

* `RANK` vs `DENSE_RANK` vs `ROW_NUMBER`: we filter on `RANK() <= @Top` so tied products are never
  dropped arbitrarily (a "top 5" can return 6 rows if two products tie for 5th). That is the
  honest answer for a leaderboard. `ROW_NUMBER` with a `ProductId` tie-breaker gives a unique,
  deterministic position inside each category.
* `SUM(...) OVER ()` computes the grand total in the same pass, with no second scan or subquery.
* The aggregation CTE runs first, so the window functions operate on about 15 rows, not on
  thousands of order lines.
* `@CategoryId` is optional. A catch-all predicate `(@CategoryId IS NULL OR CategoryId = @CategoryId)`
  usually gets one compromise plan; `OPTION (RECOMPILE)` compiles a plan for the actual values.
  That trade-off is right for a rarely-run analytics query, and wrong for a hot OLTP path.
* Expected plan: a range **Seek** on `IX_Orders_OrderDateUtc` (`[From, To)` half-open range, which
  avoids `23:59:59.997` edge cases), a join to `IX_OrderItems_OrderId`, a Hash/Stream Aggregate by
  product, then a Sort and Window Spool / Segment operators for the window functions.

## `usp_SalesByCategory`: CTE pipeline

1. `CategoryTree` is a **recursive CTE**. The anchor selects root categories and the recursive member
   walks down via `IX_Categories_ParentCategoryId`, carrying each root's id and name along.
   `OPTION (MAXRECURSION 32)` turns an accidental cycle into an error instead of a runaway query.
2. `SalesLines` holds the order lines in the date range, each tagged with its *reporting* category
   (the root when `@RollUpToRoot = 1`, otherwise the leaf).
3. `Grouped` aggregates per reporting category.
4. The final `SELECT` adds `RevenueSharePct` (`SUM() OVER ()`) and a **running total**
   `SUM() OVER (ORDER BY Revenue DESC ROWS UNBOUNDED PRECEDING)`, which gives the Pareto
   (80/20) curve. `ROWS` (not the default `RANGE`) is specified explicitly. It is faster
   (in-memory spool) and gives a strictly increasing running total even when revenues tie.

## `vw_MonthlySales`: avoiding fan-out

```sql
WITH OrderFacts AS (
    SELECT o.OrderId, o.CustomerId, o.TotalAmount, <month>, lines.Units
    FROM dbo.Orders AS o
    CROSS APPLY (SELECT SUM(oi.Quantity) AS Units FROM dbo.OrderItems oi WHERE oi.OrderId = o.OrderId) AS lines
    WHERE o.Status <> 'Cancelled')
SELECT ..., SUM(TotalAmount), LAG(SUM(TotalAmount)) OVER (ORDER BY MonthStart) ...
GROUP BY MonthStart;
```

* A naive `Orders JOIN OrderItems ... SUM(o.TotalAmount)` counts each order's total once **per line**
  (fan-out) and inflates revenue. Reducing to one row per order first (`CROSS APPLY`) keeps
  `SUM(TotalAmount)` correct while still reporting units.
* `LAG(SUM(...)) OVER (ORDER BY MonthStart)` computes month-over-month growth without a self-join.
  `NULLIF(..., 0)` prevents divide-by-zero. The first month returns `NULL`, not a fake 0%.
* Grouping on an expression of `OrderDateUtc` is not sargable for a *range*, so this view reads the
  whole of `IX_Orders_OrderDateUtc` (a narrow covering index, so the scan is cheap). The API's
  `?year=` filter is applied to the view's output. For a large fact table, the next step would be
  an indexed view or a monthly summary table maintained by the order procedure.

## `fn_GetProductStock`: why an *inline* TVF

```sql
CREATE OR ALTER FUNCTION dbo.fn_GetProductStock (@ProductId INT)
RETURNS TABLE AS RETURN SELECT ... FROM Products CROSS JOIN Warehouses LEFT JOIN Inventory ...
```

* An inline TVF is expanded into the calling query like a parameterized view, so the optimizer
  sees through it, estimates cardinality properly and seeks `PK_Inventory`.
* A multi-statement TVF (`RETURNS @t TABLE ... BEGIN ... END`) is opaque. Its fixed row estimates
  can lead to poor join choices, and it cannot be inlined.
* `CROSS JOIN Warehouses LEFT JOIN Inventory` returns **every active warehouse**, with `0` where
  no stock row exists. That is the shape a "where can I ship this from?" screen needs.

## `vw_ProductCatalog`: `OUTER APPLY` for per-product totals

`OUTER APPLY (SELECT SUM(QuantityOnHand) FROM Inventory WHERE ProductId = p.ProductId)` keeps the
aggregate correlated to each product. When the API filters the view by `ProductId`, the predicate
is pushed into the outer query and the apply becomes a single clustered-index seek on
`PK_Inventory`, instead of aggregating stock for every product and then filtering.

## API-side query construction (Dapper)

* Every value is a **parameter** (`@CategoryId`, `@WarehouseId`, ...). There is no string
  concatenation of user input, so SQL injection is not possible through the API.
* Optional filters are appended only when present (`WHERE CategoryId = @CategoryId` is added only
  if a category was supplied). Each query *shape* gets its own cached, simple plan, avoiding the
  catch-all `(@p IS NULL OR col = @p)` pattern in hot paths.
* Date parameters are sent as `DbType.DateTime2`, matching the procedures' `DATETIME2(3)`
  parameters. The default `DateTime` mapping would use the legacy `DATETIME` type and add implicit
  conversions.
* The API exposes inclusive calendar dates (`from=2025-01-01&to=2025-12-31`) and converts them
  to the half-open `[from, to + 1 day)` range the procedures use.
