using Cordis.Composition;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Cordis.JavaScript;

/// <summary>
/// Optional real JavaScript expressions. Each evaluation receives a fresh engine and a live
/// context view. This is trusted configuration execution, not a malicious-code sandbox.
/// CLR views and functions are supported; Node modules and Node globals are not provided.
/// </summary>
public sealed class JintExpressionEvaluator : IExpressionEvaluator
{
    /// <summary>
    /// Gets the timeout value.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Gets the max statements value.
    /// </summary>
    public int MaxStatements { get; init; } = 100_000;
    /// <summary>
    /// Gets the configure value.
    /// </summary>
    public Action<Engine>? Configure { get; init; }

    /// <summary>
    /// Evaluates the requested value.
    /// </summary>
    public object? Evaluate(string expression, Context context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        var engine = new Engine(options => options.TimeoutInterval(Timeout).MaxStatements(MaxStatements));
        engine.SetValue("__cordisGet", (Func<string, object?>)(name =>
        {
            var value = context.Reflect.Read(name);
            return value is Undefined ? JsValue.Undefined : value;
        }));
        engine.SetValue("__cordisHas", (Func<string, bool>)(name => context.Reflect.Has(name)));
        engine.SetValue("__cordisSet", (Action<string, JsValue>)((name, value) =>
            context.Set(name, ConvertResult(value, new(ReferenceEqualityComparer.Instance)))));
        Configure?.Invoke(engine);
        // Preserve the upstream with(ctx) scope: service names may be unqualified, while builtins
        // and missing identifiers retain JavaScript lookup and ReferenceError behavior.
        engine.Execute("""
            const ctx = new Proxy(Object.create(null), {
              has: (_, key) => typeof key === 'string' && __cordisHas(key),
              get: (_, key) => typeof key === 'string' && __cordisHas(key) ? __cordisGet(key) : undefined,
              set: (_, key, value) => { __cordisSet(key, value); return true; }
            });
            function __cordisEvaluate(source) { with (ctx) { return eval(source); } }
            """);
        engine.SetValue("__cordisSource", expression);
        try { return ConvertResult(engine.Evaluate("__cordisEvaluate(__cordisSource)"), new(ReferenceEqualityComparer.Instance)); }
        catch (Jint.Runtime.JavaScriptException error)
        {
            var name = error.Error.IsObject() ? error.Error.AsObject().Get("name").ToString() : "Error";
            throw new JavaScriptExpressionException(name, error.Message, error);
        }
    }

    private static object? ConvertResult(JsValue value, Dictionary<object, object> seen)
    {
        if (value.IsUndefined()) return Undefined.Value;
        if (value.IsNull()) return null;
        if (!value.IsObject()) return value.ToObject();
        var instance = value.AsObject();
        if (seen.TryGetValue(instance, out var previous)) return previous;
        if (value.IsArray())
        {
            var result = new List<object?>();
            seen.Add(instance, result);
            var length = (uint)instance.Get("length").AsNumber();
            for (uint i = 0; i < length; i++) result.Add(ConvertResult(instance.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), seen));
            return result;
        }
        // Plain objects form the YAML-compatible data domain. Native objects, functions and CLR
        // wrappers use Jint's native conversion instead of being flattened into configuration.
        if (instance is not JsObject && instance.GetType() != typeof(ObjectInstance)) return value.ToObject();
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        seen.Add(instance, map);
        foreach (var pair in instance.GetOwnProperties())
            if (pair.Key.IsString() && pair.Value.Enumerable)
                map[pair.Key.AsString()] = ConvertResult(instance.Get(pair.Key), seen);
        return map;
    }
}
