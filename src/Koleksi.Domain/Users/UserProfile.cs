using Koleksi.Domain.Common;

namespace Koleksi.Domain.Users;

/// <summary>What stage identity verification is at.</summary>
/// <remarks>
/// Manual review, not an automated KYC integration — out of scope for this pass. An
/// operator moves an account from <see cref="Pending"/> to <see cref="Verified"/>; this
/// domain only knows the three states, not how the middle one resolves.
/// </remarks>
public enum IdVerificationStatus
{
    None = 0,
    Pending = 1,
    Verified = 2,
}

/// <summary>
/// A buyer or seller's account details: identity, shipping addresses, and the two
/// verifications the marketplace gates buying and selling on.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the Cognito subject, not a separately minted id — a profile has no identity
/// apart from the account it belongs to, and there is exactly one per account.
/// </para>
/// <para>
/// Deliberately does not hold the handle/shop name or the email address. Both live on the
/// Cognito user pool and are read from the validated token via <c>ICurrentUser</c>; a copy
/// here would be a second place either could drift out of sync with sign-in.
/// </para>
/// </remarks>
public sealed class UserProfile
{
    public const int MaxAddresses = 10;

    private readonly List<Address> _addresses = [];

    private UserProfile(string id, DateTimeOffset createdAt)
    {
        Id = id;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        FullName = string.Empty;
        IdVerificationStatus = IdVerificationStatus.None;
    }

    /// <summary>The Cognito subject this profile belongs to.</summary>
    public string Id { get; }

    public string FullName { get; private set; }

    public string? Phone { get; private set; }

    public bool PhoneVerified { get; private set; }

    public string? AvatarKey { get; private set; }

    public IdVerificationStatus IdVerificationStatus { get; private set; }

    public BankAccount? BankAccount { get; private set; }

    public IReadOnlyList<Address> Addresses => _addresses;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic-concurrency token. See <c>Listing.Version</c> for why this exists.</summary>
    public long Version { get; private set; }

    /// <summary>A blank profile for an account that has never saved one.</summary>
    public static UserProfile CreateEmpty(string userId, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(clock);

        return new UserProfile(userId, clock.UtcNow);
    }

    /// <summary>Reconstructs a profile from storage without re-running creation rules.</summary>
    public static UserProfile Rehydrate(
        string id,
        string fullName,
        string? phone,
        bool phoneVerified,
        string? avatarKey,
        IdVerificationStatus idVerificationStatus,
        BankAccount? bankAccount,
        IEnumerable<Address> addresses,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        var profile = new UserProfile(id, createdAt)
        {
            FullName = fullName,
            Phone = phone,
            PhoneVerified = phoneVerified,
            AvatarKey = avatarKey,
            IdVerificationStatus = idVerificationStatus,
            BankAccount = bankAccount,
            UpdatedAt = updatedAt,
            Version = version,
        };

        profile._addresses.AddRange(addresses);
        return profile;
    }

    /// <summary>
    /// Applies a save-profile submission: the display name and the full address list.
    /// </summary>
    /// <remarks>
    /// The address list is replaced wholesale rather than diffed. The profile screen edits
    /// addresses in a local list and submits the result as one save, exactly like the name
    /// field beside it — there is no independent "add one address" request to reconcile
    /// against, so there is nothing a diff would buy that a replace does not already give.
    /// </remarks>
    public void UpdateDetails(string fullName, IReadOnlyList<Address> addresses, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(clock);

        if (addresses.Count > MaxAddresses)
        {
            throw DomainException.For(
                "profile.too_many_addresses", $"A profile may hold at most {MaxAddresses} addresses.");
        }

        if (addresses.Count(a => a.IsDefault) > 1)
        {
            throw DomainException.For("profile.multiple_default_addresses", "Only one address may be default.");
        }

        FullName = fullName.Trim();
        _addresses.Clear();
        _addresses.AddRange(addresses);
        Touch(clock);
    }

    public void SetAvatar(string? avatarKey, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        AvatarKey = string.IsNullOrWhiteSpace(avatarKey) ? null : avatarKey;
        Touch(clock);
    }

    /// <summary>
    /// Records verification as complete, after the OTP flow confirms it.
    /// </summary>
    /// <param name="phone">
    /// The number that was texted, or <c>null</c> when the reader had not entered one yet
    /// and verification went to the account's own email instead. A real destination that
    /// answers is what proves reachability — it does not have to specifically be an
    /// SMS-capable one — so <see cref="PhoneVerified"/> still becomes <c>true</c> either
    /// way; only <see cref="Phone"/> itself is left unset when there was no number to
    /// record.
    /// </param>
    /// <remarks>
    /// The only way <see cref="PhoneVerified"/> ever becomes <c>true</c>. There is
    /// deliberately no "set phone" method that skips verification — an unverified number on
    /// file is worse than none, because shipping and disputes both treat its presence as a
    /// promise that it works.
    /// </remarks>
    public void MarkPhoneVerified(string? phone, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!string.IsNullOrWhiteSpace(phone))
        {
            Phone = phone.Trim();
        }

        PhoneVerified = true;
        Touch(clock);
    }

    public void SetBankAccount(BankAccount account, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        BankAccount = account;
        Touch(clock);
    }

    /// <summary>
    /// Moves identity verification to <see cref="IdVerificationStatus.Pending"/> once the
    /// ID photo is uploaded.
    /// </summary>
    /// <remarks>
    /// Re-submitting from <see cref="IdVerificationStatus.Pending"/> is allowed — a second
    /// upload after a blurry first attempt should not be blocked — but re-submitting once
    /// already <see cref="IdVerificationStatus.Verified"/> is refused: a verified account
    /// should not fall back to "pending" through a page nobody but the account holder can
    /// reach, since that is also the account holder an attacker took over would use to hide
    /// the takeover from a reviewer.
    /// </remarks>
    public void SubmitIdVerification(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (IdVerificationStatus == IdVerificationStatus.Verified)
        {
            throw DomainException.For(
                "profile.already_verified", "Identity is already verified and cannot be resubmitted.");
        }

        IdVerificationStatus = IdVerificationStatus.Pending;
        Touch(clock);
    }

    private void Touch(IClock clock)
    {
        UpdatedAt = clock.UtcNow;
        Version++;
    }
}
