namespace Koleksi.Api.Endpoints;

/// <summary>Reads the raw bearer token off the request, the same way everywhere.</summary>
/// <remarks>
/// <see cref="ICurrentUser"/> is the sanctioned way to know who is calling — this exists
/// only for the rare case where a handler has to hand the token itself back to Cognito,
/// e.g. <c>IIdentityService.GetEmailAsync</c>, which resolves an attribute the validated
/// claims never carry.
/// </remarks>
internal static class BearerToken
{
    private const string Scheme = "Bearer ";

    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..]
            : null;
    }
}
