using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public interface IDocumentationSpecGenerator
{
    /// <summary>Reads official documentation with Playwright, writes deterministic source snapshots, a coverage/change report and OpenAPI, and returns whether the target specification changed. Loss of previously tracked coverage preserves the existing specification; unresolved new definitions require review before publication.</summary>
    Task<bool> Generate(string outputDirectory, DocumentationOptions options, CancellationToken cancellationToken = default);
}
