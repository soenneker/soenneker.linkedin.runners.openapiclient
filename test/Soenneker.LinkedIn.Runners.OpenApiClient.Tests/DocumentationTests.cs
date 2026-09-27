using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi.Reader;
using Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Tests;

public sealed class DocumentationTests
{
    [Test]
    public async Task ConvertsGenericTablesAndSeparatesCreateRequirementsFromResponses()
    {
        DocumentationBuild result = new DocumentationOpenApiBuilder().Build([Page()]);
        JsonNode request = result.Document["paths"]!["/rest/widgets"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!;
        JsonNode response = result.Document["paths"]!["/rest/widgets/{widgetsId}"]!["get"]!["responses"]!["default"]!["content"]!["application/json"]!["schema"]!;
        Check(request["properties"]!["id"]!["readOnly"]!.GetValue<bool>(), "Read-only field was lost.");
        Check(request["required"]!.AsArray().Any(v => v!.ToString() == "owner"), "Create-only requirement was lost.");
        Check(!response["required"]!.AsArray().Any(v => v!.ToString() == "owner"), "Create-only requirement leaked into response.");
        Check(request["properties"]!["state"]!["enum"]!.AsArray().Count == 2, "Documented enum values were lost.");
        Check(!request["required"]!.AsArray().Any(v => v!.ToString() == "note"), "An example-only field was made required.");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(result.Document.ToJsonString()));
        var read = await new OpenApiJsonReader().ReadAsync(stream, new Uri("https://example.test/openapi.json"), new OpenApiReaderSettings());
        Check(read.Document != null && read.Diagnostic?.Errors.Any() != true, "Generated OpenAPI did not parse: " + string.Join("; ", read.Diagnostic?.Errors.Select(e => e.Message) ?? []));
    }

    [Test]
    public void QueryTunnelingAndBatchResponseMapsDoNotCreateFakeEndpointsOrProperties()
    {
        DocumentationPage page = Page();
        page.Blocks =
        [
            new() { Kind = "code", Section = "Get", Text = "curl -X POST 'https://api.linkedin.com/rest/widgets/{encoded Widget URN}' -H 'X-HTTP-Method-Override: GET'" },
            new() { Kind = "code", Section = "Response", Text = "{\"results\":{\"urn:li:widget:123\":{\"id\":\"abc\",\"state\":\"ACTIVE\"}}}" }
        ];
        JsonObject paths = new DocumentationOpenApiBuilder().Build([page]).Document["paths"]!.AsObject();
        Check(paths.Count == 1 && paths["/rest/widgets/{widgetsId}"]?["get"] != null, "Query tunneling created the wrong method or route.");
        JsonNode results = paths["/rest/widgets/{widgetsId}"]!["get"]!["responses"]!["default"]!["content"]!["application/json"]!["schema"]!["properties"]!["results"]!;
        Check(results["additionalProperties"] != null && results["properties"] == null, "A sample URN was emitted as a model property.");
    }

