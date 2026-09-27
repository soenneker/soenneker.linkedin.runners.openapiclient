using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

public sealed record DocumentationBuild(JsonObject Document, IReadOnlyList<DocumentationIssue> Issues);

/// <summary>Builds deterministic OpenAPI from documented operations and schema tables, retaining source evidence and identifying example-derived schemas.</summary>
public sealed class DocumentationOpenApiBuilder
{
    public DocumentationBuild Build(IReadOnlyList<DocumentationPage> pages)
    {
        var issues = new List<DocumentationIssue>();
        issues.AddRange(pages.Where(p => p.Error != null).Select(p => new DocumentationIssue(p.Url, "", p.Error!)));
        var schemas = new SchemaBuilder(pages, issues);
        var paths = new JsonObject();
        foreach (DocumentationPage page in pages.OrderBy(p => p.Url, StringComparer.Ordinal))
            AddOperations(page, paths, schemas, issues);
        if (paths.Count == 0) throw new InvalidOperationException("No documented API operations were found. Refusing to produce an empty specification.");
        var document = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "LinkedIn API", ["version"] = "1.0.0", ["description"] = "Derived from official LinkedIn Microsoft Learn documentation. See x-source-url and the documentation coverage report for provenance and unresolved definitions." },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.linkedin.com" }),
            ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = schemas.Schemas,
                ["securitySchemes"] = new JsonObject { ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" } }
            },
            ["x-generator"] = "linkedin-learn-playwright-v1",
            ["x-documentation-versions"] = new JsonArray(pages.Select(p => p.Version).Where(v => v.Length > 0).Distinct().Order(StringComparer.Ordinal).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
        };
        return new(document, issues.Distinct().OrderBy(i => i.Url, StringComparer.Ordinal).ThenBy(i => i.Section, StringComparer.Ordinal).ThenBy(i => i.Message, StringComparer.Ordinal).ToArray());
    }

    private static void AddOperations(DocumentationPage page, JsonObject paths, SchemaBuilder schemas, List<DocumentationIssue> issues)
    {
        JsonObject? lastOperation = null;
        string lastMethod = "";
        string lastRequestSection = "";
        bool lastPartial = false;
        bool lastCreate = false;
        string status = "";
        foreach (DocumentationBlock block in page.Blocks)
        {
            if (block.Kind == "text")
            {
                Match statusMatch = Regex.Match(block.Text, @"\b([245]\d{2})(?:\s+(?:response|status|Created|OK|No Content)|\s*\(No Content\))", RegexOptions.IgnoreCase);
                if (lastOperation != null && statusMatch.Success)
                {
                    status = statusMatch.Groups[1].Value;
                    Response(lastOperation, status)["description"] = block.Text;
                }
                if (lastOperation != null && block.Text.Contains("x-restli-id", StringComparison.OrdinalIgnoreCase) && status == "201")
                    Response(lastOperation, status)["headers"] = new JsonObject { ["x-restli-id"] = new JsonObject { ["description"] = "Identifier of the created resource.", ["schema"] = new JsonObject { ["type"] = "string" } } };
                continue;
            }
            if (block.Kind != "code") continue;
            (string method, string url)? request = Request(block.Text);
            if (request != null)
            {
                (string method, string url) = request.Value;
                string? path = NormalizePath(url);
                if (path == null)
                {
                    issues.Add(new(page.Url, block.Section, $"Could not normalize documented endpoint: {url}"));
                    lastOperation = null;
                    continue;
                }
                if (paths[path] is not JsonObject pathItem) paths[path] = pathItem = new JsonObject();
                bool existing = pathItem[method] is JsonObject;
                if (pathItem[method] is not JsonObject operation)
                {
                    operation = new JsonObject
                    {
                        ["operationId"] = SchemaBuilder.Identifier(method + "_" + path.Trim('/')),
                        ["summary"] = page.Title + ": " + block.Section,
                        ["tags"] = new JsonArray(new Uri(page.Url).AbsolutePath.Split('/')[3]),
                        ["x-source-url"] = page.Url + "#" + block.Anchor,
                        ["parameters"] = new JsonArray(),
                        ["responses"] = new JsonObject { ["default"] = new JsonObject { ["description"] = "Response status or schema is not fully specified in the extracted documentation. See x-source-url." } }
                    };
                    pathItem[method] = operation;
                }
                AddParameters(operation, path, url, block.Text, page);
                AddDocumentedParameters(operation, page, block);
                foreach (JsonNode? parameter in (JsonArray)operation["parameters"]!)
                {
                    if (parameter!["in"]!.ToString() != "query") continue;
                    JsonObject? definition = schemas.Parameter(page, parameter["name"]!.ToString());
                    if (definition != null) parameter["schema"] = definition;
                }
                if (existing && operation["x-source-url"]?.ToString() != page.Url + "#" + block.Anchor)
                    AddString(operation, "x-source-urls", page.Url + "#" + block.Anchor);
                if (url.Contains('?')) AddString(operation, "x-linkedin-request-variants", url[(url.IndexOf('?') + 1)..]);
                lastOperation = operation;
                lastMethod = method;
                lastRequestSection = block.Section;
                lastPartial = method == "patch" || block.Text.Contains("partial_update", StringComparison.OrdinalIgnoreCase) || method == "post" && path.Contains('{');
                lastCreate = method == "post" && !lastPartial && !url.Contains("action=", StringComparison.OrdinalIgnoreCase);
                status = "";
                JsonNode? body = ExtractJson(block.Text);
                if (body != null && method is "post" or "put" or "patch") AddBody(operation, schemas.ForExample(body, page, lastCreate, lastPartial), true, "");
                continue;
            }
            JsonNode? example = ExtractJson(block.Text);
            if (example == null || lastOperation == null) continue;
            bool response = Regex.IsMatch(block.Section, @"response|result", RegexOptions.IgnoreCase) || lastMethod is "get" or "delete";
            bool bodyRequest = (block.Section == lastRequestSection || Regex.IsMatch(block.Section, @"request|creat|updat|reshar", RegexOptions.IgnoreCase)) && !response;
            if (response)
            {
                Match responseMethod = Regex.Match(block.Section, @"\b(get|post|put|patch|delete)\b", RegexOptions.IgnoreCase);
                if (responseMethod.Success && !responseMethod.Value.Equals(lastMethod, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new(page.Url, block.Section, "Response example names a different method from the preceding request; association needs review."));
                    continue;
                }
                Match httpStatus = Regex.Match(block.Text, @"HTTP/\S+\s+(\d{3})");
                AddBody(lastOperation, schemas.ForExample(example, page, false), false, httpStatus.Success ? httpStatus.Groups[1].Value : status.Length > 0 ? status : "default");
            }
            else if (bodyRequest && lastMethod is "post" or "put" or "patch")
                AddBody(lastOperation, schemas.ForExample(example, page, lastCreate, lastPartial), true, "");
            else
                issues.Add(new(page.Url, block.Section, "JSON example could not be reliably classified as a request or response."));
        }
        foreach ((string path, JsonNode? pathItem) in paths)
        {
            foreach ((string method, JsonNode? node) in (JsonObject)pathItem!)
            {
                var operation = (JsonObject)node!;
                if (!operation["x-source-url"]!.ToString().StartsWith(page.Url + "#", StringComparison.Ordinal)) continue;
                if (method is "post" or "put" or "patch" && operation["requestBody"] == null)
                    issues.Add(new(page.Url, path, $"{method.ToUpperInvariant()} request body was not resolved."));
                if (!((JsonObject)operation["responses"]!).Any(r => r.Value?["content"] != null || r.Key is "201" or "202" or "204"))
                    issues.Add(new(page.Url, path, $"{method.ToUpperInvariant()} response schema was not resolved."));
            }
        }
    }

    private static (string method, string url)? Request(string text)
    {
        // Replace whitespace inside documentation placeholders before finding the URL.
        text = Regex.Replace(text, @"\{[^{}\r\n]+\}", m => "{" + Regex.Replace(m.Value[1..^1], @"\s+", "_") + "}");
        Match http = Regex.Match(text, @"\b(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+['"" ]*(https://api\.linkedin\.com/[^\r\n\s'""\\]+|/(?:rest|v2)/[^\r\n\s'""\\]+)", RegexOptions.IgnoreCase);
        Match methodOverride = Regex.Match(text, @"X-HTTP-Method-Override:\s*(GET|POST|PUT|PATCH|DELETE)", RegexOptions.IgnoreCase);
        if (http.Success) return (methodOverride.Success ? methodOverride.Groups[1].Value.ToLowerInvariant() : http.Groups[1].Value.ToLowerInvariant(), http.Groups[2].Value);
        if (!text.Contains("curl", StringComparison.OrdinalIgnoreCase)) return null;
        Match url = Regex.Match(text, @"https://api\.linkedin\.com/[^\r\n\s'""\\]+");
        if (!url.Success) return null;
        Match method = Regex.Match(text, @"(?:-X|--request)\s+['"" ]*(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)", RegexOptions.IgnoreCase);
        return (methodOverride.Success ? methodOverride.Groups[1].Value.ToLowerInvariant() : method.Success ? method.Groups[1].Value.ToLowerInvariant() : Regex.IsMatch(text, @"--data|-d\s") ? "post" : "get", url.Value);
    }

    internal static string? NormalizePath(string url)
    {
        string path = url.Replace("https://api.linkedin.com", "", StringComparison.Ordinal);
        path = path.Split('?')[0].TrimEnd('/', '.', '`');
        if (!path.StartsWith("/rest/", StringComparison.Ordinal) && !path.StartsWith("/v2/", StringComparison.Ordinal)) return null;
        path = Regex.Replace(path, @"\{\{([^{}]+)\}\}|\{([^{}]+)\}|<([^<>]+)>", m => "{" + SchemaBuilder.Identifier(m.Groups.Values.Skip(1).First(g => g.Success).Value).Trim('_') + "}");
        string[] parts = path.Split('/');
        if (parts.Length < 3 || parts[2].Contains('{')) return null;
        for (int i = 3; i < parts.Length; i++)
        {
            if (Regex.IsMatch(parts[i], @"^\d+$|^\{[^{}]+\}$") || parts[i].StartsWith("urn:", StringComparison.OrdinalIgnoreCase) || parts[i].StartsWith("urn%3A", StringComparison.OrdinalIgnoreCase))
                parts[i] = "{" + SchemaBuilder.Identifier(parts[i - 1].Trim('{', '}')) + "Id}";
        }
        path = string.Join('/', parts);
        string withoutParameters = Regex.Replace(path, @"\{[^{}]+\}", "");
        return withoutParameters.IndexOfAny(['(', ')', '<', '>', '{', '}']) >= 0 ? null : path;
    }

    private static void AddParameters(JsonObject operation, string path, string url, string request, DocumentationPage page)
    {
        var parameters = (JsonArray)operation["parameters"]!;
        foreach (Match match in Regex.Matches(path, @"\{([^{}]+)\}")) AddParameter(parameters, match.Groups[1].Value, "path", true);
        if (url.Contains('?'))
            foreach (string pair in url[(url.IndexOf('?') + 1)..].Split('&'))
            {
                string name = pair.Split('=')[0];
                if (Regex.IsMatch(name, @"^[\w.\[\]]+$")) AddParameter(parameters, name, "query", false);
            }
        if (request.Contains("X-Restli-Protocol-Version", StringComparison.OrdinalIgnoreCase))
            AddParameter(parameters, "X-Restli-Protocol-Version", "header", true, "2.0.0");
        Match restliMethod = Regex.Match(request, @"X-Restli-Method:\s*([\w_]+)", RegexOptions.IgnoreCase);
        if (restliMethod.Success) AddParameter(parameters, "X-Restli-Method", "header", false, restliMethod.Groups[1].Value);
        if (path.StartsWith("/rest/", StringComparison.Ordinal) && request.Contains("Linkedin-Version", StringComparison.OrdinalIgnoreCase))
            AddParameter(parameters, "Linkedin-Version", "header", true, page.Version.StartsWith("li-lms-", StringComparison.Ordinal) ? page.Version[7..].Replace("-", "") : null);
    }

    private static void AddParameter(JsonArray parameters, string name, string location, bool required, string? defaultValue = null)
    {
        if (parameters.Any(p => p!["name"]!.ToString() == name && p["in"]!.ToString() == location)) return;
        var schema = new JsonObject { ["type"] = "string" };
        if (defaultValue != null) schema["default"] = defaultValue;
        parameters.Add(new JsonObject { ["name"] = name, ["in"] = location, ["required"] = required, ["schema"] = schema });
    }

    private static void AddDocumentedParameters(JsonObject operation, DocumentationPage page, DocumentationBlock request)
    {
        foreach (DocumentationBlock table in page.Blocks.Where(b => b.Kind == "table" && b.Section.Contains("query parameter", StringComparison.OrdinalIgnoreCase)))
        {
            if (table.SectionPath.Count > 0 && request.SectionPath.Count > 0 && table.SectionPath[0] != request.SectionPath[0]) continue;
            int column = SchemaBuilder.Column(table, "parameter", "parameter name", "name", "field");
            if (column < 0) continue;
            foreach (List<DocumentationCell> row in table.Rows)
                if (row.Count > column && Regex.IsMatch(row[column].Text, @"^[\w.]+$"))
                    AddParameter((JsonArray)operation["parameters"]!, row[column].Text, "query", false);
        }
    }

    private static JsonObject Response(JsonObject operation, string status)
    {
        var responses = (JsonObject)operation["responses"]!;
        if (responses[status] is not JsonObject response)
            responses[status] = response = new JsonObject { ["description"] = "Documented response. See x-source-url." };
        return response;
    }

    private static void AddBody(JsonObject operation, JsonObject schema, bool request, string status)
    {
        JsonObject target;
        if (request)
        {
            if (operation["requestBody"] is not JsonObject body) operation["requestBody"] = body = new JsonObject();
            target = body;
        }
        else target = Response(operation, status);
        if (target["content"] is not JsonObject content) target["content"] = content = new JsonObject();
        if (content["application/json"] is not JsonObject media) content["application/json"] = media = new JsonObject();
        media["schema"] = media["schema"] is JsonObject old ? SchemaBuilder.Merge((JsonObject)old.DeepClone(), schema) : schema;
    }

    private static void AddString(JsonObject target, string key, string value)
    {
        if (target[key] is not JsonArray values) target[key] = values = new JsonArray();
        if (!values.Any(v => v!.ToString() == value)) values.Add(value);
    }

    internal static JsonNode? ExtractJson(string text)
    {
        // Parse a complete JSON value, never infer requiredness/enums from an example.
        for (int start = 0; start < text.Length; start++)
        {
            if (text[start] is not ('{' or '[')) continue;
            int depth = 0;
            bool quoted = false;
            bool escaped = false;
            for (int end = start; end < text.Length; end++)
            {
                char c = text[end];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth--;
                if (depth != 0) continue;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(text[start..(end + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    return MaterializeExample(document.RootElement);
                }
                catch (JsonException) { break; }
            }
        }
        return null;
    }

    private static JsonNode? MaterializeExample(JsonElement element)
    {
        // Documentation can repeat property names. Materialize recursively with the last
        // value winning so JsonObject's lazy dictionary cannot fail during schema inference.
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (JsonProperty property in element.EnumerateObject())
                result[property.Name] = MaterializeExample(property.Value);
            return result;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (JsonElement item in element.EnumerateArray())
                result.Add(MaterializeExample(item));
            return result;
        }
        return JsonNode.Parse(element.GetRawText());
    }
}
