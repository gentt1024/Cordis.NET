# Optional JavaScript Native AOT probe

This separate executable exercises actual JavaScript evaluation, a CLR function service,
and writes through the context bridge. It is not part of the static composition AOT gate.

```powershell
dotnet publish tests/Cordis.JavaScript.AotProbe/Cordis.JavaScript.AotProbe.csproj -c Release -r win-x64 -o artifacts/javascript-aot-probe
```

On 2026-09-20, the Windows x64 publish reached native code generation but failed with
.NET SDK 10.0.102 / ILCompiler 10.0.2 and Jint 4.7.0. The repository's warnings-as-errors
policy was retained; no IL warnings were suppressed. Diagnostics included:

- IL2026 and IL2111 at `JintExpressionEvaluator.Evaluate` delegate bridge registrations
  involving reflective `System.Delegate.CreateDelegate` access.
- IL2104 and IL3053 for Jint trimming/AOT analysis.
- IL2026 in Jint `NamespaceReference` and `DefaultTypeConverter.BuildDelegate`.

The saved output is `verification/javascript-aot-publish.log`. Publish exited 1;
no native executable was produced or run. This is evidence of the current optional
JavaScript adapter's AOT limitation, not a successful AOT validation.

The same probe passes under JIT with `dotnet run --project
tests/Cordis.JavaScript.AotProbe/Cordis.JavaScript.AotProbe.csproj -c Release
-p:PublishAot=false`.
