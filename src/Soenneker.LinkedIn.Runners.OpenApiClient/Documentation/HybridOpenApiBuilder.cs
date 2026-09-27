using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public sealed class HybridOpenApiBuilder
{
    private static readonly HashSet<string> Methods = ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    public DocumentationBuild Build(JsonObject postman, DocumentationBuild documentation)
    {
        var result = (JsonObject)postman.DeepClone();
        var learn = (JsonObject)documentation.Document.DeepClone();
        var issues = documentation.Issues.ToList();
        var matchedDocumentation = new HashSet<JsonNode>();
        result["x-generator"] = "linkedin-postman-learn-v1";
        result["x-documentation-versions"] = learn["x-documentation-versions"]?.DeepClone();
        result["info"]!["title"] = "LinkedIn API";
        result["components"] ??= new JsonObject();
        // Keep the existing collection namespaces and namespace all Learn components to avoid collisions.
        RewriteReferences(learn);
        foreach ((string category, JsonNode? entries) in learn["components"]!.AsObject())
        {
            result["components"]![category] ??= new JsonObject();
            foreach ((string name, JsonNode? value) in entries!.AsObject())
            {
                string key = "Learn_" + name;
                if (result["components"]![category]![key] != null)
                    throw new InvalidOperationException("Component namespace collision: " + key);
                result["components"]![category]![key] = value?.DeepClone();
            }
        }
        JsonObject paths = result["paths"]!.AsObject();
        foreach ((string path, JsonNode? item) in paths.ToArray())
        {
            string canonical = Canonical(path);
            foreach ((string method, JsonNode? operationNode) in item!.AsObject().ToArray())
            {
                if (!Methods.Contains(method) || operationNode is not JsonObject operation) continue;
                var candidates = learn["paths"]!.AsObject().Where(p => Canonical(p.Key) == canonical).ToArray();
                // Collections often leave baseUrl in an external environment, hiding /rest or /v2 from the path.
                // Resolve the match only if Learn supplies a unique endpoint; never silently equate known versions.
                if (candidates.Length == 0 && (operation["servers"] ?? item["servers"] ?? result["servers"]) is JsonArray servers && servers.OfType<JsonObject>().Any(server => server["url"]?.ToString().Contains('{') == true))
                    candidates = learn["paths"]!.AsObject().Where(p => Canonical(p.Key).EndsWith(canonical, StringComparison.Ordinal) && p.Value?[method] != null).ToArray();
                JsonObject[] matches = candidates.Select(p => p.Value?[method]).OfType<JsonObject>().ToArray();
                if (matches.Length != 1)
                {
                    issues.Add(new("postman", method.ToUpperInvariant() + " " + path,
                        matches.Length == 0 ? "No Learn operation matched; Postman definition retained." : "Multiple Learn operations matched; Postman definition retained."));
                    continue;
                }
                JsonObject doc = matches[0];
                if (!CompatibleSelectors(operation, doc))
                {
                    issues.Add(new(doc["x-source-url"]?.ToString() ?? "learn", path, "Rest.li action/finder selectors differ; Postman definition retained."));
                    continue;
                }
                operation["x-linkedin-learn-source"] = doc["x-source-url"]?.DeepClone();
                matchedDocumentation.Add(doc);
                if (doc["requestBody"] is JsonObject body)
                {
                    operation["requestBody"] = EnrichContent(operation["requestBody"] as JsonObject, body, result, issues, path + " request");
                }
                if (doc["responses"] is JsonObject responses)
                {
                    operation["responses"] ??= new JsonObject();
                    foreach ((string status, JsonNode? response) in responses)
                    {
                        string targetStatus = status;
                        // Learn often describes the response without repeating its status. The collection supplies that status.
                        if (status == "default" && response?["content"] != null)
                        {
                            string[] successes = operation["responses"]!.AsObject().Select(r => r.Key)
                                .Where(s => s.StartsWith('2') && s != "204" && s != "205").ToArray();
                            if (successes.Length == 1) targetStatus = successes[0];
                            else if (successes.Length > 1 || operation["responses"]!["default"] == null)
                            {
                                issues.Add(new(doc["x-source-url"]?.ToString() ?? "learn", path, "Learn response has no explicit status; status-specific Postman responses retained."));
                                continue;
                            }
                        }
                        else if (status == "default") continue;
                        if (response is JsonObject definition)
                        {
                            operation["responses"]![targetStatus] = EnrichContent(operation["responses"]![targetStatus] as JsonObject, definition, result, issues, path + " response " + targetStatus);
                            if (targetStatus != status)
                                operation["responses"]![targetStatus]!["x-linkedin-status-source"] = "Single Postman success response; Learn supplies the response schema.";
                        }
                    }
                }
                // Only enrich existing non-path parameters. Template names and collection request variants remain intact.
                if (operation["parameters"] is JsonArray parameters && doc["parameters"] is JsonArray documentedParameters)
                    foreach (JsonObject parameter in parameters.OfType<JsonObject>())
                    {
                        if (parameter["in"]?.ToString() == "path") continue;
                        JsonObject? definition = documentedParameters.OfType<JsonObject>().FirstOrDefault(p => p["name"]?.ToString() == parameter["name"]?.ToString() && p["in"]?.ToString() == parameter["in"]?.ToString());
                        if (definition?["schema"] is JsonObject schema && HasDocumentation(schema))
                            parameter["schema"] = EnrichSchema(parameter["schema"] as JsonObject, schema, result, issues, path + " parameter " + parameter["name"]);
                    }
            }
        }
        foreach ((string path, JsonNode? item) in learn["paths"]!.AsObject())
            foreach ((string method, JsonNode? operation) in item!.AsObject().Where(p => Methods.Contains(p.Key)))
                if (operation != null && !matchedDocumentation.Contains(operation))
                    issues.Add(new("learn", method.ToUpperInvariant() + " " + path, "Learn operation has no compatible Postman match. Reported for review rather than inventing a collection assignment."));
        return new(result, issues);
    }

    private static JsonObject EnrichContent(JsonObject? original, JsonObject documented, JsonObject root, List<DocumentationIssue> issues, string location)
    {
        var result = Resolve(original, root);
        if (documented["content"] is JsonObject content)
            foreach ((string mediaType, JsonNode? media) in content)
            {
                if (media?["schema"] is not JsonObject schema || !HasDocumentation(schema)) continue;
                result["content"] ??= new JsonObject();
                result["content"]![mediaType] ??= new JsonObject();
                JsonObject? previous = result["content"]![mediaType]!["schema"] as JsonObject;
                JsonObject enriched = EnrichSchema(previous, schema, root, issues, location);
                if (!JsonNode.DeepEquals(previous, enriched)) result["content"]![mediaType]!["x-linkedin-learn-schema"] = true;
                result["content"]![mediaType]!["schema"] = enriched;
            }
        foreach ((string key, JsonNode? value) in documented)
            if (key != "content" && result[key] == null) result[key] = value?.DeepClone();
        return result;
    }

    private static JsonObject EnrichSchema(JsonObject? original, JsonObject documented, JsonObject root, List<DocumentationIssue> issues, string location)
    {
        JsonObject result = Resolve(original, root);
        bool inferred = documented["x-linkedin-inferred-from-example"]?.ToString() == "true";
        if (result["type"] != null && documented["type"] != null && !JsonNode.DeepEquals(result["type"], documented["type"]))
        {
            issues.Add(new(documented["x-source-url"]?.ToString() ?? "learn", location, "Schema types differ; Postman schema retained for review."));
            return result;
        }
        foreach ((string key, JsonNode? value) in documented)
        {
            if (key == "properties" && value is JsonObject properties)
            {
                result["properties"] ??= new JsonObject();
                foreach ((string name, JsonNode? property) in properties)
                    if (property is JsonObject definition && HasDocumentation(definition))
                        result["properties"]![name] = EnrichSchema(result["properties"]![name] as JsonObject, definition, root, issues, location + "." + name);
            }
            else if (key is "items" or "additionalProperties" && value is JsonObject child && HasDocumentation(child))
                result[key] = EnrichSchema(result[key] as JsonObject, child, root, issues, location + "." + key);
            else if (!inferred && key != "properties")
                result[key] = value?.DeepClone();
            else if (key == "type" && result[key] == null)
                result[key] = value?.DeepClone();
        }
        return result;
    }

    private static JsonObject Resolve(JsonObject? value, JsonObject root)
    {
        if (value?["$ref"]?.ToString() is string reference && reference.StartsWith("#/components/", StringComparison.Ordinal))
        {
            string[] parts = reference.Split('/');
            if (parts.Length == 4 && root["components"]?[parts[2]]?[parts[3].Replace("~1", "/").Replace("~0", "~")] is JsonObject target)
                return (JsonObject)target.DeepClone();
        }
        return value == null ? new JsonObject() : (JsonObject)value.DeepClone();
    }

    private static bool HasDocumentation(JsonNode node) => node is JsonObject obj &&
        (obj.ContainsKey("x-source-url") || obj.ContainsKey("$ref") || obj.ContainsKey("x-linkedin-schema") ||
         obj["x-linkedin-inferred-from-example"] == null || obj.Any(p => p.Value != null && p.Key is "properties" or "items" or "additionalProperties" && ContainsDocumentation(p.Value)));

    private static bool ContainsDocumentation(JsonNode node) => node is JsonObject obj &&
        (obj.ContainsKey("x-source-url") || obj.ContainsKey("$ref") || obj.ContainsKey("x-linkedin-schema") || obj.Any(p => p.Value != null && ContainsDocumentation(p.Value)));

    private static bool CompatibleSelectors(JsonObject left, JsonObject right)
    {
        foreach (string name in new[] { "action", "q" })
        {
            HashSet<string> a = Selectors(left, name), b = Selectors(right, name);
            if (!a.SetEquals(b)) return false;
        }
        return true;
    }

    private static HashSet<string> Selectors(JsonObject operation, string name)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? parameter = (operation["parameters"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(p => p["name"]?.ToString() == name && p["in"]?.ToString() == "query");
        void Add(JsonNode? value) { if (value is JsonValue) values.Add(value.ToString()); }
        Add(parameter?["schema"]?["default"]);
        Add(parameter?["example"]);
        if (parameter?["schema"]?["enum"] is JsonArray enumeration) foreach (JsonNode? value in enumeration) Add(value);
        if (parameter?["examples"] is JsonObject examples) foreach ((string _, JsonNode? example) in examples) Add(example?["value"]);
        if (operation["x-linkedin-request-variants"] is JsonArray variants)
            foreach (JsonNode? variant in variants)
                foreach (string pair in (variant?.ToString() ?? "").Split('&'))
                {
                    string[] parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0] == name) values.Add(Uri.UnescapeDataString(parts[1]));
                }
        // Preserve an unknown selector as an unknown; it must not match an ordinary resource GET/POST.
        if (parameter != null && values.Count == 0) values.Add("<unspecified>");
        return values;
    }

    private static string Canonical(string path)
    {
        foreach ((string prefix, string _) in Constants.PostmanCollections)
            if (path.StartsWith("/" + prefix + "/", StringComparison.Ordinal)) { path = path[(prefix.Length + 1)..]; break; }
        path = Uri.UnescapeDataString(path.TrimEnd('/'));
        path = string.Join('/', path.Split('/').Select(segment => segment.StartsWith("urn:", StringComparison.Ordinal) || long.TryParse(segment, out _) ? "{}" : segment));
        return Regex.Replace(path, @"\{[^{}]+\}", "{}");
    }

    private static void RewriteReferences(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach ((string key, JsonNode? value) in obj.ToArray())
            {
                if (key == "$ref" && value?.ToString().StartsWith("#/components/", StringComparison.Ordinal) == true)
                {
                    string reference = value.ToString();
                    int slash = reference.IndexOf('/', "#/components/".Length);
                    obj[key] = reference[..(slash + 1)] + "Learn_" + reference[(slash + 1)..];
                }
                else if (value != null) RewriteReferences(value);
            }
        else if (node is JsonArray array)
            foreach (JsonNode? value in array) if (value != null) RewriteReferences(value);
    }
}
