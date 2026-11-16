using InventoryAnalytics.Api.Contracts;
using InventoryAnalytics.Core.Models;
using InventoryAnalytics.Core.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InventoryAnalytics.Api.Endpoints;

public static class OrderEndpoints
{
    private const string GetOrderRouteName = "GetOrderById";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/orders").WithTags("Orders");

        group.MapPost("/", PlaceOrder)
            .WithName("PlaceOrder")
            .WithSummary("Place an order; stock is checked and decremented atomically by usp_PlaceOrder.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{orderId:int}", GetOrder)
            .WithName(GetOrderRouteName)
            .WithSummary("Get an order with its lines (usp_GetOrderDetails).")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<CreatedAtRoute<OrderSummary>> PlaceOrder(
        PlaceOrderRequest request, IOrderService orders, CancellationToken cancellationToken)
    {
        OrderSummary order = await orders.PlaceOrderAsync(request.ToCommand(), cancellationToken);
        return TypedResults.CreatedAtRoute(order, GetOrderRouteName, new { orderId = order.OrderId });
    }

    private static async Task<Ok<OrderDetails>> GetOrder(
        int orderId, IOrderService orders, CancellationToken cancellationToken) =>
        TypedResults.Ok(await orders.GetOrderAsync(orderId, cancellationToken));
}
