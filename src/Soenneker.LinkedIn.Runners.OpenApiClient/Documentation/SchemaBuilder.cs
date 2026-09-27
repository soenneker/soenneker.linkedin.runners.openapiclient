using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;

internal sealed class SchemaBuilder
{
    private readonly JsonObject _schemas = new();
    private readonly Dictionary<string, string> _references = new(StringComparer.Ordinal);
    private readonly List<(DocumentationPage page, DocumentationBlock table, string name)> _tables = [];
    private readonly List<DocumentationIssue> _issues;

    public SchemaBuilder(IReadOnlyList<DocumentationPage> pages, List<DocumentationIssue> issues)
    {
        _issues = issues;
        foreach (DocumentationPage page in pages.OrderBy(p => p.Url, StringComparer.Ordinal))
        {
            foreach (DocumentationBlock table in page.Blocks.Where(b => b.Kind == "table"))
            {
                if (Column(table, "field", "field name", "name") < 0 || Column(table, "type", "format", "data type") < 0)
                    continue;
                string key = Key(page.Url, table.Anchor);
                string name = Identifier(new Uri(page.Url).AbsolutePath["/en-us/linkedin/".Length..] + "_" + table.Anchor);
                if (_schemas.ContainsKey(name)) name += "_" + _tables.Count;
                _references.TryAdd(key, name);
                _schemas[name] = new JsonObject();
                _tables.Add((page, table, name));
            }
        }
        foreach ((DocumentationPage page, DocumentationBlock table, string name) in _tables)
            _schemas[name] = FromTable(page, table);
    }

    public JsonObject Schemas => _schemas;

    public JsonObject ForExample(JsonNode? value, DocumentationPage page, bool request)
    {
        if (value is JsonObject obj)
        {
            // Only consider definitions in this article or definitions explicitly linked by it.
            var candidates = _tables.Where(t => t.page.Url == page.Url || page.Links.Contains(t.page.Url, StringComparer.Ordinal))
                .Select(t => (t.name, schema: (JsonObject)_schemas[t.name]!, score: Score((JsonObject)_schemas[t.name]!, obj)))
                .Where(t => t.score > 0).OrderByDescending(t => t.score).ThenBy(t => t.name, StringComparer.Ordinal).ToList();
            if (candidates.Count > 0 && (candidates.Count == 1 || candidates[0].score > candidates[1].score))
            {
                var result = (JsonObject)candidates[0].schema.DeepClone();
                result["x-linkedin-schema"] = candidates[0].name;
                var properties = (JsonObject)result["properties"]!;
                foreach ((string name, JsonNode? sample) in obj)
                    if (!properties.ContainsKey(name)) properties[name] = ForExample(sample, page, request);
                if (request) ApplyCreateRequirements(result);
                return result;
            }
            var inferred = new JsonObject();
            foreach ((string name, JsonNode? child) in obj.OrderBy(x => x.Key, StringComparer.Ordinal))
                inferred[name] = ForExample(child, page, request);
            return new JsonObject { ["type"] = "object", ["properties"] = inferred, ["x-linkedin-inferred-from-example"] = true };
        }
        if (value is JsonArray array)
        {
            JsonObject? items = null;
            foreach (JsonNode? item in array)
                items = items == null ? ForExample(item, page, request) : Merge(items, ForExample(item, page, request));
            return new JsonObject { ["type"] = "array", ["items"] = items ?? new JsonObject(), ["x-linkedin-inferred-from-example"] = true };
        }
        if (value is JsonValue scalar)
        {
            string kind = scalar.GetValueKind().ToString();
            return new JsonObject { ["type"] = kind switch { "True" or "False" => "boolean", "Number" => "number", _ => "string" }, ["x-linkedin-inferred-from-example"] = true };
        }
        return new JsonObject { ["x-linkedin-inferred-from-example"] = true };
    }

    private static int Score(JsonObject schema, JsonObject example)
    {
        if (schema["properties"] is not JsonObject props || example.Count == 0) return 0;
        int matched = example.Count(p => props.ContainsKey(p.Key));
        // A single generic field such as id is not evidence that two entities have the same schema.
        return matched >= Math.Min(2, example.Count) && matched * 2 >= example.Count ? matched * 100 / example.Count + matched : 0;
    }

