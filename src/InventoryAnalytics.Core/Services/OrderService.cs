using InventoryAnalytics.Core.Abstractions;
using InventoryAnalytics.Core.Errors;
using InventoryAnalytics.Core.Models;
using Microsoft.Extensions.Logging;

namespace InventoryAnalytics.Core.Services;

public interface IOrderService
{
    Task<OrderSummary> PlaceOrderAsync(PlaceOrderCommand command, CancellationToken cancellationToken = default);

    Task<OrderDetails> GetOrderAsync(int orderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates order requests up front (cheap, no database round trip) and delegates the
/// stock check + decrement to <c>dbo.usp_PlaceOrder</c>, which is the single source of truth
/// for availability because only it can check and decrement atomically.
/// </summary>
public sealed class OrderService(IOrderRepository orders, ILogger<OrderService> logger) : IOrderService
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 10_000;

    public async Task<OrderSummary> PlaceOrderAsync(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);

        try
        {
            OrderSummary order = await orders.PlaceOrderAsync(command, cancellationToken);
            logger.LogInformation(
                "Placed order {OrderId} for customer {CustomerId} from warehouse {WarehouseId}: {LineCount} line(s), total {TotalAmount}",
                order.OrderId, order.CustomerId, order.WarehouseId, command.Lines.Count, order.TotalAmount);
            return order;
        }
        catch (InsufficientStockException ex)
        {
            logger.LogWarning(
                "Order for customer {CustomerId} rejected for insufficient stock in warehouse {WarehouseId}: {Reason}",
                command.CustomerId, command.WarehouseId, ex.Message);
            throw;
        }
    }

    public async Task<OrderDetails> GetOrderAsync(int orderId, CancellationToken cancellationToken = default)
    {
        return await orders.GetOrderAsync(orderId, cancellationToken)
            ?? throw NotFoundException.For("Order", orderId);
    }

    private static void Validate(PlaceOrderCommand command)
    {
        var errors = new Dictionary<string, string[]>();

        if (command.CustomerId <= 0)
        {
            errors["customerId"] = ["customerId must be a positive number."];
        }

        if (command.WarehouseId <= 0)
        {
            errors["warehouseId"] = ["warehouseId must be a positive number."];
        }

        if (command.Lines is null || command.Lines.Count == 0)
        {
            errors["lines"] = ["An order must contain at least one line."];
        }
        else if (command.Lines.Count > MaxLines)
        {
            errors["lines"] = [$"An order can contain at most {MaxLines} lines."];
        }
        else
        {
            for (var i = 0; i < command.Lines.Count; i++)
            {
                OrderLine line = command.Lines[i];
                if (line.ProductId <= 0)
                {
                    errors[$"lines[{i}].productId"] = ["productId must be a positive number."];
                }

                if (line.Quantity is < 1 or > MaxQuantityPerLine)
                {
                    errors[$"lines[{i}].quantity"] = [$"quantity must be between 1 and {MaxQuantityPerLine}."];
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }
}
