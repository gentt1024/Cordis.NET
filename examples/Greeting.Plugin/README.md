# Cordis.Example.Greeting

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/examples/Greeting.Plugin/README.zh.md)

This source example provides a string-valued `greeting` service and an embedded configuration patch. Use it to see how an application registers a static module and reads deployed resources.

## Run the example

Use the SDK pinned in the repository's `global.json`. From the repository root, run:

```console
dotnet run --project examples/Composition/Composition.csproj
```

The application references Greeting.Plugin, registers `GreetingModule.Plugin`, reads its embedded patch and updates its configuration. `ReadPatch()` opens the deployed assembly resource; it does not search a source directory at runtime.

## Use it as a reference

Copy the module and resource pattern into your own application, using your own service name and configuration rules. This educational package is validated during repository checks but excluded from public release publication. It is not one of the eight published `Cordis.NET.*` products.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
