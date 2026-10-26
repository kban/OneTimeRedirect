using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OneTimeRedirect.Web;

public static partial class TokenService
{
    [GeneratedRegex("^[A-Za-z0-9_-]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenFormat();

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool IsValid(string token) => TokenFormat().IsMatch(token);

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
}
