/*
    usp_PlaceOrder - atomically creates an order and decrements stock.

    Error contract (THROW numbers mapped by the API):
        50001  customer not found                 -> 404
        50002  warehouse not found / inactive     -> 404
        50003  order has no lines                 -> 400
        50004  product not found / inactive       -> 404
        50010  insufficient stock                 -> 409

    Concurrency: the stock check and the decrement are ONE statement
        UPDATE ... SET Qty = Qty - @n WHERE Qty >= @n
    The UPDATE takes an exclusive lock on each row before evaluating the predicate, so two
    concurrent orders for the last unit serialize on that row and only one can succeed. This
    avoids the classic "SELECT then UPDATE" race without escalating to SERIALIZABLE.
    If fewer rows were decremented than requested, we THROW and the CATCH block rolls the
    whole transaction back, so stock and orders can never disagree.
*/
CREATE OR ALTER PROCEDURE dbo.usp_PlaceOrder
    @CustomerId  INT,
    @WarehouseId INT,
    @Lines       dbo.OrderLineList READONLY,
    @OrderId     INT = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;   -- any runtime error dooms the transaction instead of leaving it half-done

    DECLARE @Message NVARCHAR(2048);

    -- Cheap validation outside the transaction: no locks held while we reject bad input.
    IF NOT EXISTS (SELECT 1 FROM @Lines)
        THROW 50003, N'An order must contain at least one line.', 1;

    IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerId = @CustomerId)
    BEGIN
        SET @Message = FORMATMESSAGE(N'Customer %d was not found.', @CustomerId);
        THROW 50001, @Message, 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.Warehouses WHERE WarehouseId = @WarehouseId AND IsActive = 1)
    BEGIN
        SET @Message = FORMATMESSAGE(N'Warehouse %d was not found or is inactive.', @WarehouseId);
        THROW 50002, @Message, 1;
    END;

    -- Collapse duplicate product lines so each inventory row is decremented exactly once.
    DECLARE @Requested TABLE (ProductId INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
    INSERT INTO @Requested (ProductId, Quantity)
    SELECT ProductId, SUM(Quantity)
    FROM @Lines
    GROUP BY ProductId;

    DECLARE @MissingProductId INT =
    (
        SELECT TOP (1) r.ProductId
        FROM @Requested AS r
        LEFT JOIN dbo.Products AS p
               ON p.ProductId = r.ProductId
              AND p.IsActive  = 1
        WHERE p.ProductId IS NULL
        ORDER BY r.ProductId
    );

    IF @MissingProductId IS NOT NULL
    BEGIN
        SET @Message = FORMATMESSAGE(N'Product %d was not found or is inactive.', @MissingProductId);
        THROW 50004, @Message, 1;
    END;

    DECLARE @Decremented TABLE (ProductId INT NOT NULL PRIMARY KEY);
    DECLARE @RequestedCount INT = (SELECT COUNT(*) FROM @Requested);

    BEGIN TRY
        BEGIN TRANSACTION;

        -- Check-and-decrement in a single atomic statement (see header).
        UPDATE i
        SET    i.QuantityOnHand = i.QuantityOnHand - r.Quantity,
               i.UpdatedAtUtc   = SYSUTCDATETIME()
        OUTPUT inserted.ProductId INTO @Decremented (ProductId)
        FROM   dbo.Inventory AS i
        JOIN   @Requested    AS r ON r.ProductId = i.ProductId
        WHERE  i.WarehouseId    = @WarehouseId
          AND  i.QuantityOnHand >= r.Quantity;

        IF (SELECT COUNT(*) FROM @Decremented) <> @RequestedCount
        BEGIN
            -- Report the first short line. Rows we did NOT decrement still show their true stock.
            DECLARE @ShortProductId INT, @ShortRequested INT, @ShortAvailable INT;

            SELECT TOP (1)
                @ShortProductId = r.ProductId,
                @ShortRequested = r.Quantity,
                @ShortAvailable = ISNULL(i.QuantityOnHand, 0)
            FROM @Requested AS r
            LEFT JOIN @Decremented AS d ON d.ProductId = r.ProductId
            LEFT JOIN dbo.Inventory AS i
                   ON i.ProductId   = r.ProductId
                  AND i.WarehouseId = @WarehouseId
            WHERE d.ProductId IS NULL
            ORDER BY r.ProductId;

            SET @Message = FORMATMESSAGE(
                N'Insufficient stock for product %d in warehouse %d: requested %d, available %d.',
                @ShortProductId, @WarehouseId, @ShortRequested, @ShortAvailable);
            THROW 50010, @Message, 1;
        END;

        -- Prices are captured from the catalog at order time and stored on the line.
        DECLARE @Total DECIMAL(19,4) =
        (
            SELECT SUM(CONVERT(DECIMAL(19,4), r.Quantity * p.UnitPrice))
            FROM @Requested AS r
            JOIN dbo.Products AS p ON p.ProductId = r.ProductId
        );

        INSERT INTO dbo.Orders (CustomerId, WarehouseId, Status, TotalAmount)
        VALUES (@CustomerId, @WarehouseId, 'Placed', @Total);

        SET @OrderId = CAST(SCOPE_IDENTITY() AS INT);

        INSERT INTO dbo.OrderItems (OrderId, ProductId, Quantity, UnitPrice)
        SELECT @OrderId, r.ProductId, r.Quantity, p.UnitPrice
        FROM @Requested AS r
        JOIN dbo.Products AS p ON p.ProductId = r.ProductId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        THROW;   -- re-raise with the original number, message and line
    END CATCH;

    -- Single-row result set so callers that do not use OUTPUT parameters still get the key facts.
    SELECT o.OrderId, o.CustomerId, o.WarehouseId, o.OrderDateUtc, o.Status, o.TotalAmount
    FROM dbo.Orders AS o
    WHERE o.OrderId = @OrderId;
END;
GO
