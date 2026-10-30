using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace KirillMaster.QualityGates.Results.AspNetCore;

public class ProblemExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public virtual async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = StatusCodes.Status500InternalServerError, Title = "An unexpected error occurred." },
        });
    }
}
