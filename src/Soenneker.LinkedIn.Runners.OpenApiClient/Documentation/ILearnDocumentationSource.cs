using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public interface ILearnDocumentationSource
{
    /// <summary>Uses an isolated Playwright browser to read official LinkedIn Learn articles and their linked definitions. Records unreadable URLs as errors for coverage review rather than treating them as removals.</summary>
    Task<IReadOnlyList<DocumentationPage>> Read(DocumentationOptions options, CancellationToken cancellationToken = default);
}
