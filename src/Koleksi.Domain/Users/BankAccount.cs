using Koleksi.Domain.Common;

namespace Koleksi.Domain.Users;

/// <summary>The account a seller's payouts are sent to.</summary>
/// <remarks>
/// The full <see cref="AccountNumber"/> is persisted — a payout has to be sent somewhere —
/// but nothing outside this type or its repository is meant to see it in full.
/// <see cref="Masked"/> is what every API response and the profile screen actually render.
/// </remarks>
public readonly record struct BankAccount(string BankName, string AccountNumber, string AccountHolderName)
{
    public static BankAccount Create(string bankName, string accountNumber, string accountHolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountHolderName);

        var trimmedNumber = accountNumber.Trim();
        if (trimmedNumber.Length is < 6 or > 20 || !trimmedNumber.All(char.IsAsciiDigit))
        {
            throw DomainException.For(
                "profile.bank_account_invalid", "Account number must be 6 to 20 digits.");
        }

        return new BankAccount(bankName.Trim(), trimmedNumber, accountHolderName.Trim());
    }

    /// <summary>Every digit but the last four, replaced with a bullet.</summary>
    public string Masked
    {
        get
        {
            var visible = Math.Min(4, AccountNumber.Length);
            return new string('•', AccountNumber.Length - visible) + AccountNumber[^visible..];
        }
    }
}
