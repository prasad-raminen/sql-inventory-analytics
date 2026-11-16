/*
    0001 - Core normalized schema (3NF)

    Reference data : Categories (self-referencing hierarchy), Suppliers, Warehouses
    Catalog        : Products
    Stock          : Inventory  (one row per product per warehouse)
    Sales          : Customers, Orders, OrderItems

    Conventions
      * Surrogate INT IDENTITY keys, named PK_<Table>; FKs named FK_<Child>_<Parent>.
      * Money uses DECIMAL(19,4) for line maths and DECIMAL(10,2) for list prices; never FLOAT.
      * Timestamps are UTC DATETIME2(3) with SYSUTCDATETIME() defaults.
      * Business rules that never change are enforced in the database with CHECK / UNIQUE constraints,
        so every client (API, ad-hoc scripts, ETL) gets the same guarantees.
*/
-- Pinned explicitly so the result never depends on the client's defaults (sqlcmd without -I runs
-- with QUOTED_IDENTIFIER OFF). Filtered indexes, indexes on computed columns and the modules that
-- write to those tables all require ANSI_NULLS and QUOTED_IDENTIFIER ON.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


CREATE TABLE dbo.Categories
(
    CategoryId       INT IDENTITY(1,1) NOT NULL,
    Name             NVARCHAR(100)     NOT NULL,
    ParentCategoryId INT               NULL,
    CONSTRAINT PK_Categories PRIMARY KEY CLUSTERED (CategoryId),
    CONSTRAINT UQ_Categories_Name UNIQUE (Name),
    CONSTRAINT FK_Categories_ParentCategory FOREIGN KEY (ParentCategoryId) REFERENCES dbo.Categories (CategoryId),
    CONSTRAINT CK_Categories_NotOwnParent CHECK (ParentCategoryId IS NULL OR ParentCategoryId <> CategoryId)
);
GO

CREATE TABLE dbo.Suppliers
(
    SupplierId   INT IDENTITY(1,1) NOT NULL,
    Name         NVARCHAR(150)     NOT NULL,
    ContactEmail VARCHAR(254)      NULL,
    Country      NVARCHAR(60)      NOT NULL,
    CreatedAtUtc DATETIME2(3)      NOT NULL CONSTRAINT DF_Suppliers_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Suppliers PRIMARY KEY CLUSTERED (SupplierId),
    CONSTRAINT UQ_Suppliers_Name UNIQUE (Name),
    CONSTRAINT CK_Suppliers_ContactEmail CHECK (ContactEmail IS NULL OR ContactEmail LIKE '%_@_%._%')
);
GO

CREATE TABLE dbo.Warehouses
(
    WarehouseId INT IDENTITY(1,1) NOT NULL,
    Code        VARCHAR(10)       NOT NULL,
    Name        NVARCHAR(100)     NOT NULL,
    City        NVARCHAR(100)     NOT NULL,
    Country     NVARCHAR(60)      NOT NULL,
    IsActive    BIT               NOT NULL CONSTRAINT DF_Warehouses_IsActive DEFAULT (1),
    CONSTRAINT PK_Warehouses PRIMARY KEY CLUSTERED (WarehouseId),
    CONSTRAINT UQ_Warehouses_Code UNIQUE (Code),
    CONSTRAINT CK_Warehouses_Code CHECK (LEN(Code) >= 3)
);
GO

CREATE TABLE dbo.Products
(
    ProductId    INT IDENTITY(1,1) NOT NULL,
    Sku          VARCHAR(32)       NOT NULL,
    Name         NVARCHAR(200)     NOT NULL,
    CategoryId   INT               NOT NULL,
    SupplierId   INT               NOT NULL,
    UnitPrice    DECIMAL(10,2)     NOT NULL,
    ReorderLevel INT               NOT NULL CONSTRAINT DF_Products_ReorderLevel DEFAULT (10),
    IsActive     BIT               NOT NULL CONSTRAINT DF_Products_IsActive DEFAULT (1),
    CreatedAtUtc DATETIME2(3)      NOT NULL CONSTRAINT DF_Products_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Products PRIMARY KEY CLUSTERED (ProductId),
    CONSTRAINT UQ_Products_Sku UNIQUE (Sku),
    CONSTRAINT FK_Products_Categories FOREIGN KEY (CategoryId) REFERENCES dbo.Categories (CategoryId),
    CONSTRAINT FK_Products_Suppliers  FOREIGN KEY (SupplierId) REFERENCES dbo.Suppliers (SupplierId),
    CONSTRAINT CK_Products_UnitPrice    CHECK (UnitPrice >= 0),
    CONSTRAINT CK_Products_ReorderLevel CHECK (ReorderLevel >= 0)
);
GO

