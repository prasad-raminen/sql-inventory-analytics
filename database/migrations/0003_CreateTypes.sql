/*
    0003 - User-defined table type used to pass order lines to usp_PlaceOrder.

    A table-valued parameter (TVP) lets the API send every line of an order in ONE round trip
    and lets the procedure process them set-based, instead of N calls or string-splitting JSON.
    Types cannot be altered while referenced, so they live with the journaled migrations.
*/
CREATE TYPE dbo.OrderLineList AS TABLE
(
    ProductId INT NOT NULL,
    Quantity  INT NOT NULL CHECK (Quantity > 0)
);
GO
