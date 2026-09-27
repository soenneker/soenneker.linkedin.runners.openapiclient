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
                bool fields = Column(table, "field", "field name", "name") >= 0 && Column(table, "type", "format", "data type") >= 0;
                bool enumeration = Column(table, "value", "enum value", "enum", "symbol", "enumeration name") >= 0;
                if (!fields && !enumeration)
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

    public JsonObject? Parameter(DocumentationPage page, string name)
    {
        foreach (DocumentationBlock table in page.Blocks.Where(b => b.Kind == "table"))
        {
            int nameIndex = Column(table, "parameter", "parameter name", "query parameter", "name", "field");
            int typeIndex = Column(table, "type", "format", "data type");
            if (nameIndex < 0 || typeIndex < 0) continue;
            foreach (List<DocumentationCell> row in table.Rows)
            {
                if (Cell(row, nameIndex).Text.Trim() != name) continue;
                JsonObject schema = Type(Cell(row, typeIndex), page, table);
                schema["description"] = Cell(row, Column(table, "description", "definition")).Text;
                schema["x-source-url"] = page.Url + "#" + table.Anchor;
                // Rest.li encodes complex query values with its own grammar, not OpenAPI's array/object styles.
                if (schema["type"]?.ToString() is "array" or "object" || schema["allOf"] != null)
                    return new JsonObject { ["type"] = "string", ["description"] = schema["description"]!.DeepClone(), ["x-linkedin-value-schema"] = schema };
                return schema;
            }
        }
        return null;
    }

    public JsonObject ForExample(JsonNode? value, DocumentationPage page, bool request, bool partial = false)
    {
        if (value is JsonObject obj)
        {
            if (obj.Count > 0 && obj.All(p => p.Key.StartsWith("urn:", StringComparison.Ordinal) || long.TryParse(p.Key, out _)))
            {
                JsonObject? values = null;
                foreach ((string _, JsonNode? item) in obj)
                    values = values == null ? ForExample(item, page, request, partial) : Merge(values, ForExample(item, page, request, partial));
                return new JsonObject { ["type"] = "object", ["additionalProperties"] = values, ["x-linkedin-inferred-from-example"] = true };
            }
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
                {
                    if (!properties.ContainsKey(name)) properties[name] = ForExample(sample, page, request, partial);
                    else if (properties[name] is JsonObject property && property["type"] == null && property["allOf"] == null)
                    {
                        JsonObject inferredProperty = ForExample(sample, page, request, partial);
                        foreach ((string key, JsonNode? definition) in property) inferredProperty[key] = definition?.DeepClone();
                        properties[name] = inferredProperty;
                    }
                }
                if (partial) result.Remove("required");
                else if (request) ApplyCreateRequirements(result);
                return result;
            }
            var inferred = new JsonObject();
            foreach ((string name, JsonNode? child) in obj.OrderBy(x => x.Key, StringComparer.Ordinal))
                inferred[name] = ForExample(child, page, request, partial);
            return new JsonObject { ["type"] = "object", ["properties"] = inferred, ["x-linkedin-inferred-from-example"] = true };
        }
        if (value is JsonArray array)
        {
            JsonObject? items = null;
            foreach (JsonNode? item in array)
                items = items == null ? ForExample(item, page, request, partial) : Merge(items, ForExample(item, page, request, partial));
            return new JsonObject { ["type"] = "array", ["items"] = items ?? new JsonObject(), ["x-linkedin-inferred-from-example"] = true };
        }
        if (value is JsonValue scalar)
        {
            string kind = scalar.GetValueKind().ToString();
            return new JsonObject { ["type"] = kind switch { "True" or "False" => "boolean", "Number" => scalar.TryGetValue<long>(out _) ? "integer" : "number", _ => "string" }, ["x-linkedin-inferred-from-example"] = true };
        }
        return new JsonObject { ["x-linkedin-inferred-from-example"] = true };
    }

    private static int Score(JsonObject schema, JsonObject example)
    {
        if (schema["properties"] is not JsonObject props || example.Count == 0) return 0;
        int matched = example.Count(p => props.ContainsKey(p.Key));
        // A single generic field such as id is not evidence that two entities have the same schema.
        return matched >= 2 && matched * 2 >= example.Count ? matched * 100 / example.Count + matched : 0;
    }

    private JsonObject FromTable(DocumentationPage page, DocumentationBlock table)
    {
        int enumIndex = Column(table, "value", "enum value", "enum", "symbol", "enumeration name");
        if (enumIndex >= 0)
        {
            string[] values = table.Rows.Where(r => r.Count > enumIndex).Select(r => r[enumIndex].Text.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["x-source-url"] = page.Url + "#" + table.Anchor };
        }
        int fieldIndex = Column(table, "field", "field name", "name");
        int typeIndex = Column(table, "type", "format", "data type");
        int descriptionIndex = Column(table, "description", "definition");
        int requiredIndex = Column(table, "required", "required/optional", "optional");
        int subFieldIndex = Column(table, "sub-field", "subfield", "sub field");
        string parentField = "";
        var properties = new JsonObject();
        var required = new JsonArray();
        var createRequired = new JsonArray();
        foreach (List<DocumentationCell> row in table.Rows)
        {
            if (row.Count <= Math.Max(fieldIndex, typeIndex)) continue;
            string name = row[fieldIndex].Text.Trim().Trim('`', '*');
            if (subFieldIndex >= 0)
            {
                if (name.Length > 0) parentField = name;
                string child = Cell(row, subFieldIndex).Text;
                if (child.Length > 0) name = parentField + "." + child;
            }
            if (!Regex.IsMatch(name, @"^[A-Za-z_][\w.]*$"))
            {
                _issues.Add(new(page.Url, table.Section, $"Unrecognized field name '{name}' was not converted."));
                continue;
            }
            string description = Cell(row, descriptionIndex).Text;
            string requirement = Cell(row, requiredIndex).Text;
            var typeCell = new DocumentationCell { Text = row[typeIndex].Text, Values = row[typeIndex].Values.Concat(Cell(row, descriptionIndex).Values).Distinct(StringComparer.Ordinal).ToList(),
                Links = row[typeIndex].Links.Concat(row[fieldIndex].Links).Concat(Cell(row, descriptionIndex).Links).Distinct(StringComparer.Ordinal).ToList() };
            JsonObject schema = Type(typeCell, page, table);
            if (schema.Count == 0 && Regex.IsMatch(description, @"^(long|int|integer|boolean|string|enum string|time|array of\b)", RegexOptions.IgnoreCase))
                schema = Type(new DocumentationCell { Text = description, Links = Cell(row, descriptionIndex).Links, Values = typeCell.Values }, page, table);
            schema["description"] = string.Join("\n\n", new[] { description, "Documented type: " + row[typeIndex].Text, requirement.Length > 0 ? "Requirement: " + requirement : "" }.Where(s => s.Length > 0));
            if (Regex.IsMatch(requirement, @"^read[- ]only$", RegexOptions.IgnoreCase) || Regex.IsMatch(description, @"(?:^|\.\s+)read[- ]only(?:\.|$)", RegexOptions.IgnoreCase)) schema["readOnly"] = true;
            if (Regex.IsMatch(requirement, @"\bcreate[- ]only\b", RegexOptions.IgnoreCase)) schema["x-linkedin-create-only"] = true;
            Match createEnum = Regex.Match(row[typeIndex].Text + " " + description, @"\b([A-Z][A-Z0-9_]*) is the only accepted (?:field|value) during creation\b");
            if (createEnum.Success) schema["x-linkedin-create-enum"] = new JsonArray(createEnum.Groups[1].Value);
            if (Regex.IsMatch(requirement, @"^(required|yes|true)$", RegexOptions.IgnoreCase) && !name.Contains('.')) required.Add(name);
            else if (Regex.IsMatch(requirement, @"^create[- ]only required$", RegexOptions.IgnoreCase) && !name.Contains('.')) createRequired.Add(name);
            else if (requiredIndex >= 0 && table.Headers[requiredIndex].Equals("optional", StringComparison.OrdinalIgnoreCase) && requirement.Equals("no", StringComparison.OrdinalIgnoreCase) && !name.Contains('.')) required.Add(name);
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
        if (array)
        {
            string element = Regex.Replace(type, @"(?i)\b(array|list)\b\s*(of)?|\[\]|[<>]", "").Trim();
            if (Regex.IsMatch(element, @"(?i)^(?:optional\s+)?(?:string|boolean|bool|int|integer|long|double|float|number|urn|url|uri)\b|urn$"))
                return new JsonObject { ["type"] = "array", ["items"] = Type(new DocumentationCell { Text = element, Values = cell.Values }, page, table) };
        }
        bool primitive = Regex.IsMatch(lower, @"^(?:optional\s+)?(?:string|boolean|bool|int|integer|long|double|float|number|urn|url|uri)\b") || lower.EndsWith("urn", StringComparison.Ordinal);
        foreach (string link in cell.Links)
        {
            if (primitive && !array) break;
            var uri = new Uri(link);
            string? name = null;
            if (!_references.TryGetValue(Key(link, uri.Fragment.TrimStart('#')), out name) && uri.Fragment.Length == 0)
            {
                var linked = _tables.Where(t => new Uri(t.page.Url).AbsolutePath == uri.AbsolutePath).ToArray();
                if (linked.Length == 1) name = linked[0].name;
                else name = linked.FirstOrDefault(t => NormalizeTypeName(t.table.Section) == NormalizeTypeName(type)).name;
            }
            if (name != null)
            {
                var reference = new JsonObject { ["$ref"] = "#/components/schemas/" + name };
                return array ? new JsonObject { ["type"] = "array", ["items"] = reference } : new JsonObject { ["allOf"] = new JsonArray(reference) };
            }
        }
        string[] values = cell.Values.Select(v => Regex.Match(v, @"^([A-Z][A-Z0-9_]*)(?:\s|$|[-:])"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length > 1)
        {
            var enumeration = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
            return array ? new JsonObject { ["type"] = "array", ["items"] = enumeration } : enumeration;
        }
        if (array)
        {
            string element = Regex.Replace(type, @"(?i)\b(array|list)\b\s*(of)?|\[\]|[<>]", "").Trim();
            return new JsonObject { ["type"] = "array", ["items"] = element.Length > 0 ? Type(new DocumentationCell { Text = element }, page, table) : new JsonObject() };
        }
        string localType = Regex.Replace(type, @"(?i)\b(optional|type)\b", "").Trim();
        var local = _tables.Where(t => t.page.Url == page.Url &&
            (NormalizeTypeName(t.table.Section) == NormalizeTypeName(localType) || NormalizeTypeName(t.table.Anchor) == NormalizeTypeName(localType))).ToList();
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
        if (schema["properties"] is JsonObject properties)
            foreach ((string _, JsonNode? property) in properties)
                if (property is JsonObject field && field["x-linkedin-create-enum"] is JsonArray values)
                    field["enum"] = values.DeepClone();
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
    private static string Key(string url, string anchor) => new Uri(DocumentationUrl.Normalize(url) ?? url).GetLeftPart(UriPartial.Path) + "#" + anchor;
    internal static string Identifier(string value) => Regex.Replace(value, @"[^A-Za-z0-9_]", "_");
    private static string NormalizeTypeName(string value) => Regex.Replace(Regex.Replace(value, @"(?i)\b(optional|type|schema|api)\b", ""), @"[^A-Za-z0-9]", "").ToLowerInvariant();
}
