using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// Shard assignment for the live-auction index.
/// </summary>
/// <remarks>
/// <para>
/// This is the one function in the codebase whose <b>exact output</b> is part of the
/// stored data. A listing's index entry lives in the partition this returns; change the
/// answer and every entry written before the change is orphaned, so the auction appears
/// twice in the close sweep — once at its real close time, once at a stale one — and the
/// sweep closes it at the wrong price.
/// </para>
/// <para>
/// The values below are therefore pinned, not derived. A test that recomputed the hash
/// alongside the implementation would agree with any change, including the one that
/// matters.
/// </para>
/// </remarks>
public sealed class AuctionShardingTests
{
    /// <summary>
    /// The published FNV-1a 32-bit vector for <c>"a"</c> is <c>0xE40C292C</c>.
    /// </summary>
    /// <remarks>
    /// Pinning a canonical vector, rather than only our own ids, is what says which
    /// algorithm this is. Swapping in a different stable hash would still be
    /// deterministic — and would still orphan every index entry ever written.
    /// </remarks>
    [Fact]
    public void The_hash_is_canonical_fnv_1a()
    {
        const uint FnvOfLetterA = 0xE40C292C;

        DynamoTable.ShardOf("a").ShouldBe((int)(FnvOfLetterA % DynamoTable.AuctionShardCount));
    }

    [Theory]
    [InlineData("01JQ8ZC4K7N2M5V9XR3P6TW0YB", 0)]
    [InlineData("01JQ8ZC4K7N2M5V9XR3P6TW0YC", 3)]
    [InlineData("01JQ8ZC4K7N2M5V9XR3P6TW0YD", 2)]
    [InlineData("01JQ8ZC4K7N2M5V9XR3P6TW0YE", 1)]
    public void A_listing_lands_in_the_same_shard_forever(string listingId, int expected)
    {
        // Golden values, captured once. If one of these changes, the change is wrong until
        // someone has written a migration that rewrites every live auction's index entry.
        DynamoTable.ShardOf(listingId).ShouldBe(expected);
    }

    [Fact]
    public void Every_shard_is_within_the_fan_out_the_sweep_queries()
    {
        // The close sweep queries exactly AuctionShardCount partitions. A shard outside
        // that range is an auction that never closes and never appears anywhere.
        for (var i = 0; i < 500; i++)
        {
            var shard = DynamoTable.ShardOf($"01JQ8ZC4K7N2M5V9XR3P6TW{i:D3}");

            shard.ShouldBeGreaterThanOrEqualTo(0);
            shard.ShouldBeLessThan(DynamoTable.AuctionShardCount);
        }
    }

    [Fact]
    public void The_shards_are_used_rather_than_being_a_constant_in_disguise()
    {
        // Four shards exist to spread the write load of bidding. A hash that always
        // answered zero would pass every other test here and none of the intent.
        var used = new HashSet<int>();
        for (var i = 0; i < 500; i++)
        {
            used.Add(DynamoTable.ShardOf($"01JQ8ZC4K7N2M5V9XR3P6TW{i:D3}"));
        }

        used.Count.ShouldBe(DynamoTable.AuctionShardCount);
    }

    [Fact]
    public void A_shards_partition_key_names_its_status_and_number()
    {
        DynamoTable.AuctionEndPkValue("Live", 3).ShouldBe("AUCTION#Live#3");
    }
}
