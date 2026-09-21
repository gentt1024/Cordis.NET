using System.Reflection;

var root = Environment.CurrentDirectory;
var baseline = Path.Combine(root, "docs", "public-api.txt");
var names = new[] { "Cordis.Core", "Cordis.Composition", "Cordis.Extensions", "Cordis.Clr", "Cordis.Hosting", "Cordis.JavaScript" };
var lines = new List<string>();
foreach (var name in names)
{
    var assembly = Assembly.Load(name);
    foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
    {
        lines.Add($"{name}: {type.FullName} : {type.BaseType} [{string.Join(",", type.GetInterfaces().Select(t => t.ToString()).Order(StringComparer.Ordinal))}] flags={type.Attributes}" + Details(type));
        foreach (var member in type.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                     .Where(m => m is not Type && m is not MethodInfo { IsSpecialName: true }).OrderBy(m => m.ToString(), StringComparer.Ordinal))
            lines.Add(("  " + member.MemberType + " " + member + Details(member)).TrimEnd());
    }
}
var current = string.Join('\n', lines) + "\n";
if (args is ["--accept"]) { File.WriteAllText(baseline, current); Console.WriteLine("Public API snapshot updated; review this diff before committing."); return 0; }
if (!File.Exists(baseline) || File.ReadAllText(baseline).Replace("\r\n", "\n", StringComparison.Ordinal) != current)
{
    Console.Error.WriteLine("Public API changed. Review and run Cordis.ApiCheck --accept intentionally.");
    return 1;
}
Console.WriteLine("Public API matches checked-in snapshot.");
return 0;

// Signature strings alone omit optional arguments, nullable contracts and setters. Keep
// those source-compatibility details visible too; this initial snapshot is not a claim of
// binary compatibility with an earlier stable release.
static string Details(MemberInfo member)
{
    var nullability = new NullabilityInfoContext();
    static string Nullable(NullabilityInfo info) => $"{info.ReadState}/{info.WriteState}"
        + (info.ElementType is { } element ? "[" + Nullable(element) + "]" : "")
        + (info.GenericTypeArguments.Length > 0 ? "<" + string.Join(",", info.GenericTypeArguments.Select(Nullable)) + ">" : "");
    static string Attributes(IEnumerable<CustomAttributeData> attributes) => string.Join(",", attributes
        .Where(a => a.AttributeType.Namespace != "System.Runtime.CompilerServices" && a.AttributeType.Namespace != "System.Diagnostics")
        .Select(a => a.ToString()).Order(StringComparer.Ordinal));
    var details = " attrs=" + Attributes(member.CustomAttributes);
    if (member is Type type && type.IsGenericTypeDefinition)
        details += " constraints=" + string.Join(";", type.GetGenericArguments().Select(argument =>
            argument.Name + ":" + argument.GenericParameterAttributes + ":"
            + string.Join(",", argument.GetGenericParameterConstraints().Select(constraint => constraint.ToString()).Order(StringComparer.Ordinal))));
    if (member is MethodBase method)
    {
        details += " params=" + string.Join(";", method.GetParameters().Select(parameter =>
            parameter.Name + ":" + Nullable(nullability.Create(parameter))
            + (parameter.IsOptional ? " optional=" + System.Text.Json.JsonSerializer.Serialize(parameter.DefaultValue) : "")
            + " " + Attributes(parameter.CustomAttributes)));
        if (member is MethodInfo result)
            details += " return=" + Nullable(nullability.Create(result.ReturnParameter));
        if (method.IsGenericMethodDefinition)
            details += " constraints=" + string.Join(";", method.GetGenericArguments().Select(argument =>
                argument.Name + ":" + argument.GenericParameterAttributes + ":"
                + string.Join(",", argument.GetGenericParameterConstraints().Select(type => type.ToString()).Order(StringComparer.Ordinal))));
    }
    else if (member is PropertyInfo property)
        details += " " + Nullable(nullability.Create(property)) + " get=" + (property.GetMethod?.IsPublic == true)
            + " set=" + (property.SetMethod?.IsPublic == true)
            + " init=" + (property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true);
    else if (member is FieldInfo field)
        details += " " + Nullable(nullability.Create(field)) + " readonly=" + field.IsInitOnly
            + (field.IsLiteral ? " constant=" + System.Text.Json.JsonSerializer.Serialize(field.GetRawConstantValue()) : "");
    return details;
}
