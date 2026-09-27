using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public sealed class LearnDocumentationSource(ILogger<LearnDocumentationSource> logger) : ILearnDocumentationSource
{
    public async Task<IReadOnlyList<DocumentationPage>> Read(DocumentationOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        using Stream scriptStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Soenneker.LinkedIn.Runners.OpenApiClient.Documentation.ExtractArticle.js")!;
        using var reader = new StreamReader(scriptStream);
        string script = await reader.ReadToEndAsync(cancellationToken);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync(new() { Locale = "en-US" });
        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(options.NavigationTimeoutMs);
        var pending = new SortedSet<string>(options.SeedUrls.Select(url => DocumentationUrl.Normalize(url, options.MarketingVersion)!), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pages = new SortedDictionary<string, DocumentationPage>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string url = pending.Min!;
            pending.Remove(url);
            if (!visited.Add(url)) continue;
            if (visited.Count > options.MaxPages)
                throw new InvalidOperationException($"Documentation exceeded the {options.MaxPages} page limit. Refusing a partial crawl; increase Documentation:MaxPages or narrow SeedUrls.");
            logger.LogInformation("Reading LinkedIn documentation ({Count}): {Url}", visited.Count, url);
            DocumentationPage article = await ReadPage(page, url, script, options, cancellationToken);
            string canonical = DocumentationUrl.Normalize(article.Url, options.MarketingVersion)
                ?? throw new InvalidOperationException($"Documentation redirected outside official LinkedIn Learn articles: {url}");
            article.Url = canonical;
            visited.Add(canonical);
            article.Links = article.Links.Select(link => DocumentationUrl.Normalize(link, options.MarketingVersion))
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            pages[canonical] = article;
            if (options.FollowLinks)
                foreach (string link in article.Links)
                    if (!visited.Contains(link)) pending.Add(link);
            if (options.DelayMs > 0) await Task.Delay(options.DelayMs, cancellationToken);
        }
        return pages.Values.ToList();
    }

    private static async Task<DocumentationPage> ReadPage(IPage page, string url, string script, DocumentationOptions options, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                IResponse? response = await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = options.NavigationTimeoutMs }).WaitAsync(cancellationToken);
                if (response == null || !response.Ok)
                    throw new InvalidOperationException($"Documentation returned HTTP {response?.Status}: {url}");
                await page.Locator("main h1").WaitForAsync().WaitAsync(cancellationToken);
                DocumentationPage result = await page.EvaluateAsync<DocumentationPage>(script).WaitAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(result.Title) || result.Blocks.Count == 0 ||
                    result.Title.Contains("404", StringComparison.OrdinalIgnoreCase) || result.Title.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"No readable article content at {url}. Refusing to replace the previous specification.");
                return result;
            }
            catch (Exception ex) when (attempt < 3 && ex is not OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
    }
}
