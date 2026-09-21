using Cordis;
using Cordis.Composition;
using Cordis.Example.Greeting;

await using var context = new Context();
await context.RunAsync(async root =>
{
    var modules = new StaticModuleResolver().Register("greeting", GreetingModule.Plugin);
    var loader = new Loader(root, modules);
    var entries = EntryPatches.Apply([], ConfigurationFile.ParseEntries(GreetingModule.ReadPatch()));
    await loader.Root.UpdateAsync(entries);
    await loader.WaitAsync();
    Console.WriteLine(root.Get<string>("greeting"));
    await loader.UpdateAsync("greeting", new EntryOptions { Config = "Configuration stays live with JIT or AOT" });
    await loader.WaitAsync();
    Console.WriteLine(root.Get<string>("greeting"));
    if (root.Get<string>("greeting") != "Configuration stays live with JIT or AOT") throw new Exception("Update failed");
});