    private JsonObject FromTable(DocumentationPage page, DocumentationBlock table)
    {
        int fieldIndex = Column(table, "field", "field name", "name");
        int typeIndex = Column(table, "type", "format", "data type");
        int descriptionIndex = Column(table, "description", "definition");
        int requiredIndex = Column(table, "required", "required/optional", "optional");
        var properties = new JsonObject();
        var required = new JsonArray();
        var createRequired = new JsonArray();
        foreach (List<DocumentationCell> row in table.Rows)
        {
            if (row.Count <= Math.Max(fieldIndex, typeIndex)) continue;
            string name = row[fieldIndex].Text.Trim().Trim('`', '*');
            if (!Regex.IsMatch(name, @"^[A-Za-z_][\w.]*$"))
            {
                _issues.Add(new(page.Url, table.Section, $"Unrecognized field name '{name}' was not converted."));
                continue;
            }
            string description = Cell(row, descriptionIndex).Text;
            string requirement = Cell(row, requiredIndex).Text;
            JsonObject schema = Type(row[typeIndex], page, table);
            if (schema.Count == 0 && Regex.IsMatch(description, @"^(long|int|integer|boolean|string|enum string|time)$", RegexOptions.IgnoreCase))
                schema = Type(new DocumentationCell { Text = description }, page, table);
            schema["description"] = string.Join("\n\n", new[] { description, "Documented type: " + row[typeIndex].Text, requirement.Length > 0 ? "Requirement: " + requirement : "" }.Where(s => s.Length > 0));
            if (Regex.IsMatch(requirement + " " + description, @"\bread[- ]only\b", RegexOptions.IgnoreCase)) schema["readOnly"] = true;
            if (Regex.IsMatch(requirement, @"\bcreate[- ]only\b", RegexOptions.IgnoreCase)) schema["x-linkedin-create-only"] = true;
            if (Regex.IsMatch(requirement, @"^(required|yes|true)$", RegexOptions.IgnoreCase)) required.Add(name);
            else if (Regex.IsMatch(requirement, @"^create[- ]only required$", RegexOptions.IgnoreCase)) createRequired.Add(name);
            else if (requiredIndex >= 0 && table.Headers[requiredIndex].Equals("optional", StringComparison.OrdinalIgnoreCase) && requirement.Equals("no", StringComparison.OrdinalIgnoreCase)) required.Add(name);
            if (schema["type"] == null && schema["allOf"] == null && schema["oneOf"] == null)
                _issues.Add(new(page.Url, table.Section, $"Type for {name} needs review: {row[typeIndex].Text}"));
            AddProperty(properties, name, schema);
        }
        var result = new JsonObject { ["type"] = "object", ["properties"] = properties, ["x-source-url"] = page.Url + "#" + table.Anchor };
        if (required.Count > 0) result["required"] = required;
        if (createRequired.Count > 0) result["x-linkedin-create-required"] = createRequired;
        return result;
    }

    private JsonObject Type(DocumentationCell cell, DocumentationPage page, DocumentationBlock table)
    {
        string type = cell.Text.Trim();
        string lower = type.ToLowerInvariant();
        bool array = Regex.IsMatch(lower, @"\barray\b|\blist\b|\[\]");
        foreach (string link in cell.Links)
        {
            var uri = new Uri(link);
            if (_references.TryGetValue(Key(link, uri.Fragment.TrimStart('#')), out string? name))
            {
                var reference = new JsonObject { ["$ref"] = "#/components/schemas/" + name };
                return array ? new JsonObject { ["type"] = "array", ["items"] = reference } : new JsonObject { ["allOf"] = new JsonArray(reference) };
            }
        }
        string[] values = cell.Values.Select(v => Regex.Match(v, @"^([A-Z][A-Z0-9_]*)(?:\s|$|[-:])"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length > 1)
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
        if (array)
        {
            string element = Regex.Replace(type, @"(?i)\b(array|list)\b\s*(of)?|\[\]|[<>]", "").Trim();
            return new JsonObject { ["type"] = "array", ["items"] = element.Length > 0 ? Type(new DocumentationCell { Text = element }, page, table) : new JsonObject() };
        }
        string localType = Regex.Replace(type, @"(?i)\b(optional|type)\b", "").Trim();
        var local = _tables.Where(t => t.page.Url == page.Url &&
            (t.table.Section.Equals(localType, StringComparison.OrdinalIgnoreCase) || t.table.Anchor.Equals(localType, StringComparison.OrdinalIgnoreCase))).ToList();
        if (local.Count == 1) return new JsonObject { ["allOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/components/schemas/" + local[0].name }) };
        if (Regex.IsMatch(lower, @"\burn\b|urn$|\bstring\b|\btext\b|\burl\b|\buri\b|\benum\b")) return new JsonObject { ["type"] = "string" };
        if (Regex.IsMatch(lower, @"\bboolean\b|^bool$")) return new JsonObject { ["type"] = "boolean" };
        if (Regex.IsMatch(lower, @"\blong\b|\bint64\b|\bepoch\b|^time$")) return new JsonObject { ["type"] = "integer", ["format"] = "int64" };
        if (Regex.IsMatch(lower, @"\binteger\b|\bint\b|\bint32\b")) return new JsonObject { ["type"] = "integer", ["format"] = "int32" };
        if (Regex.IsMatch(lower, @"\bdouble\b|\bfloat\b|\bdecimal\b|^number$")) return new JsonObject { ["type"] = "number" };
        if (lower is "object" or "record" or "map") return new JsonObject { ["type"] = "object" };
        return new JsonObject();
    }

