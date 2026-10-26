namespace OneTimeRedirect.Web;

/// <summary>Validated PUBLIC_BASE_URL (scheme and authority only), or null to use the request host (development).</summary>
public sealed record PublicBaseUrl(string? Value);

/// <summary>Creates a link and builds its public URL; the raw token exists only in the returned value and, encrypted, in the database.</summary>
public sealed class LinkPublisher(LinkStore store, PublicBaseUrl publicBase, LinkTokenProtector protector)
{
    /// <summary>Returns an error message, or null when the input is acceptable.</summary>
    public static string? Validate(string? target, DateTimeOffset? expiresAt, string? description, out string? normalized)
    {
        if (!UrlValidator.TryValidate(target, out normalized))
            return "Only absolute HTTP or HTTPS URLs without embedded credentials are allowed.";
        if (expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow) return "ExpiresAt must be in the future.";
        if (description?.Length > 500) return "Description is too long.";
        return null;
    }

    public string BuildLink(string token, HttpRequest request) =>
        $"{publicBase.Value ?? $"{request.Scheme}://{request.Host}"}/r/{token}";

    public async Task<(long Id, string Link)> CreateAsync(string target, DateTimeOffset? expiresAt,
        string? description, HttpRequest request, CancellationToken ct)
    {
        var token = TokenService.Generate();
        var id = await store.CreateAsync(TokenService.Hash(token), target, expiresAt, description, ct,
            encryptedToken: protector.Protect(token));
        return (id, BuildLink(token, request));
    }
}