    [Test]
    public async Task DailyRunsAreStableAndReportFieldChanges()
    {
        string directory = Path.Combine(Path.GetTempPath(), "linkedin-docs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = new FakeSource { Pages = [Page()] };
            var generator = new DocumentationSpecGenerator(source, NullLogger<DocumentationSpecGenerator>.Instance);
            var options = new DocumentationOptions { SpecOnly = true };
            Check(await generator.Generate(directory, options), "First generation was not detected.");
            string original = await File.ReadAllTextAsync(Path.Combine(directory, "openapi.json"));
            string report = await File.ReadAllTextAsync(Path.Combine(directory, "documentation", "changes.json"));
            Check(!await generator.Generate(directory, options), "Identical documentation generated a change.");
            Check(report == await File.ReadAllTextAsync(Path.Combine(directory, "documentation", "changes.json")), "No-op run churned the change report.");
            source.Pages[0].Blocks[0].Rows.Add([new() { Text = "newField" }, new() { Text = "boolean" }, new() { Text = "Added by documentation" }, new() { Text = "optional" }]);
            Check(await generator.Generate(directory, options), "New documented field was not detected without a code change.");
            string updated = await File.ReadAllTextAsync(Path.Combine(directory, "openapi.json"));
            Check(original != updated && updated.Contains("newField", StringComparison.Ordinal), "New field missing from spec.");
            Check((await File.ReadAllTextAsync(Path.Combine(directory, "documentation", "changes.json"))).Contains("newField", StringComparison.Ordinal), "Field change missing from report.");
            source.Failure = new InvalidOperationException("HTTP 503");
            await MustFail(() => generator.Generate(directory, options));
            Check(updated == await File.ReadAllTextAsync(Path.Combine(directory, "openapi.json")), "Failed crawl replaced the last good spec.");
            source.Failure = null;
            source.Pages[0].Error = "HTTP 503";
            source.Pages[0].Blocks.Clear();
            await MustFail(() => generator.Generate(directory, options));
            Check(updated == await File.ReadAllTextAsync(Path.Combine(directory, "openapi.json")), "An unreadable tracked article replaced the last good spec.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task MissingPreviouslyTrackedPagesCannotSilentlyRemoveCoverage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "linkedin-docs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = new FakeSource { Pages = [Page(), new() { Url = "https://learn.microsoft.com/en-us/linkedin/shared/extra", Title = "Extra" }] };
            var generator = new DocumentationSpecGenerator(source, NullLogger<DocumentationSpecGenerator>.Instance);
            var options = new DocumentationOptions { SpecOnly = true };
            await generator.Generate(directory, options);
            string previous = await File.ReadAllTextAsync(Path.Combine(directory, "openapi.json"));
            source.Pages.RemoveAt(1);
            await MustFail(() => generator.Generate(directory, options));
            Check(previous == await File.ReadAllTextAsync(Path.Combine(directory, "openapi.json")), "Missing article changed the spec.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task CachedDraftStillRequiresClientGenerationWhenNotPublished()
    {
        string directory = Path.Combine(Path.GetTempPath(), "linkedin-docs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var generator = new DocumentationSpecGenerator(new FakeSource { Pages = [Page()] }, NullLogger<DocumentationSpecGenerator>.Instance);
            string baseline = Path.Combine(directory, "baseline");
            await generator.Generate(baseline, new DocumentationOptions { SpecOnly = true });
            bool changed = await generator.Generate(Path.Combine(directory, "client"), new DocumentationOptions { SpecOnly = true, BaselineDirectory = baseline });
            Check(changed, "An unpublished cached draft incorrectly skipped client generation.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void DocumentationUrlsStayOfficialAndVersioned()
    {
        Check(DocumentationUrl.Normalize("https://example.org/en-us/linkedin/api") == null, "External URL accepted.");
        Check(DocumentationUrl.Normalize("https://docs.microsoft.com/linkedin/shared/api#field") == "https://learn.microsoft.com/en-us/linkedin/shared/api", "Legacy official documentation link was lost.");
        Check(DocumentationUrl.Normalize("https://learn.microsoft.com/en-us/azure/api") == null, "Unrelated Learn product accepted.");
        Check(DocumentationUrl.Normalize("https://learn.microsoft.com/en-us/linkedin/marketing/api?view=old#field", "202609") == "https://learn.microsoft.com/en-us/linkedin/marketing/api?view=li-lms-2026-09&preserve-view=true", "Version not pinned consistently.");
    }

    private static DocumentationPage Page() => new()
    {
        Url = "https://learn.microsoft.com/en-us/linkedin/shared/widgets",
        Title = "Widgets",
        Blocks =
        [
            new() { Kind = "table", Section = "Widget Schema", Anchor = "widget", Headers = ["Field", "Type", "Description", "Required"], Rows =
            [
                [new() { Text = "id" }, new() { Text = "string" }, new() { Text = "Identifier" }, new() { Text = "read-only" }],
                [new() { Text = "owner" }, new() { Text = "URN" }, new() { Text = "Owner" }, new() { Text = "create-only required" }],
                [new() { Text = "state" }, new() { Text = "enum", Values = ["ACTIVE - Active widget", "ARCHIVED - Archived widget"] }, new() { Text = "State" }, new() { Text = "required" }]
            ] },
            new() { Kind = "code", Section = "Create", Anchor = "create", Text = "POST https://api.linkedin.com/rest/widgets\n{\"owner\":\"urn:li:person:1\",\"state\":\"ACTIVE\",\"note\":\"example\"}" },
            new() { Kind = "text", Section = "Create", Text = "201 response with x-restli-id." },
            new() { Kind = "code", Section = "Get", Anchor = "get", Text = "GET https://api.linkedin.com/rest/widgets/{widget id}" },
            new() { Kind = "code", Section = "Response", Text = "{\"id\":\"abc\",\"owner\":\"urn:li:person:1\",\"state\":\"ACTIVE\"}" }
        ]
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task MustFail(Func<Task<bool>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected incomplete documentation to be rejected.");
    }

    private sealed class FakeSource : ILearnDocumentationSource
    {
        public List<DocumentationPage> Pages { get; set; } = [];
        public Exception? Failure { get; set; }
        public Task<IReadOnlyList<DocumentationPage>> Read(DocumentationOptions options, CancellationToken cancellationToken = default) =>
            Failure != null ? Task.FromException<IReadOnlyList<DocumentationPage>>(Failure) : Task.FromResult<IReadOnlyList<DocumentationPage>>(Pages);
    }
}
