using Cordis.Composition;

if (args.Length != 2 || args[0] != "--output")
    throw new ArgumentException("Usage: Cordis.ClientAssets --output <directory>");
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
Save("cordis-remote.mjs", TypertClientRuntime.Module);
Save("cordis-remote.d.mts", TypertClientRuntime.Declaration);
Write("management", PluginManagerTypert.Contribution("Cordis.NET.PluginManager"));
Write("notifications", RemoteNotificationsTypert.Contribution("Cordis.NET.Notifications"));
Write("settings", SettingsControllerTypert.Contribution("Cordis.NET.Settings"));

void Write(string name, TypertContribution contribution)
{
    var artifact = TypertArtifacts.GenerateClient(contribution);
    Save(name + ".mjs", artifact.Module);
    Save(name + ".d.ts", artifact.Declaration);
}

void Save(string name, string content)
{
    var destination = Path.Combine(directory, name);
    if (File.Exists(destination) && File.ReadAllText(destination) == content)
        return;
    var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        File.WriteAllText(temporary, content);
        File.Move(temporary, destination, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary))
            File.Delete(temporary);
    }
}