    private static void ApplyCreateRequirements(JsonObject schema)
    {
        if (schema["x-linkedin-create-required"] is JsonArray create)
        {
            var required = schema["required"] as JsonArray ?? new JsonArray();
            if (required.Parent == null) schema["required"] = required;
            foreach (JsonNode? name in create)
                if (!required.Any(n => JsonNode.DeepEquals(n, name))) required.Add(name!.DeepClone());
        }
    }

    private static void AddProperty(JsonObject properties, string name, JsonObject schema)
    {
        string[] parts = name.Split('.');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (properties[parts[i]] is not JsonObject parent)
            {
                parent = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                properties[parts[i]] = parent;
            }
            if (parent["properties"] is not JsonObject nested) parent["properties"] = nested = new JsonObject();
            properties = nested;
        }
        properties[parts[^1]] = schema;
    }

    public static JsonObject Merge(JsonObject left, JsonObject right)
    {
        if (JsonNode.DeepEquals(left, right)) return left;
        if (left["type"]?.ToString() == "object" && right["type"]?.ToString() == "object" && left["properties"] is JsonObject lp && right["properties"] is JsonObject rp)
        {
            foreach ((string key, JsonNode? value) in rp)
                lp[key] = lp[key] is JsonObject old && value is JsonObject incoming ? Merge((JsonObject)old.DeepClone(), (JsonObject)incoming.DeepClone()) : value?.DeepClone();
            // Fields required by only one variant are not required by the union.
            if (left["required"] is JsonArray required)
            {
                var other = right["required"] as JsonArray;
                left["required"] = new JsonArray(required.Where(n => other?.Any(o => JsonNode.DeepEquals(n, o)) == true).Select(n => n!.DeepClone()).ToArray());
                if (((JsonArray)left["required"]!).Count == 0) left.Remove("required");
            }
            return left;
        }
        if (left["type"]?.ToString() == "array" && right["type"]?.ToString() == "array")
        {
            left["items"] = Merge((JsonObject)left["items"]!.DeepClone(), (JsonObject)right["items"]!.DeepClone());
            return left;
        }
        var variants = left["anyOf"] as JsonArray ?? new JsonArray(left.DeepClone());
        if (!variants.Any(v => JsonNode.DeepEquals(v, right))) variants.Add(right.DeepClone());
        return new JsonObject { ["anyOf"] = variants.DeepClone() };
    }

    internal static int Column(DocumentationBlock table, params string[] names) => table.Headers.FindIndex(h => names.Contains(h.Trim(), StringComparer.OrdinalIgnoreCase));
    private static DocumentationCell Cell(List<DocumentationCell> row, int index) => index >= 0 && index < row.Count ? row[index] : new();
    private static string Key(string url, string anchor) => new Uri(url).GetLeftPart(UriPartial.Path) + "#" + anchor;
    internal static string Identifier(string value) => Regex.Replace(value, @"[^A-Za-z0-9_]", "_");
}
