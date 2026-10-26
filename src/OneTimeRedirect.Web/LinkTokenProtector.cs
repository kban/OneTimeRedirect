using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OneTimeRedirect.Web;

/// <summary>
/// Encrypts one-time tokens so an authenticated administrator can copy a link again. Uses ASP.NET Core Data Protection
/// with its own purpose; the existing key ring is reused. Decryption is only called from authorized admin pages.
/// The SHA-256 hash stays the lookup key for redemption.
/// </summary>
public sealed class LinkTokenProtector(IDataProtectionProvider provider)
{
    public const string Purpose = "OneTimeRedirect.LinkTokens.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string token) => _protector.Protect(token);

    /// <summary>Returns the token, or null if the value is unreadable (wrong/missing keys, corruption, tampering).</summary>
    public string? TryUnprotect(string protectedValue, byte[] expectedHash)
    {
        try
        {
            var token = _protector.Unprotect(protectedValue);
            // The ciphertext must belong to this row: a swapped value would decrypt fine but hash differently.
            return TokenService.IsValid(token) && CryptographicOperations.FixedTimeEquals(TokenService.Hash(token), expectedHash)
                ? token : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
