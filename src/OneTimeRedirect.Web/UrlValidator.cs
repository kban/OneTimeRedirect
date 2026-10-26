using System.Globalization;
using System.Text.RegularExpressions;

namespace OneTimeRedirect.Web;

public static partial class UrlValidator
{
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*):(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@"^[0-9]{1,5}(?:[/?#]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PortPrefix();

    /// <summary>
    /// Validates and normalizes an administrator-supplied target. Only http and https are accepted; input without a
    /// scheme gets https://. Pure string/URI parsing: nothing is ever fetched.
    /// </summary>
    public static bool TryValidate(string? value, out string? normalized)
    {
        normalized = null;
        if (value is null) return false;
        foreach (var c in value)
        {
            // Control characters (CR, LF, TAB, NUL, ...), backslashes (parsed as "/" by browsers) and invisible
            // formatting characters (bidi overrides, zero-width) never belong in a stored redirect target.
            if (char.IsControl(c) || c == '\\') return false;
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
        }

        var input = value.Trim();
        if (input.Length is 0 or > 2048 || input.StartsWith("//", StringComparison.Ordinal)) return false;

        string candidate;
        var prefix = SchemePrefix().Match(input);
        if (!prefix.Success) candidate = "https://" + input;                       // ya.ru/path, [::1]:8080
        else
        {
            var rest = prefix.Groups[2].Value;
            if (rest.StartsWith("//", StringComparison.Ordinal))                    // explicit scheme
            {
                var scheme = prefix.Groups[1].Value;
                if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                    !scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
                candidate = input;
            }
            else if (PortPrefix().IsMatch(rest)) candidate = "https://" + input;   // host:port without a scheme
            else return false;                                                      // javascript:, data:, mailto:, http:/x ...
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.HostNameType == UriHostNameType.Unknown)
            return false;

        var result = uri.AbsoluteUri;
        // A bare origin the user typed without a trailing slash stays as typed (https://ya.ru, not https://ya.ru/).
        if (uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            !input.EndsWith('/') && result.EndsWith('/') && !result.Contains('?') && !result.Contains('#'))
            result = result[..^1];
        if (result.Length > 2048) return false;
        normalized = result;
        return true;
    }

    /// <summary>An explicit HTTPS origin such as <c>https://example.com</c> (public links are always HTTPS); a path, query or fragment is rejected.</summary>
    public static bool TryValidateBaseUrl(string? value, out string? origin)
    {
        origin = null;
        if (value is null || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryValidate(value, out var normalized)) return false;
        var uri = new Uri(normalized!);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
