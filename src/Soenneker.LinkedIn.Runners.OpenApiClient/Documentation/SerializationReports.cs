using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

internal sealed record SpecificationDifference(string Pointer, JsonNode? Before, JsonNode? After);

internal sealed class CrawlFailureReport
{
    public string? Error { get; init; }
    public bool PreviousSpecificationRetained { get; init; }
}

internal sealed class UnreadablePagesReport
{
    public string[] UnreadablePreviouslyTrackedPages { get; init; } = [];
    public bool PreviousSpecificationRetained { get; init; }
}

internal sealed class ValidationErrorsReport
{
    public string[] ValidationErrors { get; init; } = [];
    public bool PreviousSpecificationRetained { get; init; }
}

internal sealed class RemovedOperationsReport
{
    public string[] RemovedOperations { get; init; } = [];
    public bool PreviousSpecificationRetained { get; init; }
}

internal sealed class CoverageReport
{
    public int Pages { get; init; }
    public string[] RetainedMissingPages { get; init; } = [];
    public int PostmanCollections { get; init; }
    public IReadOnlyList<DocumentationIssue>? PostmanFetchIssues { get; init; }
    public int ResponsesEnrichedFromLearn { get; init; }
    public int Operations { get; init; }
    public int Schemas { get; init; }
    public IReadOnlyList<DocumentationIssue> Issues { get; init; } = [];
}

internal sealed class ChangesReport
{
    public string[] ChangedPages { get; init; } = [];
    public string[] ChangedPostmanCollections { get; init; } = [];
    public IReadOnlyList<DocumentationIssue>? PostmanFetchIssues { get; init; }
    public string[] AddedOperations { get; init; } = [];
    public string[] RemovedOperations { get; init; } = [];
    public SpecificationDifference[] SpecificationChanges { get; init; } = [];
    public bool SpecificationChanged { get; init; }
}
