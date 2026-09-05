namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// Shared behaviour for the in-memory adapters the test host runs on.
/// </summary>
/// <remarks>
/// The one thing every fake needs beyond storing data is a way to fail on demand. The
/// error pipeline's job is to turn an adapter's exception into an opaque 500 that leaks
/// nothing, and that is not testable without an adapter willing to throw.
/// </remarks>
public abstract class FakeAdapter
{
    /// <summary>Set to make every subsequent call on this adapter throw.</summary>
    public Exception? FailWith { get; set; }

    protected void ThrowIfFailing()
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }
    }
}
