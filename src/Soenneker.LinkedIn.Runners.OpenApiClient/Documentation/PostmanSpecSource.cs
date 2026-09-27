using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.OpenApi.Merger.Abstract;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public sealed class PostmanSpecSource(IPostmanConverter converter, IOpenApiMerger merger) : IPostmanSpecSource
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task<PostmanBuild> Read(string outputDirectory, DocumentationOptions options, CancellationToken cancellationToken = default)
    {
        if (options.PostmanDirectory != null && !options.SpecOnly)
            throw new InvalidOperationException("PostmanDirectory is only supported for local spec-only previews; daily publication downloads fresh collections.");
        string temporary = Path.Combine(Path.GetTempPath(), "linkedin-postman-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var inputs = new List<(string prefix, string filePath)>();
            var issues = new List<DocumentationIssue>();
            var snapshots = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach ((string prefix, string id) in Constants.PostmanCollections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url = "https://www.postman.com/collections/" + id;
                string raw;
                try
                {
                    raw = options.PostmanDirectory == null
                        ? await Client.GetStringAsync(url, cancellationToken)
                        : await File.ReadAllTextAsync(Path.Combine(options.PostmanDirectory, prefix + ".postman.json"), cancellationToken);
                }
                catch (Exception ex) when (options.PostmanDirectory == null && (ex is HttpRequestException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    string? previous = new[]
                    {
                        Path.Combine(options.BaselineDirectory ?? outputDirectory, "documentation", "postman-" + prefix + ".json"),
                        Path.Combine(outputDirectory, "documentation", "postman-" + prefix + ".json"),
                        Path.Combine(outputDirectory, "postman", prefix + ".postman.json")
                    }.FirstOrDefault(File.Exists);
                    if (previous == null)
                        throw new InvalidOperationException($"Could not download official collection {prefix} ({url}); no previous snapshot is available.", ex);
                    raw = await File.ReadAllTextAsync(previous, cancellationToken);
                    issues.Add(new(url, prefix, "Download failed; previous Postman snapshot retained (stale). " + ex.Message));
                }
                JsonObject root = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidOperationException($"Empty collection: {prefix}");
                JsonObject collection = root["collection"] as JsonObject ?? root;
                if (collection["info"] == null || collection["item"] is not JsonArray)
                    throw new InvalidOperationException($"Postman did not return a collection for {prefix}.");
                snapshots[prefix] = collection.ToJsonString();
                string path = Path.Combine(temporary, prefix + ".json");
                await File.WriteAllTextAsync(path, await converter.ConvertToJson(collection.ToJsonString(), cancellationToken), cancellationToken);
                inputs.Add((prefix, path));
            }
            JsonObject document = JsonNode.Parse(merger.ToJson(await merger.MergeOpenApis(inputs, cancellationToken)))!.AsObject();
            return new(document, snapshots, issues);
        }
        finally { Directory.Delete(temporary, true); }
    }
}
