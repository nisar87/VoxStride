using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VoxStride.Core.Json;

/// <summary>
/// Strict validator for the JSON-schema subset used by canonical tool definitions:
/// object/string/integer/number/boolean/array, properties, required, additionalProperties:false,
/// enum, pattern, minLength/maxLength, minimum/maximum, items, maxItems.
/// Unknown fields are rejected when additionalProperties is false (the canonical default).
/// </summary>
public static class SchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonNode? value, JsonObject schema, string path = "$")
    {
        var errors = new List<string>();
        ValidateNode(value, schema, path, errors);
        return errors;
    }

    private static void ValidateNode(JsonNode? value, JsonObject schema, string path, List<string> errors)
    {
        var type = schema["type"]?.GetValue<string>();
        if (value is null)
        {
            errors.Add($"{path}: null is not allowed");
            return;
        }

        if (schema["enum"] is JsonArray allowed)
        {
            var text = value.ToJsonString();
            if (!allowed.Any(a => a is not null && a.ToJsonString() == text))
                errors.Add($"{path}: value not in enum");
        }

        switch (type)
        {
            case "object":
                if (value is not JsonObject obj) { errors.Add($"{path}: expected object"); return; }
                var props = schema["properties"] as JsonObject ?? new JsonObject();
                if (schema["required"] is JsonArray req)
                    foreach (var r in req)
                    {
                        var name = r!.GetValue<string>();
                        if (!obj.ContainsKey(name)) errors.Add($"{path}.{name}: required");
                    }
                var additionalAllowed = schema["additionalProperties"] is JsonValue av && av.GetValueKind() == JsonValueKind.True;
                foreach (var (k, v) in obj)
                {
                    if (props[k] is JsonObject ps) ValidateNode(v, ps, $"{path}.{k}", errors);
                    else if (!additionalAllowed) errors.Add($"{path}.{k}: unknown field");
                }
                break;
            case "string":
                if (value is not JsonValue sv || sv.GetValueKind() != JsonValueKind.String) { errors.Add($"{path}: expected string"); return; }
                var s = sv.GetValue<string>();
                if (schema["maxLength"] is JsonValue mx && s.Length > Num(mx)) errors.Add($"{path}: too long");
                if (schema["minLength"] is JsonValue mn && s.Length < Num(mn)) errors.Add($"{path}: too short");
                if (schema["pattern"] is JsonValue pat && !Regex.IsMatch(s, pat.GetValue<string>(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    errors.Add($"{path}: does not match pattern");
                break;
            case "integer":
            case "number":
                if (value is not JsonValue nv || nv.GetValueKind() != JsonValueKind.Number) { errors.Add($"{path}: expected {type}"); return; }
                var d = Num(nv);
                if (type == "integer" && Math.Abs(d % 1) > double.Epsilon) errors.Add($"{path}: expected integer");
                if (schema["minimum"] is JsonValue min && d < Num(min)) errors.Add($"{path}: below minimum");
                if (schema["maximum"] is JsonValue max && d > Num(max)) errors.Add($"{path}: above maximum");
                break;
            case "boolean":
                if (value is not JsonValue bv || (bv.GetValueKind() != JsonValueKind.True && bv.GetValueKind() != JsonValueKind.False))
                    errors.Add($"{path}: expected boolean");
                break;
            case "array":
                if (value is not JsonArray arr) { errors.Add($"{path}: expected array"); return; }
                if (schema["maxItems"] is JsonValue mi && arr.Count > Num(mi)) errors.Add($"{path}: too many items");
                if (schema["items"] is JsonObject itemSchema)
                    for (var i = 0; i < arr.Count; i++) ValidateNode(arr[i], itemSchema, $"{path}[{i}]", errors);
                break;
            default:
                errors.Add($"{path}: unsupported schema type '{type}'");
                break;
        }
    }

    private static double Num(JsonValue v) => double.Parse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses raw model-produced arguments. Incomplete or non-object JSON is rejected, never repaired.</summary>
    public static bool TryParseArguments(string raw, out JsonObject? args, out string? error)
    {
        args = null; error = null;
        if (string.IsNullOrWhiteSpace(raw)) { args = new JsonObject(); return true; }
        try
        {
            var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
            if (node is JsonObject o) { args = o; return true; }
            error = "arguments must be a JSON object";
            return false;
        }
        catch (JsonException ex)
        {
            error = "malformed arguments: " + ex.Message;
            return false;
        }
    }
}

/// <summary>Small helpers for building canonical schemas in code.</summary>
public static class Schema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (n, s, r) in props) { p[n] = s; if (r) req.Add(n); }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req, ["additionalProperties"] = false };
    }
    public static JsonObject Str(string description, string? pattern = null, int maxLength = 4096, params string[] enumValues)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };
        if (pattern is not null) o["pattern"] = pattern;
        if (enumValues.Length > 0) o["enum"] = new JsonArray(enumValues.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        return o;
    }
    public static JsonObject Int(string description, int min, int max) =>
        new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };
    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
    public static JsonObject Array(string description, JsonObject items, int maxItems) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items, ["maxItems"] = maxItems };
}
