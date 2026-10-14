using System.Reflection;

internal static class ApiSnapshot
{
    public static string Capture(IEnumerable<Assembly> assemblies)
    {
        var lines = new List<string>();
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name;
            foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                lines.Add(
                    $"{name}: {type.FullName} : {type.BaseType} [{string.Join(",", type.GetInterfaces().Select(t => t.ToString()).Order(StringComparer.Ordinal))}] flags={type.Attributes}" +
                    Details(type));
                foreach (var member in type
                             .GetMembers(
                                 BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance |
                                 BindingFlags.Static)
                             .Where(m => m is not Type && m is not MethodInfo { IsSpecialName: true })
                             .OrderBy(m => m.ToString(), StringComparer.Ordinal))
                    lines.Add(("  " + member.MemberType + " " + member + Details(member)).TrimEnd());
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    // Signature strings alone omit optional arguments, nullable contracts and setters. Keep
    // those source-compatibility details visible too; this initial snapshot is not a claim of
    // binary compatibility with an earlier stable release.
    static string MethodFlags(MethodBase method) =>
        $"virtual={method.IsVirtual},abstract={method.IsAbstract},final={method.IsFinal},newSlot={(method.Attributes & MethodAttributes.NewSlot) != 0}";

    static string Details(MemberInfo member)
    {
        var nullability = new NullabilityInfoContext();

        static string Nullable(NullabilityInfo info) =>
            $"{info.ReadState}/{info.WriteState}" +
            (info.ElementType is { } element ? "[" + Nullable(element) + "]" : "") +
            (info.GenericTypeArguments.Length > 0
                ? "<" + string.Join(",", info.GenericTypeArguments.Select(Nullable)) + ">"
                : "");

        static string Attributes(IEnumerable<CustomAttributeData> attributes) =>
            string.Join(
                ",",
                attributes
                    .Where(a =>
                        a.AttributeType.Namespace != "System.Runtime.CompilerServices" &&
                        a.AttributeType.Namespace != "System.Diagnostics")
                    .Select(a => a.ToString())
                    .Order(StringComparer.Ordinal));

        var details = " attrs=" + Attributes(member.CustomAttributes);
        if (member is Type type && type.IsGenericTypeDefinition)
            details += " constraints=" + string.Join(
                ";",
                type
                    .GetGenericArguments()
                    .Select(argument => argument.Name + ":" + argument.GenericParameterAttributes + ":" + string.Join(
                        ",",
                        argument
                            .GetGenericParameterConstraints()
                            .Select(constraint => constraint.ToString())
                            .Order(StringComparer.Ordinal))));
        if (member is MethodBase method)
        {
            details += " flags=" + MethodFlags(method);
            details += " params=" + string.Join(
                ";",
                method
                    .GetParameters()
                    .Select(parameter => parameter.Name + ":" + Nullable(nullability.Create(parameter)) +
                        (parameter.IsOptional
                            ? " optional=" + System.Text.Json.JsonSerializer.Serialize(parameter.DefaultValue)
                            : "") + " " + Attributes(parameter.CustomAttributes)));
            if (member is MethodInfo result)
                details += " return=" + Nullable(nullability.Create(result.ReturnParameter));
            if (method.IsGenericMethodDefinition)
                details += " constraints=" + string.Join(
                    ";",
                    method
                        .GetGenericArguments()
                        .Select(argument => argument.Name + ":" + argument.GenericParameterAttributes + ":" +
                            string.Join(
                                ",",
                                argument
                                    .GetGenericParameterConstraints()
                                    .Select(type => type.ToString())
                                    .Order(StringComparer.Ordinal))));
        }
        else if (member is PropertyInfo property)
        {
            details += " " + Nullable(nullability.Create(property)) + " get=" + (property.GetMethod?.IsPublic == true) +
                " set=" + (property.SetMethod?.IsPublic == true) + " init=" + (property
                    .SetMethod?.ReturnParameter
                    .GetRequiredCustomModifiers()
                    .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true);
            if (property.GetMethod is { IsPublic: true } getter)
                details += " getFlags=" + MethodFlags(getter);
            if (property.SetMethod is { IsPublic: true } setter)
                details += " setFlags=" + MethodFlags(setter);
        }
        else if (member is EventInfo @event)
        {
            if (@event.AddMethod is { IsPublic: true } add)
                details += " addFlags=" + MethodFlags(add);
            if (@event.RemoveMethod is { IsPublic: true } remove)
                details += " removeFlags=" + MethodFlags(remove);
        }
        else if (member is FieldInfo field)
            details += " " + Nullable(nullability.Create(field)) + " readonly=" + field.IsInitOnly + (field.IsLiteral
                ? " constant=" + System.Text.Json.JsonSerializer.Serialize(field.GetRawConstantValue())
                : "");

        return details;
    }
}
