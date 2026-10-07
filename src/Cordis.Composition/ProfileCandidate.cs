namespace Cordis.Composition;

/// <summary>A library-owned proposal for one coordinated profile commit. Approval cannot mutate its saved inputs.</summary>
public sealed class ProfileCandidate
{
    private readonly ProfileRefresh composition;

    internal ProfileCandidate(
        string revision,
        string manifestJson,
        string baseSource,
        string baseText,
        ProfileRefresh composition,
        PreparedPackage? package)
    {
        Revision = revision;
        ManifestJson = manifestJson;
        BaseSource = baseSource;
        BaseText = baseText;
        Package = package;
        this.composition = Copy(composition);
        ConfigurationJson = ConfigurationFile.Write(
            EntryPatches.Apply(
                ConfigurationFile.ParseEntries(baseText, Path.GetExtension(baseSource) == ".json"),
                ProfileComposition.Flatten(composition.Layers)),
            true);
    }

    /// <summary>Revision of the complete captured inputs, before this operation's own planned writes.</summary>
    public string Revision
    {
        get;
    }

    /// <summary>The exact manifest text this operation will save.</summary>
    public string ManifestJson
    {
        get;
    }

    /// <summary>Complete raw root configuration after the existing patch composer; expressions are not evaluated.</summary>
    public string ConfigurationJson
    {
        get;
    }

    /// <summary>The base configuration's source path.</summary>
    public string BaseSource
    {
        get;
    }

    internal string BaseText
    {
        get;
    }

    /// <summary>The prepared package and planned destination for installation admission; null for other operations.</summary>
    public PreparedPackage? Package
    {
        get;
    }

    /// <summary>A detached copy of the proposed layers and selection, including skipped-bundle diagnostics.</summary>
    public ProfileRefresh Composition => Copy(composition);

    private static ProfileRefresh Copy(ProfileRefresh value) => value with
    {
        Layers = value
            .Layers.Select(layer => layer with
            {
                Patches = ProfileComposition.Flatten([layer])
            })
            .ToArray(),
        CurrentBundles = value.CurrentBundles.ToArray(),
        SelectedBundles = value.SelectedBundles.ToArray(),
        SkippedBundles = value.SkippedBundles.ToArray()
    };
}

/// <summary>A detached manifest and the input revision required by coordinated metadata saving.</summary>
public sealed record ProfileDocument(string ManifestJson, string Revision);
