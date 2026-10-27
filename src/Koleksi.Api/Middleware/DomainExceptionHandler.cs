using Koleksi.Api.Security;
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
    private const string RetryAfterSeconds = "2";

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

        if (exception is RequestInProgressException)
        {
            // Seconds. The first request is normally done well inside this.
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
        }

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

            DomainException domain when domain.Code is "account.banned" =>
                (StatusCodes.Status403Forbidden, "Account banned", domain.Message, domain.Code),

            DomainException domain when domain.Code.EndsWith(".forbidden", StringComparison.Ordinal) =>
                (StatusCodes.Status403Forbidden, "Forbidden", domain.Message, domain.Code),

            // The caller asked too often. 429 rather than 409: nothing is wrong with the state of
            // anything, they simply have to wait.
            DomainException domain when domain.Code is "profile.otp_cooldown" or "profile.otp_limit" =>
                (StatusCodes.Status429TooManyRequests, "Too many requests", domain.Message, domain.Code),

            DomainException domain =>
                (StatusCodes.Status409Conflict, "Request conflicts with current state", domain.Message, domain.Code),

            // The same Idempotency-Key is still being processed by an earlier request. 409, not
            // 503: nothing is down, running this one would duplicate the first, and the retry
            // (after Retry-After) replays the first request's recorded result.
            RequestInProgressException =>
                (StatusCodes.Status409Conflict, "Request in progress",
                    "A request with the same Idempotency-Key is still being processed. Retry in a moment.",
                    "request.in_progress"),

            // Retries were exhausted under contention. 503 with a retry hint is honest:
            // the request was well-formed and may well succeed shortly.
            ConcurrencyConflictException =>
                (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable",
                    "The resource is being updated by another request. Please retry.", "concurrency.conflict"),

            // The key is already spent on a different request: replaying the first answer would be wrong (QA P-06).
            IdempotencyKeyReusedException =>
                (StatusCodes.Status422UnprocessableEntity, "Idempotency key reused",
                    "This Idempotency-Key was already used for a different request. Use a new key.",
                    "idempotency.key_reused"),

            // A page cursor the store could not resume from: forged, edited, or taken from another list.
            InvalidCursorException =>
                (StatusCodes.Status400BadRequest, "Invalid request", "The page cursor is not valid.",
                    "request.invalid_cursor"),

            // Argument guards indicate a malformed request that slipped past validation.
            // The message names a parameter, never a value, so nothing is echoed back.
            ArgumentException =>
                (StatusCodes.Status400BadRequest, "Invalid request", "One or more values were not valid.",
                    "request.invalid"),

            // A body past the cap (see RequestBodyLimitMiddleware) is its own answer, not a malformed one.
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (StatusCodes.Status413PayloadTooLarge, "Request body too large", "The request body is too large.",
                    RequestBodyTooLargeException.Code),

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
