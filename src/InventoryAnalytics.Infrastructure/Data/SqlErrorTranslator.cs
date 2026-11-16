using InventoryAnalytics.Core.Errors;

namespace InventoryAnalytics.Infrastructure.Data;

/// <summary>
/// Maps the stored procedures' THROW error numbers (the database's error contract) to domain
/// exceptions. Kept independent of <c>SqlException</c> so the mapping is unit-testable.
/// </summary>
public static class SqlErrorTranslator
{
    public const int CustomerNotFound = 50001;
    public const int WarehouseNotFound = 50002;
    public const int EmptyOrder = 50003;
    public const int ProductNotFound = 50004;
    public const int InsufficientStock = 50010;
    public const int InvalidTop = 50020;
    public const int InvalidDateRange = 50021;

    /// <summary>Returns the domain exception for a known error number, or null if the error is unexpected.</summary>
    public static DomainException? Translate(int errorNumber, string message, Exception? innerException = null) =>
        errorNumber switch
        {
            CustomerNotFound or WarehouseNotFound or ProductNotFound
                => new NotFoundException(message, innerException),
            InsufficientStock
                => new InsufficientStockException(message, innerException),
            EmptyOrder
                => new RequestValidationException("lines", message, innerException),
            InvalidTop
                => new RequestValidationException("top", message, innerException),
            InvalidDateRange
                => new RequestValidationException("from", message, innerException),
            _ => null,
        };
}
