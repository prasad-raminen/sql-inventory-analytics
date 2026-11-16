using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryAnalytics.Api.ErrorHandling;

/// <summary>
/// Minimal APIs throw <see cref="BadHttpRequestException"/> for unbindable input (e.g. <c>?top=abc</c>
/// or malformed JSON) in Development. Without this handler those would surface as 500s; here they
/// keep their intended 4xx status and get a ProblemDetails body.
/// </summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "The request is malformed.",
                Detail = badRequest.Message,
            },
        });
    }
}
