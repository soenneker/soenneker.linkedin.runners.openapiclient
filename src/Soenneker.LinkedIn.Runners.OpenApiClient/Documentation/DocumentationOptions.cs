using System;
using System.Collections.Generic;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public sealed class DocumentationOptions
{
    public string[] SeedUrls { get; set; } = [];
    public string? MarketingVersion { get; set; }
    public bool FollowLinks { get; set; } = true;
    public int MaxPages { get; set; } = 2000;
    public int NavigationTimeoutMs { get; set; } = 60000;
    public int DelayMs { get; set; } = 250;
    public string? OutputDirectory { get; set; }
    public string? BaselineDirectory { get; set; }
    public bool SpecOnly { get; set; }
    public bool FailOnUnresolvedSchemas { get; set; }
    public string? PostmanDirectory { get; set; }

    public void Validate()
    {
        if (MaxPages < 1 || NavigationTimeoutMs < 1 || DelayMs < 0)
            throw new ArgumentException("Documentation requires positive page/timeout limits and a nonnegative delay.");
        if (MarketingVersion != null && !System.Text.RegularExpressions.Regex.IsMatch(MarketingVersion, @"^\d{4}(0[1-9]|1[0-2])$"))
            throw new ArgumentException("Documentation:MarketingVersion must use YYYYMM, or be omitted to use Learn's current version.");
        foreach (string url in SeedUrls)
            if (DocumentationUrl.Normalize(url, MarketingVersion) == null)
                throw new ArgumentException($"Not an official LinkedIn Learn article: {url}");
    }
}

public static class DocumentationUrl
{
    public static string? Normalize(string url, string? marketingVersion = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            uri.Host is not ("learn.microsoft.com" or "docs.microsoft.com"))
            return null;
        string path = uri.AbsolutePath.StartsWith("/linkedin/", StringComparison.Ordinal) ? "/en-us" + uri.AbsolutePath : uri.AbsolutePath;
        if (!path.StartsWith("/en-us/linkedin/", StringComparison.Ordinal) || System.IO.Path.HasExtension(path))
            return null;
        var builder = new UriBuilder(uri) { Host = "learn.microsoft.com", Path = path, Fragment = "", Query = "" };
        if (marketingVersion != null && path.StartsWith("/en-us/linkedin/marketing/", StringComparison.Ordinal))
            builder.Query = $"view=li-lms-{marketingVersion[..4]}-{marketingVersion[4..]}&preserve-view=true";
        return builder.Uri.AbsoluteUri;
    }
}

public sealed class DocumentationPage
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Error { get; set; }
    public List<string> Links { get; set; } = [];
    public List<DocumentationBlock> Blocks { get; set; } = [];
}

public sealed class DocumentationBlock
{
    public string Kind { get; set; } = "";
    public string Section { get; set; } = "";
    public string Anchor { get; set; } = "";
    public List<string> SectionPath { get; set; } = [];
    public string Text { get; set; } = "";
    public List<string> Headers { get; set; } = [];
    public List<List<DocumentationCell>> Rows { get; set; } = [];
}

public sealed class DocumentationCell
{
    public string Text { get; set; } = "";
    public List<string> Links { get; set; } = [];
    public List<string> Values { get; set; } = [];
}

public sealed record DocumentationIssue(string Url, string Section, string Message);

public sealed class DocumentationReviewRequiredException(string message) : InvalidOperationException(message);
