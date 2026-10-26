namespace OneTimeRedirect.Web;

/// <summary>
/// Recognises requests that are certainly link previews or browser prefetches, never a person opening the link.
/// A match only ever makes the server answer "not consumed, no Location". No match does NOT mean "human":
/// unrecognised scanners that look like browsers are still a residual risk (see README).
/// </summary>
public static class PreviewDetector
{
    // Documented preview/unfurl crawlers. Deliberately explicit: generic tokens such as "bot" or "preview"
    // are not used because they can appear in real browser User-Agents (e.g. device brand names).
    // Apple Messages previews present a UA containing "Twitterbot" and "facebookexternalhit".
    private static readonly string[] PreviewAgents =
    [
        "TelegramBot", "Twitterbot", "facebookexternalhit", "Facebot", "WhatsApp", "Slackbot", "Slack-ImgProxy",
        "Discordbot", "LinkedInBot", "SkypeUriPreview", "MicrosoftPreview", "BingPreview", "vkShare",
        "Pinterestbot", "redditbot", "Applebot", "Mattermost-Bot"
    ];

    /// <summary>Returns a short reason label, or null when the request is not recognised as a preview.</summary>
    public static string? Classify(IHeaderDictionary headers)
    {
        // Sec-Purpose is a forbidden header name: page scripts cannot set it, browsers do.
        if (headers["Sec-Purpose"].ToString().Contains("prefetch", StringComparison.OrdinalIgnoreCase) ||
            headers["Purpose"].ToString().Equals("prefetch", StringComparison.OrdinalIgnoreCase) ||
            headers["X-Moz"].ToString().Equals("prefetch", StringComparison.OrdinalIgnoreCase))
            return "prefetch";

        var userAgent = headers.UserAgent.ToString();
        foreach (var agent in PreviewAgents)
            if (userAgent.Contains(agent, StringComparison.OrdinalIgnoreCase))
                return agent;
        return null;
    }
}
