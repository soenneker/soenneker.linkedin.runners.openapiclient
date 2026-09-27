using System;
using System.Linq;
using System.Text.Json.Nodes;
using Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Tests;

public sealed class HybridTests
{
    [Test]
    public void LearnResponseTypesEnrichPostmanSuccessWithoutLosingExamplesOrErrors()
    {
        JsonObject postman = Postman();
        DocumentationBuild build = new HybridOpenApiBuilder().Build(postman, Learn());
        JsonNode operation = build.Document["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!;
        JsonNode schema = operation["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        Check(schema["properties"]!["state"]!["enum"]!.AsArray().Count == 2, "Learn enum missing from the actual response.");
        Check(schema["properties"]!["details"]!["allOf"]![0]!["$ref"]!.ToString() == "#/components/schemas/Learn_Details", "Nested response type was not namespaced.");
        Check(schema["required"]![0]!.ToString() == "state", "Learn requiredness missing.");
        Check(schema["properties"]!["postmanOnly"] != null, "Postman-only property lost.");
        Check(operation["responses"]!["200"]!["content"]!["application/json"]!["example"] != null, "Saved response example lost.");
        Check(JsonNode.DeepEquals(operation["responses"]!["400"], postman["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!["responses"]!["400"]), "Error response changed.");
        Check(build.Document["paths"]!["/unmatched"] != null, "Unmatched endpoint lost.");
        Check(operation["parameters"]![0]!["name"]!.ToString() == "widgetId", "Path parameter renamed.");
        Check(build.Document["components"]!["schemas"]!["Learn_Details"] != null, "Nested model missing.");
        Check(!postman.ToJsonString().Contains("Learn_", StringComparison.Ordinal), "Source document was mutated.");
    }

    [Test]
    public void AmbiguousStatusesAndConflictingTypesKeepPostmanAndReportGaps()
    {
        JsonObject postman = Postman();
        JsonObject responses = postman["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!["responses"]!.AsObject();
        responses["202"] = responses["200"]!.DeepClone();
        DocumentationBuild build = new HybridOpenApiBuilder().Build(postman, Learn());
        Check(JsonNode.DeepEquals(responses, build.Document["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!["responses"]), "Ambiguous response was overwritten.");
        Check(build.Issues.Any(i => i.Message.Contains("no explicit status", StringComparison.Ordinal)), "Ambiguous status not reported.");
        responses.Remove("202");
        responses["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["state"]!["type"] = "integer";
        build = new HybridOpenApiBuilder().Build(postman, Learn());
        Check(build.Document["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["state"]!["type"]!.ToString() == "integer", "Conflicting type replaced silently.");
        Check(build.Issues.Any(i => i.Message.Contains("types differ", StringComparison.Ordinal)), "Type conflict not reported.");
    }

    [Test]
    public void FinderExamplesMatchDocumentedSelectorsAndDifferentFindersStaySeparate()
    {
        JsonObject postman = Postman();
        DocumentationBuild learn = Learn();
        JsonNode operation = postman["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!;
        operation["parameters"]!.AsArray().Add(JsonNode.Parse("""{"in":"query","name":"q","schema":{"type":"string"},"examples":{"finder":{"value":"owner"}}}"""));
        JsonNode doc = learn.Document["paths"]!["/v2/widgets/{resourceId}"]!["get"]!;
        doc["parameters"] = JsonNode.Parse("""[{"in":"query","name":"q","schema":{"type":"string","default":"owner"}}]""");
        DocumentationBuild build = new HybridOpenApiBuilder().Build(postman, learn);
        Check(build.Document["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!["x-linkedin-learn-source"] != null, "Matching finder was not enriched.");
        doc["parameters"]![0]!["schema"]!["default"] = "author";
        build = new HybridOpenApiBuilder().Build(postman, learn);
        Check(build.Document["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!["x-linkedin-learn-source"] == null, "A different finder supplied the response type.");
    }

    [Test]
    public void EnvironmentBasePathsAndLiteralUrnsMatchButKnownApiVersionsStayDistinct()
    {
        JsonObject postman = Postman();
        JsonObject paths = postman["paths"]!.AsObject();
        JsonNode item = paths["/learning-content/v2/widgets/{widgetId}"]!.DeepClone();
        paths.Remove("/learning-content/v2/widgets/{widgetId}");
        item["get"]!["servers"] = JsonNode.Parse("""[{"url":"https://{baseUrl}"}]""");
        paths["/learning-content/widgets/urn%3Ali%3Awidget%3A123"] = item;
        DocumentationBuild build = new HybridOpenApiBuilder().Build(postman, Learn());
        Check(build.Document["paths"]!["/learning-content/widgets/urn%3Ali%3Awidget%3A123"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["state"]!["enum"] != null, "Environment-relative literal URN did not receive its response type.");
        paths.Remove("/learning-content/widgets/urn%3Ali%3Awidget%3A123");
        item["get"]!["servers"] = JsonNode.Parse("""[{"url":"https://api.linkedin.com"}]""");
        paths["/learning-content/rest/widgets/{widgetId}"] = item;
        build = new HybridOpenApiBuilder().Build(postman, Learn());
        Check(build.Document["paths"]!["/learning-content/rest/widgets/{widgetId}"]!["get"]!["x-linkedin-learn-source"] == null, "Known /rest endpoint matched a /v2 schema.");
    }

    [Test]
    public void ExampleOnlyFieldsDoNotOverrideDocumentedOrPostmanResponseTypes()
    {
        DocumentationBuild learn = Learn();
        JsonNode properties = learn.Document["paths"]!["/v2/widgets/{resourceId}"]!["get"]!["responses"]!["default"]!["content"]!["application/json"]!["schema"]!["properties"]!;
        properties["state"] = JsonNode.Parse("""{"type":"integer","x-linkedin-inferred-from-example":true}""");
        DocumentationBuild build = new HybridOpenApiBuilder().Build(Postman(), learn);
        Check(build.Document["paths"]!["/learning-content/v2/widgets/{widgetId}"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["state"]!["type"]!.ToString() == "string", "Example overrode the response type.");
    }

    private static JsonObject Postman() => JsonNode.Parse("""
        {"openapi":"3.0.3","info":{"title":"Postman","version":"1"},"components":{"schemas":{}},"paths":{
          "/learning-content/v2/widgets/{widgetId}":{"get":{"operationId":"getWidget","parameters":[{"in":"path","name":"widgetId","required":true,"schema":{"type":"string"}}],"responses":{
            "200":{"description":"OK","content":{"application/json":{"example":{"state":"ACTIVE"},"schema":{"type":"object","properties":{"state":{"type":"string"},"postmanOnly":{"type":"string"}}}}}},
            "400":{"description":"Bad request","content":{"application/json":{"schema":{"type":"object","properties":{"message":{"type":"string"}}}}}}
          }}},
          "/unmatched":{"post":{"responses":{"204":{"description":"No content"}}}}
        }}
        """)!.AsObject();

    private static DocumentationBuild Learn() => new(JsonNode.Parse("""
        {"openapi":"3.0.3","info":{"title":"Learn","version":"1"},"components":{"schemas":{"Details":{"type":"object","properties":{"count":{"type":"integer","format":"int64"}}}}},"paths":{
          "/v2/widgets/{resourceId}":{"get":{"x-source-url":"https://learn.microsoft.com/en-us/linkedin/shared/widgets","responses":{
            "default":{"description":"Documented response","content":{"application/json":{"schema":{"type":"object","x-source-url":"https://learn.microsoft.com/en-us/linkedin/shared/widgets#schema","required":["state"],"properties":{"state":{"type":"string","enum":["ACTIVE","ARCHIVED"]},"details":{"allOf":[{"$ref":"#/components/schemas/Details"}]}}}}}}
          }}}
        }}
        """)!.AsObject(), []);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
