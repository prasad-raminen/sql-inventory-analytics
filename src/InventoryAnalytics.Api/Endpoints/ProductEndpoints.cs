using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InventoryAnalytics.Api.Endpoints;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/products").WithTags("Products");

        group.MapGet("/", GetProducts)
            .WithName("GetProducts")
            .WithSummary("List the product catalog with total stock across warehouses (vw_ProductCatalog).")
            .ProducesValidationProblem();

        group.MapGet("/{productId:int}", GetProduct)
            .WithName("GetProductById")
            .WithSummary("Get one product.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{productId:int}/stock", GetStock)
            .WithName("GetProductStock")
            .WithSummary("Per-warehouse stock for a product (inline TVF fn_GetProductStock).")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<Ok<IReadOnlyList<Product>>> GetProducts(
        int? categoryId, bool? includeInactive, IProductService products, CancellationToken cancellationToken) =>
        TypedResults.Ok(await products.GetProductsAsync(categoryId, includeInactive ?? false, cancellationToken));

    private static async Task<Ok<Product>> GetProduct(
        int productId, IProductService products, CancellationToken cancellationToken) =>
        TypedResults.Ok(await products.GetProductAsync(productId, cancellationToken));

    private static async Task<Ok<ProductAvailability>> GetStock(
        int productId, IProductService products, CancellationToken cancellationToken) =>
        TypedResults.Ok(await products.GetAvailabilityAsync(productId, cancellationToken));
}
