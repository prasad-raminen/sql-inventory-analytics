using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Infrastructure.Repositories;
using InventoryAnalytics.IntegrationTests.Infrastructure;

namespace InventoryAnalytics.IntegrationTests.Repositories;

/// <summary>
/// The production Dapper repositories against the real schema: parameter types, TVP shape, result
/// set mapping and SQL error translation all have to line up for these to pass.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RepositoryEndToEndTests(DatabaseFixture fixture)
{
    private readonly TestData _data = fixture.Data;
    private readonly OrderRepository _orders = fixture.CreateOrderRepository();
    private readonly ProductRepository _products = fixture.CreateProductRepository();
    private readonly ReportRepository _reports = fixture.CreateReportRepository();

    [SqlServerFact]
    public async Task OrderRepository_PlaceThenGetOrder_RoundTripsHeaderAndLines()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int customer = await _data.CreateCustomerAsync(s.Token, firstName: "Ada", lastName: "Lovelace");
        int scanner = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 1500.00m);
        int battery = await _data.CreateProductAsync(s.Sku("B"), s.CategoryId, s.SupplierId, 19.99m);
        await _data.SetStockAsync(scanner, s.WarehouseId, 2);
        await _data.SetStockAsync(battery, s.WarehouseId, 50);

        OrderSummary placed = await _orders.PlaceOrderAsync(
            new PlaceOrderCommand(customer, s.WarehouseId, [new OrderLine(scanner, 2), new OrderLine(battery, 3)]),
            CancellationToken.None);

        placed.OrderId.Should().BePositive();
        placed.Status.Should().Be("Placed");
        placed.TotalAmount.Should().Be(2 * 1500.00m + 3 * 19.99m);
        (await _data.GetStockAsync(scanner, s.WarehouseId)).Should().Be(0);
        (await _data.GetStockAsync(battery, s.WarehouseId)).Should().Be(47);

        OrderDetails? details = await _orders.GetOrderAsync(placed.OrderId, CancellationToken.None);

        details.Should().NotBeNull();
        details!.Header.Should().BeEquivalentTo(new
        {
            placed.OrderId,
            CustomerId = customer,
            CustomerName = "Ada Lovelace",
            WarehouseId = s.WarehouseId,
            WarehouseCode = $"W{s.Token}",
            Status = "Placed",
            TotalAmount = 3059.97m,
        });
        details.Lines.Should().BeEquivalentTo(new[]
        {
            new { ProductId = scanner, Sku = s.Sku("A"), Quantity = 2, UnitPrice = 1500.00m, LineTotal = 3000.00m },
            new { ProductId = battery, Sku = s.Sku("B"), Quantity = 3, UnitPrice = 19.99m, LineTotal = 59.97m },
        });
    }

    [SqlServerFact]
    public async Task OrderRepository_GetUnknownOrder_ReturnsNull()
    {
        int unknownOrderId = await _data.ScalarAsync<int>("SELECT ISNULL(MAX(OrderId), 0) + 1000 FROM dbo.Orders;");

        OrderDetails? details = await _orders.GetOrderAsync(unknownOrderId, CancellationToken.None);

        details.Should().BeNull();
    }

    [SqlServerFact]
    public async Task OrderRepository_InsufficientStock_ThrowsInsufficientStockExceptionAndKeepsStock()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int product = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 10m);
        await _data.SetStockAsync(product, s.WarehouseId, 3);

        Func<Task> act = () => _orders.PlaceOrderAsync(
            new PlaceOrderCommand(s.CustomerId, s.WarehouseId, [new OrderLine(product, 4)]), CancellationToken.None);

        await act.Should().ThrowAsync<InsufficientStockException>()
            .WithMessage($"Insufficient stock for product {product}*requested 4, available 3.");
        (await _data.GetStockAsync(product, s.WarehouseId)).Should().Be(3);
        (await _data.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Orders WHERE CustomerId = @CustomerId;", new { s.CustomerId }))
            .Should().Be(0);
    }

    [SqlServerFact]
    public async Task OrderRepository_UnknownCustomer_ThrowsNotFoundException()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int product = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 10m);
        await _data.SetStockAsync(product, s.WarehouseId, 3);
        int unknownCustomer = await _data.ScalarAsync<int>("SELECT ISNULL(MAX(CustomerId), 0) + 1000 FROM dbo.Customers;");

        Func<Task> act = () => _orders.PlaceOrderAsync(
            new PlaceOrderCommand(unknownCustomer, s.WarehouseId, [new OrderLine(product, 1)]), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>().WithMessage($"Customer {unknownCustomer} was not found.");
    }

    [SqlServerFact]
    public async Task ProductRepository_ReadsCatalogFiltersInactiveAndReturnsStockPerWarehouse()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int active = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 99.95m, reorderLevel: 4);
        int retired = await _data.CreateProductAsync(s.Sku("R"), s.CategoryId, s.SupplierId, 5m, isActive: false);
        await _data.SetStockAsync(active, s.WarehouseId, 3);

        IReadOnlyList<Product> activeOnly = await _products.GetProductsAsync(s.CategoryId, includeInactive: false, CancellationToken.None);
        IReadOnlyList<Product> withInactive = await _products.GetProductsAsync(s.CategoryId, includeInactive: true, CancellationToken.None);
        Product? single = await _products.GetByIdAsync(active, CancellationToken.None);
        IReadOnlyList<WarehouseStock> stock = await _products.GetStockByWarehouseAsync(active, CancellationToken.None);

        activeOnly.Select(p => p.ProductId).Should().Equal(active);
        withInactive.Select(p => p.ProductId).Should().BeEquivalentTo(new[] { active, retired });
        single.Should().BeEquivalentTo(new
        {
            ProductId = active, Sku = s.Sku("A"), CategoryName = $"IT Leaf {s.Token}", SupplierName = $"IT Supplier {s.Token}",
            UnitPrice = 99.95m, ReorderLevel = 4, IsActive = true, TotalOnHand = 3,
        });
        stock.Should().ContainSingle(w => w.WarehouseId == s.WarehouseId)
            .Which.Should().BeEquivalentTo(new { WarehouseCode = $"W{s.Token}", QuantityOnHand = 3, IsLowStock = true });
        (await _products.GetByIdAsync(-1, CancellationToken.None)).Should().BeNull();
    }

    [SqlServerFact]
    public async Task ProductRepository_SeededProductLookedUpBySku_MapsCatalogColumns()
    {
        int productId = await _data.GetProductIdAsync("LS-PRO-360");

        Product? product = await _products.GetByIdAsync(productId, CancellationToken.None);

        product.Should().BeEquivalentTo(new
        {
            Sku = "LS-PRO-360",
            Name = "Terrestrial Laser Scanner Pro 360",
            CategoryName = "Laser Scanners",
            SupplierName = "Northwind Optics",
            UnitPrice = 48500.00m,
            ReorderLevel = 3,
            IsActive = true,
        });
        product!.TotalOnHand.Should().BePositive();
    }

    [SqlServerFact]
    public async Task ReportRepository_LowStockFilteredByWarehouse_MapsViewRows()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int product = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 10m, reorderLevel: 6);
        await _data.SetStockAsync(product, s.WarehouseId, 2);

        IReadOnlyList<LowStockItem> rows = await _reports.GetLowStockAsync(s.WarehouseId, CancellationToken.None);

        rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProductId = product, Sku = s.Sku("A"), WarehouseId = s.WarehouseId, QuantityOnHand = 2, ReorderLevel = 6,
            SuggestedReorderQty = 10, SupplierEmail = "buyer@supplier.example",
        });
    }

    [SqlServerFact]
    public async Task ReportRepository_MonthlySalesForYear_ReturnsOnlyThatYear()
    {
        int year = ReportWindows.RepositoryMonthlySalesYear;
        Scenario s = await _data.CreateScenarioAsync();
        int product = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 250m);
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, new DateTime(year, 7, 14, 10, 0, 0), [new(product, 2, 250m)]);

        IReadOnlyList<MonthlySales> months = await _reports.GetMonthlySalesAsync(year, CancellationToken.None);

        months.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Year = year, Month = 7, OrderCount = 1, UniqueCustomers = 1, UnitsSold = 2, Revenue = 500m, AverageOrderValue = 500m,
        });
    }

    [SqlServerFact]
    public async Task ReportRepository_TopProductsAndSalesByCategory_MapProcedureResults()
    {
        Scenario s = await _data.CreateScenarioAsync();
        int best = await _data.CreateProductAsync(s.Sku("A"), s.CategoryId, s.SupplierId, 300m);
        int second = await _data.CreateProductAsync(s.Sku("B"), s.CategoryId, s.SupplierId, 100m);

        DateTime topFrom = ReportWindows.RepositoryTopProducts;
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, topFrom.AddDays(3), [new(best, 1, 300m), new(second, 1, 100m)]);
        DateTime categoryFrom = ReportWindows.RepositorySalesByCategory;
        await _data.InsertOrderAsync(s.CustomerId, s.WarehouseId, categoryFrom.AddDays(3), [new(best, 2, 300m)]);

        IReadOnlyList<TopProduct> top = await _reports.GetTopProductsAsync(
            new ReportPeriod(topFrom, topFrom.AddMonths(1)), top: 1, categoryId: null, CancellationToken.None);
        IReadOnlyList<CategorySales> byCategory = await _reports.GetSalesByCategoryAsync(
            new ReportPeriod(categoryFrom, categoryFrom.AddMonths(1)), rollUpToRoot: true, CancellationToken.None);

        top.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProductId = best, Revenue = 300m, RevenueRank = 1L, RevenueSharePct = (decimal?)75.00m,
        });
        byCategory.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            CategoryId = s.RootCategoryId, CategoryName = $"IT Root {s.Token}", Revenue = 600m, CumulativeSharePct = (decimal?)100m,
        });
    }

    [SqlServerFact]
    public async Task ReportRepository_EmptyPeriod_TranslatesProcedureErrorToValidationException()
    {
        DateTime instant = ReportWindows.RepositoryTopProducts;

        Func<Task> act = () => _reports.GetTopProductsAsync(new ReportPeriod(instant, instant), top: 5, categoryId: null, CancellationToken.None);

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("from");
    }
}
