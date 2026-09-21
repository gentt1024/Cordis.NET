namespace Cordis.JavaScript;

/// <summary>Retains the JavaScript error kind at the native expression boundary and preserves the engine exception.</summary>
public sealed class JavaScriptExpressionException(string errorName, string message, Exception innerException)
    : Exception($"{errorName}: {message}", innerException)
{
    /// <summary>
    /// Gets the error name value.
    /// </summary>
    public string ErrorName { get; } = errorName;
    /// <summary>
    /// Returns a string representation of this instance.
    /// </summary>
    public override string ToString() => Message
        + (InnerException is null ? "" : Environment.NewLine + " ---> " + InnerException + Environment.NewLine + "   --- End of inner exception stack trace ---")
        + (StackTrace is null ? "" : Environment.NewLine + StackTrace);
}
