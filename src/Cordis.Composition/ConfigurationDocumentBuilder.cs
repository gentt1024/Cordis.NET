using System.Globalization;
using System.Text.Json.Nodes;

namespace Cordis.Composition;

/// <summary>Compose projected declarations with the two static Loader document grammars.</summary>
internal sealed class ConfigurationDocumentBuilder
{
    private const string Definitions = "#/$defs/";

    private readonly JsonObject _definitions = JsonNode.Parse(
        """
        {
          "loaderExpression": {
            "type": "object", "required": ["__jsExpr"], "properties": { "__jsExpr": { "type": "string" } }
          },
          "entryMetadata": {
            "type": "object",
            "properties": {
              "id": { "type": "string" }, "name": { "type": "string" }, "config": {},
              "group": { "type": ["boolean", "null"] },
              "disabled": { "anyOf": [{ "type": ["boolean", "null"] }, { "$ref": "#/$defs/loaderExpression" }] },
              "inject": { "anyOf": [{ "type": "array", "items": { "type": "string" } }, { "type": ["object", "null"] }] },
              "intercept": { "type": ["object", "null"] },
              "isolate": { "type": ["object", "null"], "additionalProperties": { "anyOf": [{ "const": true }, { "type": "string" }] } }
            }
          },
          "entryList": { "type": "array", "items": { "$ref": "#/$defs/entry" } },
          "patchList": { "type": "array", "items": { "$ref": "#/$defs/patch" } },
          "unknownConfig": { "$comment": "No static input declaration is available." },
          "includeConfig": {
            "type": "object", "required": ["path"],
            "properties": {
              "path": { "type": "string" }, "initial": { "$ref": "#/$defs/entryList" },
              "patches": { "type": "array", "items": { "$ref": "#/$defs/includePatch" } }, "enableLogs": { "type": "boolean" }
            }
          },
          "includePatch": {
            "type": "object", "allOf": [{ "$ref": "#/$defs/entryMetadata" }], "properties": { "insert": { "$ref": "#/$defs/entryList" } }
          }
        }
        """)!.AsObject();

    private readonly Dictionary<string, List<string>> _names = new(StringComparer.Ordinal)
    {
        ["cordis:group"] = ["entryList"],
        ["cordis:include"] = ["includeConfig"]
    };

    private readonly Dictionary<string, (string Name, string Definition)> _targets = new(StringComparer.Ordinal);

    private readonly HashSet<string> _requiredConfigs = new(StringComparer.Ordinal)
    {
        "entryList",
        "includeConfig"
    };

    private readonly Dictionary<IPlugin, Dictionary<string, string>> _declarations =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, (JsonNode Schema, bool? AcceptsMissing, string Definition)> _projections =
        new(StringComparer.Ordinal);

    private int _declarationCount;
    private readonly JsonArray _entries = [];
    private readonly List<(string Level, string? Path, string Message)> _diagnostics = [];
    private bool _complete = true;

    internal void Add(
        string path,
        string? id,
        string? name,
        ConfigDescriptor? descriptor,
        string? tree,
        string status,
        bool target,
        IPlugin? owner = null)
    {
        var definition = tree switch
        {
            "group" => "entryList",
            "include" => "includeConfig",
            _ => "unknownConfig"
        };
        if (tree is null && descriptor is not null)
        {
            var limitations = new List<string>();
            ArgumentNullException.ThrowIfNull(owner);
            definition = AddDeclaration(owner, descriptor, limitations);
            status = limitations.Count > 0 ? "partial" : "schema";
            foreach (var message in limitations)
                Report("warning", path, message);
        }

        AddEntry(path, id, name, tree, status, target, definition);
    }

    internal void AddProjection(string path, string? id, string name, bool target, ResolvedConfigurationSchema projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projection.ModuleIdentity);
        if (projection.Schema is not { } export)
        {
            AddEntry(path, id, name, null, "absent", target, "unknownConfig");
            return;
        }

