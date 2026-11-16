using InventoryAnalytics.Core.Errors;

namespace InventoryAnalytics.Api.ErrorHandling;

/// <summary>
/// Converts expected <see cref="DomainException"/>s into RFC 7807 ProblemDetails responses.
/// Handling them here (instead of in the global exception handler) keeps client errors out of
/// the error log; anything else still bubbles up to <c>UseExceptionHandler</c> as a 500.
/// </summary>
public sealed class DomainExceptionFilter(ILogger<DomainExceptionFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (DomainException ex)
        {
            logger.LogInformation("Request {Method} {Path} rejected: {ErrorType} - {Message}",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path, ex.GetType().Name, ex.Message);
            return ToProblem(ex);
        }
    }

    public static IResult ToProblem(DomainException exception) => exception switch
    {
        RequestValidationException validation => TypedResults.ValidationProblem(
            validation.Errors.ToDictionary(e => e.Key, e => e.Value),
            title: "One or more validation errors occurred."),
        NotFoundException => TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Resource not found",
            detail: exception.Message),
        InsufficientStockException => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Insufficient stock",
            detail: exception.Message,
            extensions: new Dictionary<string, object?> { ["errorCode"] = "insufficient_stock" }),
        _ => TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "The request could not be processed",
            detail: exception.Message),
    };
}