-- Stock is tracked per (product, warehouse). The composite PK doubles as the
-- clustered index used by usp_PlaceOrder's "lock and decrement" seek.
CREATE TABLE dbo.Inventory
(
    ProductId      INT          NOT NULL,
    WarehouseId    INT          NOT NULL,
    QuantityOnHand INT          NOT NULL,
    UpdatedAtUtc   DATETIME2(3) NOT NULL CONSTRAINT DF_Inventory_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Inventory PRIMARY KEY CLUSTERED (ProductId, WarehouseId),
    CONSTRAINT FK_Inventory_Products   FOREIGN KEY (ProductId)   REFERENCES dbo.Products (ProductId),
    CONSTRAINT FK_Inventory_Warehouses FOREIGN KEY (WarehouseId) REFERENCES dbo.Warehouses (WarehouseId),
    -- Last line of defence against overselling: stock can never go negative,
    -- even if a future code path forgets the explicit check in usp_PlaceOrder.
    CONSTRAINT CK_Inventory_QuantityOnHand CHECK (QuantityOnHand >= 0)
);
GO

CREATE TABLE dbo.Customers
(
    CustomerId   INT IDENTITY(1,1) NOT NULL,
    Email        VARCHAR(254)      NOT NULL,
    FirstName    NVARCHAR(100)     NOT NULL,
    LastName     NVARCHAR(100)     NOT NULL,
    City         NVARCHAR(100)     NULL,
    Country      NVARCHAR(60)      NOT NULL,
    CreatedAtUtc DATETIME2(3)      NOT NULL CONSTRAINT DF_Customers_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Customers PRIMARY KEY CLUSTERED (CustomerId),
    CONSTRAINT UQ_Customers_Email UNIQUE (Email),
    CONSTRAINT CK_Customers_Email CHECK (Email LIKE '%_@_%._%')
);
GO

CREATE TABLE dbo.Orders
(
    OrderId      INT IDENTITY(1,1) NOT NULL,
    CustomerId   INT               NOT NULL,
    WarehouseId  INT               NOT NULL,   -- fulfilling warehouse
    OrderDateUtc DATETIME2(3)      NOT NULL CONSTRAINT DF_Orders_OrderDateUtc DEFAULT (SYSUTCDATETIME()),
    Status       VARCHAR(20)       NOT NULL CONSTRAINT DF_Orders_Status DEFAULT ('Placed'),
    TotalAmount  DECIMAL(19,4)     NOT NULL CONSTRAINT DF_Orders_TotalAmount DEFAULT (0),
    CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (OrderId),
    CONSTRAINT FK_Orders_Customers  FOREIGN KEY (CustomerId)  REFERENCES dbo.Customers (CustomerId),
    CONSTRAINT FK_Orders_Warehouses FOREIGN KEY (WarehouseId) REFERENCES dbo.Warehouses (WarehouseId),
    CONSTRAINT CK_Orders_Status CHECK (Status IN ('Placed', 'Shipped', 'Delivered', 'Cancelled')),
    CONSTRAINT CK_Orders_TotalAmount CHECK (TotalAmount >= 0)
);
GO

CREATE TABLE dbo.OrderItems
(
    OrderItemId INT IDENTITY(1,1) NOT NULL,
    OrderId     INT               NOT NULL,
    ProductId   INT               NOT NULL,
    Quantity    INT               NOT NULL,
    UnitPrice   DECIMAL(10,2)     NOT NULL,   -- price captured at order time, not a live lookup
    LineTotal   AS (CONVERT(DECIMAL(19,4), Quantity * UnitPrice)) PERSISTED,
    CONSTRAINT PK_OrderItems PRIMARY KEY CLUSTERED (OrderItemId),
    CONSTRAINT FK_OrderItems_Orders   FOREIGN KEY (OrderId)   REFERENCES dbo.Orders (OrderId) ON DELETE CASCADE,
    CONSTRAINT FK_OrderItems_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products (ProductId),
    CONSTRAINT UQ_OrderItems_Order_Product UNIQUE (OrderId, ProductId),
    CONSTRAINT CK_OrderItems_Quantity  CHECK (Quantity > 0),
    CONSTRAINT CK_OrderItems_UnitPrice CHECK (UnitPrice >= 0)
);
GO
