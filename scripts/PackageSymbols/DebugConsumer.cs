using Cordis;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

try
{
    // Reflection prevents the JIT from inlining the library frame into this consumer.
    typeof(ConfigResult<object>).GetMethod(nameof(ConfigResult<object>.Failure))!.Invoke(
        null,
        new object?[] { Array.Empty<string>() });
    throw new InvalidOperationException("Expected the library to reject empty issues.");
}
catch (TargetInvocationException wrapper) when (wrapper.InnerException is ArgumentException error)
{
    var frame = new StackTrace(error, true)
        .GetFrames()
        .Single(frame => frame.GetMethod()?.DeclaringType?.Assembly == typeof(ConfigResult<object>).Assembly &&
            frame.GetMethod()?.Name == nameof(ConfigResult<object>.Failure));
    if (frame.GetFileName() is not { } file || frame.GetFileLineNumber() <= 0)
        throw new InvalidOperationException("The consumed library has no matching source symbols.");
    Console.WriteLine(
        JsonSerializer.Serialize(
            new
            {
                file,
                line = frame.GetFileLineNumber(),
                method = frame.GetMethod()!.Name
            }));
}
