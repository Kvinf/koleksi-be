using Koleksi.AdminApi.Security;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Koleksi.AdminApi.Middleware;

/// <summary>
/// Turns exceptions into problem responses with a stable <c>code</c>.
/// </summary>
/// <remarks>
/// A <see cref="DomainException"/> is a rule the caller broke, so its code is safe to return. Anything else is our
/// fault: the caller gets a correlation id and nothing more, and the detail goes to the log. Every refusal an admin
/// endpoint makes is a coded error that the web tier can show in both languages.
/// </remarks>
public sealed partial class AdminExceptionHandler(
    ILogger<AdminExceptionHandler> logger,
    IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var (status, title, detail, code) = Classify(exception);
        var correlationId = httpContext.TraceIdentifier;

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, correlationId, exception);
        }
        else
        {
            LogRejected(logger, code ?? "unknown", correlationId);
        }

        httpContext.Response.StatusCode = status;
        if (exception is RequestInProgressException)
        {
            httpContext.Response.Headers.RetryAfter = "2";
        }

        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = httpContext.Request.Path };
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        problem.Extensions["correlationId"] = correlationId;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    private static (int Status, string Title, string Detail, string? Code) Classify(Exception exception) =>
        exception switch
        {
            DomainException d when d.Code.StartsWith("auth.", StringComparison.Ordinal) =>
                (StatusCodes.Status401Unauthorized, "Authentication required", d.Message, d.Code),

            DomainException d when d.Code is "admin.cannot_act_on_self" or "admin.cannot_act_on_staff"
                || d.Code.EndsWith(".forbidden", StringComparison.Ordinal) =>
                (StatusCodes.Status403Forbidden, "Forbidden", d.Message, d.Code),

            // A record that does not exist answers 404 WITH its code (dispute.not_found, holiday.not_found). A 404 with no code is the web
            // tier's way of knowing a route is not built, so the code is what tells the two apart.
            DomainException d when d.Code.EndsWith(".not_found", StringComparison.Ordinal) =>
                (StatusCodes.Status404NotFound, "Not found", d.Message, d.Code),

            DomainException d when d.Code.EndsWith("_invalid", StringComparison.Ordinal) =>
                (StatusCodes.Status400BadRequest, "Invalid request", d.Message, d.Code),

            DomainException d =>
                (StatusCodes.Status409Conflict, "Request conflicts with current state", d.Message, d.Code),

            RequestInProgressException =>
                (StatusCodes.Status409Conflict, "Request in progress",
                    "A request with the same Idempotency-Key is still being processed. Retry in a moment.", "request.in_progress"),

            ConcurrencyConflictException =>
                (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable",
                    "The resource is being updated by another request. Please retry.", "concurrency.conflict"),

            ArgumentException =>
                (StatusCodes.Status400BadRequest, "Invalid request", "One or more values were not valid.", "request.invalid"),

            // A body past the cap (see AdminBodyLimitMiddleware) is its own answer, not a malformed one.
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (StatusCodes.Status413PayloadTooLarge, "Request body too large", "The request body is too large.", AdminBodyTooLargeException.Code),

            BadHttpRequestException =>
                (StatusCodes.Status400BadRequest, "Invalid request", "The request could not be read.", "request.malformed"),

            OperationCanceledException =>
                (499, "Request cancelled", "The request was cancelled.", null),

            _ => (StatusCodes.Status500InternalServerError, "Unexpected error",
                "Something went wrong. Quote the correlation id if you contact the owner.", "server.error"),
        };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on admin request {CorrelationId}.")]
    private static partial void LogUnhandled(ILogger logger, string correlationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin request {CorrelationId} refused with {Code}.")]
    private static partial void LogRejected(ILogger logger, string code, string correlationId);
}
