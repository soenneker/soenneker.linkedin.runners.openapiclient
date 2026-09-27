using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public interface IPostmanSpecSource
{
    /// <summary>Loads and converts the official collections, retaining their raw snapshots. When a download fails, an available previous snapshot is retained and reported; otherwise the update fails.</summary>
    Task<PostmanBuild> Read(string outputDirectory, DocumentationOptions options, CancellationToken cancellationToken = default);
}

public sealed record PostmanBuild(JsonObject Document, IReadOnlyDictionary<string, string> Collections, IReadOnlyList<DocumentationIssue>? Issues = null);
