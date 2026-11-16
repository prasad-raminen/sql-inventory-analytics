namespace InventoryAnalytics.Core.Errors;

/// <summary>Base type for expected, client-facing failures. The API maps each subtype to an HTTP status.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A referenced entity (customer, warehouse, product, order) does not exist. Maps to 404.</summary>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public static NotFoundException For(string entity, int id) => new($"{entity} {id} was not found.");
}

/// <summary>Not enough stock to fulfil an order; nothing was persisted. Maps to 409.</summary>
public sealed class InsufficientStockException : DomainException
{
    public InsufficientStockException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The request is syntactically valid but breaks a business rule. Maps to 400.</summary>
public sealed class RequestValidationException : DomainException
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors, Exception? innerException = null)
        : base("One or more validation errors occurred.", innerException)
    {
        Errors = errors;
    }

    public RequestValidationException(string field, string error, Exception? innerException = null)
        : this(new Dictionary<string, string[]> { [field] = [error] }, innerException)
    {
    }

    /// <summary>Field name to error messages, in the shape of ASP.NET Core's ValidationProblemDetails.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
