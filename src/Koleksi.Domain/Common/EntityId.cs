namespace Koleksi.Domain.Common;

/// <summary>
/// Generates and validates the public identifiers used for every aggregate.
/// </summary>
/// <remarks>
/// <para>
/// Ids are UUID version 7 values rendered in Crockford base-32: 26 characters,
/// case-insensitive, with no vowels or ambiguous glyphs (<c>I</c>, <c>L</c>,
/// <c>O</c>, <c>U</c>). Two properties matter here.
/// </para>
/// <para>
/// <b>Time-ordered.</b> UUIDv7 embeds a millisecond timestamp in its leading bits,
/// so ids sort chronologically as plain strings. DynamoDB sort keys are compared
/// bytewise, which means "the newest bids on this auction" or "this seller's
/// recent orders" is a range query on the id alone, with no secondary index.
/// </para>
/// <para>
/// <b>Unguessable.</b> The remaining 74 bits come from a cryptographic RNG, so ids
/// cannot be enumerated. This is defence in depth, not an access control: every
/// endpoint still authorises the caller against the resource. An id is a name, never
/// a capability.
/// </para>
/// <para>
/// <see cref="Guid.CreateVersion7()"/> is used rather than a hand-rolled or
/// third-party ULID so the generation path stays inside the platform's audited code.
/// </para>
/// </remarks>
public static class EntityId
{
    /// <summary>Crockford base-32 alphabet: digits, then letters excluding I, L, O and U.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>16 bytes encode to 26 base-32 characters (130 bits of capacity, 128 used).</summary>
    public const int Length = 26;

    /// <summary>Creates a new time-ordered identifier.</summary>
    public static string New() => Encode(Guid.CreateVersion7());

    /// <summary>Creates a new identifier stamped with an explicit timestamp.</summary>
    /// <remarks>Lets tests produce deterministic, ordered ids without waiting on the clock.</remarks>
    public static string New(DateTimeOffset timestamp) => Encode(Guid.CreateVersion7(timestamp));

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is shaped like an id.
    /// </summary>
    /// <remarks>
    /// Callers use this to reject malformed route segments before they reach the data
    /// store, so a hostile path cannot turn into a query. It proves the shape only —
    /// it says nothing about whether the entity exists or the caller may see it.
    /// </remarks>
    public static bool IsValid(string? value)
    {
        if (value is not { Length: Length })
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (DecodeChar(ch) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Normalises an id to its canonical uppercase form for storage and comparison.</summary>
    /// <remarks>
    /// Crockford base-32 is case-insensitive, so a link typed in lowercase must resolve
    /// to the same record. Normalising at the edge keeps a single canonical key in
    /// DynamoDB, whose comparisons are case-sensitive.
    /// </remarks>
    public static string Normalise(string value) => value.ToUpperInvariant();

    private static string Encode(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        // Big-endian preserves UUIDv7's timestamp-first byte order, which is what makes
        // the encoded string sort chronologically. Guid's default layout does not.
        if (!id.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            throw new InvalidOperationException("Failed to serialise identifier.");
        }

        Span<char> chars = stackalloc char[Length];

        // Walk the 128 bits from the most significant end, five at a time. The final
        // character carries the two remaining bits, left-aligned.
        for (var i = 0; i < Length; i++)
        {
            var bitOffset = i * 5;
            var byteIndex = bitOffset >> 3;
            var bitInByte = bitOffset & 7;

            int window = bytes[byteIndex] << 8;
            if (byteIndex + 1 < bytes.Length)
            {
                window |= bytes[byteIndex + 1];
            }

            chars[i] = Alphabet[(window >> (11 - bitInByte)) & 0x1F];
        }

        return new string(chars);
    }

    private static int DecodeChar(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        // Crockford treats O as 0 and I/L as 1 when reading, tolerating transcription
        // slips in ids that were read aloud or retyped from a screen.
        'O' or 'o' => 0,
        'I' or 'i' or 'L' or 'l' => 1,
        >= 'A' and <= 'Z' => Alphabet.IndexOf(ch),
        >= 'a' and <= 'z' => Alphabet.IndexOf(char.ToUpperInvariant(ch)),
        _ => -1,
    };
}