        if (export.Format != "json-schema-2020-12")
            throw new NotSupportedException("Expected a JSON Schema draft 2020-12 declaration.");
        var standalone = JsonNode.Parse(export.Document) ?? throw new FormatException("A schema cannot be null.");
        if (_projections.TryGetValue(projection.ModuleIdentity, out var existing))
        {
            if (existing.AcceptsMissing != projection.AcceptsMissing || !JsonNode.DeepEquals(existing.Schema, standalone))
                throw new FormatException("One resolved module identity returned inconsistent declarations.");
        }
        else
        {
            var embedded = ConfigurationSchemaResource.Embed(standalone, projection.ModuleIdentity);
            var definition = "config" + (_declarationCount++).ToString(CultureInfo.InvariantCulture);
            _definitions.Add(definition, new JsonObject
            {
                ["anyOf"] = new JsonArray(embedded, Ref("loaderExpression"))
            });
            if (projection.AcceptsMissing == false)
                _requiredConfigs.Add(definition);
            existing = (standalone, projection.AcceptsMissing, definition);
            _projections.Add(projection.ModuleIdentity, existing);
        }

        foreach (var message in export.Diagnostics)
            Report("warning", path, message);
        if (!export.Complete && export.Diagnostics.Count == 0)
            Report("warning", path, "The declaration provider reported a partial projection.");
        if (projection.AcceptsMissing is null)
            Report("warning", path, "The declaration does not describe whether a missing Config is accepted.");
        var status = export.Complete && projection.AcceptsMissing.HasValue ? "schema" : "partial";
        AddEntry(path, id, name, null, status, target, existing.Definition);
    }

    private void AddEntry(string path, string? id, string? name, string? tree, string status, bool target, string definition)
    {
        var entry = new JsonObject
        {
            ["path"] = path,
            ["status"] = status,
            ["configRef"] = Definitions + definition
        };
        if (id is not null)
            entry["id"] = id;
        if (tree is not null)
            entry["tree"] = tree;
        if (name is not null)
        {
            entry["name"] = name;
            if (!_names.TryGetValue(name, out var choices))
                _names.Add(name, choices = []);
            if (!choices.Contains(definition))
                choices.Add(definition);
        }

        if (target && !string.IsNullOrEmpty(id))
        {
            if (name is null)
                _targets.Remove(id);
            else
                _targets[id] = (name, definition);
        }

        _entries.Add((JsonNode)entry);
        if (status is "error" or "partial" or "unsupported")
            _complete = false;
    }

    internal void Report(string level, string? path, string message)
    {
        _diagnostics.Add((level, path, message));
        if (level == "error")
            _complete = false;
    }

    private string AddDeclaration(IPlugin owner, ConfigDescriptor descriptor, List<string> limitations)
    {
        var references = new List<JsonObject>();
        var projection = ConfigurationSchemaExporter.ProjectLoaderInput(descriptor, limitations, references);
        // One export may capture a fresh descriptor on each import. Equal shapes from different exports still have distinct identities.
        if (!_declarations.TryGetValue(owner, out var shapes))
            _declarations.Add(owner, shapes = new(StringComparer.Ordinal));
        var key = projection.ToJsonString();
        if (shapes.TryGetValue(key, out var existing))
            return existing;
        var name = "config" + (_declarationCount++).ToString(CultureInfo.InvariantCulture);
        foreach (var reference in references)
            if (reference["$ref"]?.GetValue<string>() is { } local)
                reference["$ref"] = Definitions + name + local[1..];
        shapes.Add(key, name);
        _definitions.Add(name, projection);
        if (!descriptor.IsOptional && !descriptor.HasDefault)
            _requiredConfigs.Add(name);
        return name;
    }

    internal ConfigurationSchemaExport Build(string profile)
    {
        var entryRules = new JsonArray(Ref("entryMetadata"));
        foreach (var (name, choices) in _names)
        {
            entryRules.Add((JsonNode)EntryRule(name, choices));
            if (choices.Count > 1)
            {
                Report(
                    "warning",
                    null,
                    $"Plugin '{name}' has multiple collected schemas; entry validation accepts their union.");
                _complete = false;
            }
        }

        _definitions["entry"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("name"),
            ["allOf"] = entryRules
        };
        // Includes have an independent id index. Only root patches receive the current target refinements.
        var patch = _definitions["includePatch"]!.DeepClone();
        foreach (var (id, target) in _targets)
            patch["allOf"]!.AsArray().Add((JsonNode)PatchRule(id, target.Name, target.Definition));
        _definitions["patch"] = patch;
        var diagnostics = new JsonArray();
        var messages = new List<string>();
        var positions = _entries
            .Select((entry, index) => (Path: entry!["path"]!.GetValue<string>(), Index: index))
            .ToDictionary(item => item.Path, item => item.Index, StringComparer.Ordinal);
        foreach (var (level, path, message) in _diagnostics.OrderBy(item =>
                     positions.GetValueOrDefault(item.Path ?? "", -1)))
        {
            var item = new JsonObject
            {
                ["level"] = level,
                ["message"] = message
            };
            if (path is not null)
                item["path"] = path;
            diagnostics.Add((JsonNode)item);
            messages.Add(path is null ? message : path + ": " + message);
        }

        var document = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = $"Cordis configuration for profile {profile}",
            ["$comment"] = "Patch config replaces a whole value. Target constraints describe the current root index. " +
                "Expressions and plugin validators run only when the application applies configuration.",
            ["type"] = "array",
            ["items"] = Ref("entry"),
            ["$defs"] = _definitions,
            ["x-cordis"] = new JsonObject
            {
                ["profile"] = profile,
                ["complete"] = _complete,
                ["entries"] = _entries,
                ["diagnostics"] = diagnostics,
                ["patchSchema"] = Definitions + "patchList"
            }
        };
        return new("json-schema-2020-12", document.ToJsonString(), _complete, messages.AsReadOnly());
    }

    private JsonObject EntryRule(string name, List<string> choices)
    {
        var references = new JsonArray(choices.Select(choice => (JsonNode?)Ref(choice)).ToArray());
        var validated = Properties(
            "config",
            new JsonObject
            {
                ["anyOf"] = references
            });
        JsonObject then = validated;
        if (choices.All(_requiredConfigs.Contains))
        {
            var active = validated.DeepClone();
            active["required"] = new JsonArray("config");
            then = new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["disabled"] = new JsonObject
                        {
                            ["anyOf"] = new JsonArray(
                                new JsonObject
                                {
                                    ["const"] = true
                                },
                                Ref("loaderExpression"))
                        },
                        ["group"] = new JsonObject
                        {
                            ["not"] = new JsonObject
                            {
                                ["const"] = true
                            }
                        }
                    },
                    ["required"] = new JsonArray("disabled")
                },
                ["else"] = active
            };
            if (!choices.All(choice => choice is "entryList" or "includeConfig"))
                then["then"] = validated;
        }

        var condition = Properties(
            "name",
            new JsonObject
            {
                ["const"] = name
            });
        condition["required"] = new JsonArray("name");
        return new()
        {
            ["if"] = condition,
            ["then"] = then
        };
    }

    private static JsonObject PatchRule(string id, string name, string definition)
    {
        var condition = new JsonObject
        {
            ["required"] = new JsonArray("id"),
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["const"] = id
                },
                ["name"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        new JsonObject
                        {
                            ["const"] = ""
                        },
                        new JsonObject
                        {
                            ["const"] = name
                        })
                }
            },
            ["not"] = new JsonObject
            {
                ["required"] = new JsonArray("insert")
            }
        };
        return new()
        {
            ["if"] = condition,
            ["then"] = Properties("config", Ref(definition))
        };
    }

    private static JsonObject Properties(string name, JsonObject constraint) =>
        new()
        {
            ["properties"] = new JsonObject
            {
                [name] = constraint
            }
        };

    private static JsonObject Ref(string name) => new()
    {
        ["$ref"] = Definitions + name
    };
}
