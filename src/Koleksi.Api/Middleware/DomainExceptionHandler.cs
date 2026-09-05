using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Koleksi.Api.Middleware;

/// <summary>
/// Turns exceptions into RFC 9457 problem responses.
/// </summary>
/// <remarks>
/// <para>
/// The split that matters: a <see cref="DomainException"/> is a rule the caller broke,
/// so its <see cref="DomainException.Code"/> is safe and useful to return. Anything else
/// is our fault, and its message may contain connection strings, key material, or
/// internal hostnames — so the caller gets a correlation id and nothing more, while the
/// detail goes to the log.
/// </para>
/// <para>
/// This is the last line of the pipeline. An endpoint should still validate its input
/// and return a considered response; this exists so that the one path nobody thought
/// about fails closed instead of leaking a stack trace.
/// </para>
/// </remarks>
public sealed partial class DomainExceptionHandler(
    ILogger<DomainExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var (status, title, detail, code) = Classify(exception);

        // The trace identifier ties a user's screenshot to the log entry without
        // exposing anything about what went wrong.
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

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
        };

        // The frontend switches on this to pick translated copy, so the wire contract is
        // the stable code rather than the English sentence beside it.
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        problem.Extensions["correlationId"] = correlationId;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    private static (int Status, string Title, string Detail, string? Code) Classify(Exception exception) =>
        exception switch
        {
            // A business rule refused the request. The message is written for a developer
            // but contains no internal detail, and the code carries the meaning.
            DomainException domain when domain.Code.StartsWith("auth.", StringComparison.Ordinal) =>
                (StatusCodes.Status401Unauthorized, "Authentication required", domain.Message, domain.Code),

            DomainException domain when domain.Code.EndsWith(".forbidden", StringComparison.Ordinal) =>
                (StatusCodes.Status403Forbidden, "Forbidden", domain.Message, domain.Code),

            DomainException domain =>
                (StatusCodes.Status409Conflict, "Request conflicts with current state", domain.Message, domain.Code),

            // Retries were exhausted under contention. 503 with a retry hint is honest:
            // the request was well-formed and may well succeed shortly.
            ConcurrencyConflictException =>
                (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable",
                    "The resource is being updated by another request. Please retry.", "concurrency.conflict"),

            // Argument guards indicate a malformed request that slipped past validation.
            // The message names a parameter, never a value, so nothing is echoed back.
            ArgumentException =>
                (StatusCodes.Status400BadRequest, "Invalid request", "One or more values were not valid.",
                    "request.invalid"),

            BadHttpRequestException =>
                (StatusCodes.Status400BadRequest, "Invalid request", "The request could not be read.",
                    "request.malformed"),

            OperationCanceledException =>
                (StatusCodesExtra.ClientClosedRequest, "Request cancelled", "The request was cancelled.", null),

            // Deliberately opaque. Whatever this is, the caller learns only that it failed.
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error",
                "Something went wrong. Quote the correlation id if you contact support.", "server.error"),
        };

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Unhandled exception. CorrelationId={CorrelationId}")]
    private static partial void LogUnhandled(ILogger logger, string correlationId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Request rejected with {Code}. CorrelationId={CorrelationId}")]
    private static partial void LogRejected(ILogger logger, string code, string correlationId);
}

/// <summary>Status codes ASP.NET Core does not define constants for.</summary>
internal static class StatusCodesExtra
{
    /// <summary>nginx's "client closed request", the conventional code for a cancelled request.</summary>
    public const int ClientClosedRequest = 499;
}
