using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public sealed class DocumentationSpecGenerator(ILearnDocumentationSource source, IPostmanSpecSource postmanSource, ILogger<DocumentationSpecGenerator> logger) : IDocumentationSpecGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<bool> Generate(string outputDirectory, DocumentationOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        string specPath = Path.Combine(outputDirectory, "openapi.json");
        string snapshotDirectory = Path.Combine(outputDirectory, "documentation");
        string manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
        string baseline = options.BaselineDirectory != null && File.Exists(Path.Combine(options.BaselineDirectory, "documentation", "manifest.json"))
            ? options.BaselineDirectory : outputDirectory;
        string baselineManifest = Path.Combine(baseline, "documentation", "manifest.json");
        string baselineSpec = Path.Combine(baseline, "openapi.json");
        JsonObject? previousManifest = File.Exists(baselineManifest) ? JsonNode.Parse(await File.ReadAllTextAsync(baselineManifest, cancellationToken))!.AsObject() : null;
        JsonObject? previousSpec = File.Exists(baselineSpec) ? JsonNode.Parse(await File.ReadAllTextAsync(baselineSpec, cancellationToken))!.AsObject() : null;
        JsonObject? publishedSpec = File.Exists(specPath) ? JsonNode.Parse(await File.ReadAllTextAsync(specPath, cancellationToken))!.AsObject() : null;
        IReadOnlyList<DocumentationPage> pages;
        PostmanBuild postman;
        try
        {
            postman = await postmanSource.Read(outputDirectory, options, cancellationToken);
            pages = await source.Read(options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Directory.CreateDirectory(snapshotDirectory);
            await WriteIfChanged(Path.Combine(snapshotDirectory, "crawl-failure.json"), JsonSerializer.Serialize(new CrawlFailureReport {
                Error = ex.Message,
                PreviousSpecificationRetained = true
            }, AotJsonContext.Get<CrawlFailureReport>(JsonOptions)) + "\n", cancellationToken);
            throw;
        }
        var manifest = new JsonObject();
        var snapshots = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DocumentationPage page in pages)
        {
            string content = JsonSerializer.Serialize(page, AotJsonContext.Get<DocumentationPage>(JsonOptions)) + "\n";
            string name = Hash(page.Url)[..24] + ".json";
            snapshots[name] = content;
            manifest[page.Url] = new JsonObject { ["file"] = name, ["sha256"] = Hash(content), ["version"] = page.Version, ["error"] = page.Error };
        }
        string[] unreadablePreviousPages = pages.Where(p => p.Error != null && previousManifest?.ContainsKey(p.Url) == true && previousManifest[p.Url]?["error"] == null).Select(p => p.Url).ToArray();
        if (unreadablePreviousPages.Length > 0)
        {
            Directory.CreateDirectory(snapshotDirectory);
            await WriteIfChanged(Path.Combine(snapshotDirectory, "crawl-failure.json"), JsonSerializer.Serialize(new UnreadablePagesReport {
                UnreadablePreviouslyTrackedPages = unreadablePreviousPages,
                PreviousSpecificationRetained = true
            }, AotJsonContext.Get<UnreadablePagesReport>(JsonOptions)) + "\n", cancellationToken);
            throw new InvalidOperationException("Previously readable documentation could not be fetched. The previous specification was retained: " + string.Join(", ", unreadablePreviousPages));
        }
        string[] removedPages = previousManifest?.Select(p => p.Key).Except(manifest.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (removedPages.Length > 0)
        {
            Directory.CreateDirectory(snapshotDirectory);
            await WriteIfChanged(Path.Combine(snapshotDirectory, "crawl-failure.json"), JsonSerializer.Serialize(new RemovedPagesReport { RemovedPages = removedPages, PreviousSpecificationRetained = true }, AotJsonContext.Get<RemovedPagesReport>(JsonOptions)) + "\n", cancellationToken);
            throw new InvalidOperationException("Previously tracked documentation disappeared from the crawl. Refusing an incomplete update: " + string.Join(", ", removedPages));
        }
        var postmanManifest = new JsonObject();
        string previousPostmanPath = Path.Combine(baseline, "documentation", "postman-manifest.json");
        JsonObject? previousPostman = File.Exists(previousPostmanPath) ? JsonNode.Parse(await File.ReadAllTextAsync(previousPostmanPath, cancellationToken))!.AsObject() : null;
        foreach ((string name, string raw) in postman.Collections)
        {
            string filename = "postman-" + name + ".json";
            string content = Serialize(JsonNode.Parse(raw)!);
            snapshots[filename] = content;
            postmanManifest[name] = new JsonObject
            {
                ["file"] = filename,
                ["sha256"] = Hash(content),
                ["fetchIssue"] = postman.Issues?.FirstOrDefault(i => i.Section == name)?.Message
            };
        }
        string[] changedCollections = postmanManifest.Where(p => !JsonNode.DeepEquals(previousPostman?[p.Key], p.Value)).Select(p => p.Key)
            .Union(previousPostman?.Select(p => p.Key).Except(postmanManifest.Select(p => p.Key)) ?? []).Order(StringComparer.Ordinal).ToArray();
        DocumentationBuild build = new HybridOpenApiBuilder().Build(postman.Document, new DocumentationOpenApiBuilder().Build(pages));
        if (postman.Issues is { Count: > 0 })
        {
            build = build with { Issues = build.Issues.Concat(postman.Issues).ToArray() };
            foreach (DocumentationIssue issue in postman.Issues) logger.LogWarning("{Collection}: {Message}", issue.Section, issue.Message);
        }
        string serialized = Serialize(build.Document);
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(serialized)))
        {
            ReadResult parsed = await new OpenApiJsonReader().ReadAsync(stream, new Uri(Path.GetFullPath(specPath)), new OpenApiReaderSettings(), cancellationToken);
            string[] errors = (parsed.Diagnostic?.Errors.Select(e => e.Message) ?? [])
                .Concat(parsed.Document?.Validate(ValidationRuleSet.GetDefaultRuleSet()).Select(e => e.Message) ?? []).Distinct().ToArray();
            if (parsed.Document == null || errors.Length > 0)
            {
                Directory.CreateDirectory(snapshotDirectory);
                await WriteIfChanged(Path.Combine(snapshotDirectory, "crawl-failure.json"), JsonSerializer.Serialize(new ValidationErrorsReport { ValidationErrors = errors, PreviousSpecificationRetained = true }, AotJsonContext.Get<ValidationErrorsReport>(JsonOptions)) + "\n", cancellationToken);
                throw new InvalidOperationException("Generated OpenAPI failed validation: " + string.Join("; ", errors));
            }
        }
        bool changed = publishedSpec == null || !JsonNode.DeepEquals(publishedSpec, build.Document);
        bool baselineChanged = previousSpec == null || !JsonNode.DeepEquals(previousSpec, build.Document);
        string[] changedPages = manifest.Where(p => !JsonNode.DeepEquals(previousManifest?[p.Key], p.Value)).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        string[] removedOperations = previousSpec?["x-generator"]?.ToString() == "linkedin-postman-learn-v1"
            ? Operations(previousSpec).Except(Operations(build.Document), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
        if (removedOperations.Length > 0)
        {
            Directory.CreateDirectory(snapshotDirectory);
            await WriteIfChanged(Path.Combine(snapshotDirectory, "crawl-failure.json"), JsonSerializer.Serialize(new RemovedOperationsReport { RemovedOperations = removedOperations, PreviousSpecificationRetained = true }, AotJsonContext.Get<RemovedOperationsReport>(JsonOptions)) + "\n", cancellationToken);
            throw new InvalidOperationException("Previously generated operations disappeared from the documentation. Review these removals before replacing the baseline: " + string.Join(", ", removedOperations));
        }
        Directory.CreateDirectory(snapshotDirectory);
        string failurePath = Path.Combine(snapshotDirectory, "crawl-failure.json");
        if (File.Exists(failurePath)) File.Delete(failurePath);
        foreach ((string name, string content) in snapshots)
            await WriteIfChanged(Path.Combine(snapshotDirectory, name), content, cancellationToken);
        await WriteIfChanged(manifestPath, Serialize(manifest), cancellationToken);
        await WriteIfChanged(Path.Combine(snapshotDirectory, "postman-manifest.json"), Serialize(postmanManifest), cancellationToken);
        await WriteIfChanged(Path.Combine(snapshotDirectory, "coverage.json"), JsonSerializer.Serialize(new CoverageReport {
            Pages = pages.Count,
            PostmanCollections = postman.Collections.Count,
            PostmanFetchIssues = postman.Issues,
            ResponsesEnrichedFromLearn = build.Document["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject())
                .Where(p => p.Value is JsonObject).SelectMany(p => (p.Value?["responses"] as JsonObject)?.Select(r => r.Value) ?? [])
                .Count(r => (r?["content"] as JsonObject)?.Any(m => m.Value?["x-linkedin-learn-schema"]?.ToString() == "true") == true),
            Operations = Operations(build.Document).Count(),
            Schemas = build.Document["components"]!["schemas"]!.AsObject().Count,
            Issues = build.Issues
        }, AotJsonContext.Get<CoverageReport>(JsonOptions)) + "\n", cancellationToken);
        // Keep the last meaningful change report on no-op runs to avoid an extra commit the next day.
        if (changedPages.Length > 0 || changedCollections.Length > 0 || baselineChanged)
            await WriteIfChanged(Path.Combine(snapshotDirectory, "changes.json"), JsonSerializer.Serialize(new ChangesReport {
                ChangedPages = changedPages,
                ChangedPostmanCollections = changedCollections,
                PostmanFetchIssues = postman.Issues,
                AddedOperations = Operations(build.Document).Except(previousSpec == null ? [] : Operations(previousSpec), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                RemovedOperations = removedOperations,
                SpecificationChanges = Differences(previousSpec, build.Document, "").ToArray(),
                SpecificationChanged = baselineChanged
            }, AotJsonContext.Get<ChangesReport>(JsonOptions)) + "\n", cancellationToken);
        await WriteIfChanged(specPath, serialized, cancellationToken);
        logger.LogInformation("LinkedIn documentation: {Pages} pages, {Operations} operations, {Issues} unresolved definitions; spec changed: {Changed}", pages.Count, Operations(build.Document).Count(), build.Issues.Count, changed);
        if (!options.SpecOnly && options.FailOnUnresolvedSchemas && build.Issues.Count > 0)
            throw new DocumentationReviewRequiredException($"Documentation conversion has {build.Issues.Count} unresolved definitions. Inspect documentation/coverage.json; publication was stopped. Set Documentation:FailOnUnresolvedSchemas=false only after reviewing the reported gaps.");
        return changed;
    }

    private static IEnumerable<string> Operations(JsonObject document) => document["paths"]?.AsObject()
        .SelectMany(p => p.Value!.AsObject().Where(o => o.Key is "get" or "post" or "put" or "patch" or "delete" or "head" or "options").Select(o => o.Key.ToUpperInvariant() + " " + p.Key)) ?? [];

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IEnumerable<SpecificationDifference> Differences(JsonNode? before, JsonNode? after, string pointer)
    {
        if (JsonNode.DeepEquals(before, after)) yield break;
        if (before is JsonObject left && after is JsonObject right)
        {
            foreach (string key in left.Select(p => p.Key).Union(right.Select(p => p.Key)).Order(StringComparer.Ordinal))
                foreach (SpecificationDifference difference in Differences(left[key], right[key], pointer + "/" + key.Replace("~", "~0").Replace("/", "~1")))
                    yield return difference;
        }
        else yield return new SpecificationDifference(pointer, before?.DeepClone(), after?.DeepClone());
    }

    private static string Serialize(JsonNode node) => Sort(node).ToJsonString(JsonOptions) + "\n";

    private static JsonNode Sort(JsonNode node)
    {
        if (node is JsonObject obj) return new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value == null ? null : Sort(p.Value))));
        if (node is JsonArray arr) return new JsonArray(arr.Select(n => n == null ? null : Sort(n)).ToArray());
        return node.DeepClone();
    }

    private static async Task WriteIfChanged(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken) == content) return;
        string temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
