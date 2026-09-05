namespace Koleksi.Application.Common;

/// <summary>
/// A page of results plus the cursor needed to fetch the next one.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The results, in query order.</param>
/// <param name="NextCursor">
/// Opaque token for the following page, or <c>null</c> when this is the last page.
/// </param>
/// <param name="TotalCount">
/// Approximate total, for the "Showing 1–12 of 4.128 items" line. Approximate because
/// DynamoDB cannot count a filtered set cheaply; it is served from maintained counters.
/// </param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, long TotalCount)
{
    public bool HasMore => NextCursor is not null;
}

/// <summary>
/// A page addressed by number rather than by cursor.
/// </summary>
/// <remarks>
/// <para>
/// Browse uses numbered pages, not infinite scroll. The handoff is explicit about why:
/// collectors compare across tabs and return to a result position, and page numbers
/// are what make that position addressable in the URL and crawlable by search engines.
/// </para>
/// <para>
/// DynamoDB has no native offset paging, so deep pages are served through the search
/// projection rather than by scanning. See <c>docs/data-model.md</c>.
/// </para>
/// </remarks>
/// <param name="Items">The results for this page.</param>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="PageSize">Items per page.</param>
/// <param name="TotalCount">Total matching items, for the result count and page count.</param>
public sealed record NumberedPage<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    long TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPrevious => PageNumber > 1;

    public bool HasNext => PageNumber < TotalPages;
}

/// <summary>Factory helpers for <see cref="NumberedPage{T}"/>.</summary>
/// <remarks>
/// Lives on a non-generic type so callers write <c>NumberedPage.Empty&lt;Listing&gt;(12)</c>
/// rather than reaching for a static member through a constructed generic type.
/// </remarks>
public static class NumberedPage
{
    public static NumberedPage<T> Empty<T>(int pageSize) => new([], 1, pageSize, 0);
}
