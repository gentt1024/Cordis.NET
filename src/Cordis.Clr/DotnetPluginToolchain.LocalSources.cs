using Cordis.Composition;

namespace Cordis.Clr;

public sealed partial class DotnetPluginToolchain
{
    private LocalSourcePolicy? localSourcePolicy;

    /// <inheritdoc />
    public string ProfileDirectory => profileDirectory;

    private LocalSourcePolicy RequireLocalSourcePolicy() => localSourcePolicy ??
        throw new UnauthorizedAccessException("This Host has no local CLR source policy.");

    /// <inheritdoc />
    public async Task<LocalSourceFacts> InspectLocalSourceAsync(
        string subject,
        LocalSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(request);
        return (await InspectSourceAsync(subject, request, null, requireBundle: true, cancellationToken))!;
    }

    private async Task<LocalSourceFacts?> InspectSourceAsync(
        string? subject,
        LocalSourceRequest request,
        ManagedLocalSource? saved,
        bool requireBundle,
        CancellationToken token)
    {
        var root = SourceRoot(request.Source);
        var access = new LocalSourceAccess(
            subject,
            profileDirectory,
            LocalSourceStage.Read,
            root,
            request.Configuration);
        await AuthorizeAsync(access, token);
        var project = SelectSourceProject(root, out var bundle);
        if (saved is not null && !SameSourcePath(saved.ProjectFile, project))
            throw new InvalidDataException(
                "The local plugin declaration selects a different project than the saved registration.");
        if (!bundle && requireBundle)
            throw new InvalidDataException("The selected source has no CLR plugin declaration.");
        await AuthorizeAsync(
            access with
            {
                Stage = LocalSourceStage.Evaluate,
                ProjectFile = project
            },
            token);
        if (!SameSourcePath(project, SelectSourceProject(root, out var stillBundle)) || stillBundle != bundle)
            throw new InvalidDataException("The selected local project changed during authorization.");
        var evaluated = await ClrLocalProject.QuerySelectedAsync(
            root,
            project,
            bundle,
            request.Configuration,
            dotnet,
            token);
        if (!string.Equals(evaluated.Configuration, request.Configuration, StringComparison.Ordinal))
            throw new InvalidDataException("The evaluated CLR configuration differs from the requested configuration.");
        if (!bundle)
            return null;
        ValidateIdentity(evaluated.PackageId, "0.0.0");
        if (string.IsNullOrWhiteSpace(evaluated.Version))
            throw new InvalidDataException("The evaluated CLR project has no package version.");
        CheckPhysicalPath(project);
        var output = evaluated.ReadOutput(allowMissing: true, verifyVersion: true);
        return new(
            root,
            project,
            evaluated.Configuration,
            evaluated.PackageId,
            evaluated.Version,
            evaluated.TargetFramework,
            evaluated.RuntimeIdentifier,
            output?.Directory,
            output?.Identity);
    }

    /// <inheritdoc />
    public Task AuthorizeLocalSourceAsync(
        string subject,
        LocalSourceStage stage,
        LocalSourceFacts facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(facts);
        return AuthorizeFactsAsync(subject, stage, facts, cancellationToken);
    }

    private async Task AuthorizeFactsAsync(
        string? subject,
        LocalSourceStage stage,
        LocalSourceFacts facts,
        CancellationToken token)
    {
        if (stage is not (LocalSourceStage.Register or LocalSourceStage.Activate))
            throw new ArgumentOutOfRangeException(nameof(stage));
        var root = SourceRoot(facts.SourceRoot);
        LocalBuildFiles.Within(facts.ProjectFile, root);
        if (facts.OutputDirectory is not null)
            LocalBuildFiles.Within(facts.OutputDirectory, root);
        if (stage == LocalSourceStage.Activate)
        {
            if (facts.Configuration != configuration)
                throw new InvalidOperationException("The local source requires a different Host configuration.");
            if (facts.OutputDirectory is null || facts.OutputIdentity is null)
                throw new InvalidOperationException("Build the local CLR source before activating it.");
        }

        await AuthorizeAsync(
            new(subject, profileDirectory, stage, root, facts.Configuration, facts.ProjectFile, facts),
            token);
        VerifySourceSelection(facts);
    }

    private static void VerifySourceSelection(LocalSourceFacts facts)
    {
        var root = facts.SourceRoot;
        if (!SameSourcePath(facts.ProjectFile, SelectSourceProject(root, out var bundle)) || !bundle)
            throw new InvalidDataException("The selected local project changed during authorization.");
        var output = LocalBuildFiles.ReadOutput(
            root,
            facts.ProjectFile,
            facts.Configuration,
            facts.TargetFramework,
            facts.RuntimeIdentifier,
            allowMissing: true);
        if (facts.OutputIdentity != output?.Identity || facts.OutputDirectory != output?.Directory)
            throw new InvalidDataException("The completed local output changed during authorization.");
    }

    /// <inheritdoc />
    public Task AuthorizeLocalSourceReadAsync(
        string subject,
        RegisteredLocalSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(source);
        var root = SourceRoot(source.SourceRoot);
        LocalBuildFiles.Within(source.ProjectFile, root);
        return AuthorizeAsync(
            new(subject, profileDirectory, LocalSourceStage.Read, root, source.Configuration, source.ProjectFile),
            cancellationToken);
    }

    private async Task AuthorizeAsync(LocalSourceAccess access, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await RequireLocalSourcePolicy().Authorize(access, token))
            throw new UnauthorizedAccessException($"The Host denied local source access ({access.Stage}).");
        token.ThrowIfCancellationRequested();
    }

    private string SourceRoot(string source)
    {
        var policy = RequireLocalSourcePolicy();
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Path.IsPathRooted(source) && !Path.IsPathFullyQualified(source))
            throw new ArgumentException(
                "A local source must be absolute or relative to the Host workspace.",
                nameof(source));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source, policy.Workspace));
        try
        {
            LocalBuildFiles.Within(root, policy.Workspace);
        }
        catch (InvalidDataException)
        {
            throw new UnauthorizedAccessException("The local source is outside the Host workspace.");
        }

        return root;
    }

    private static void CheckPhysicalPath(string path) => LocalBuildFiles.CheckPath(path, Path.GetPathRoot(path)!);

    private static string SelectSourceProject(string root, out bool bundle)
    {
        CheckPhysicalPath(root);
        LocalBuildFiles.CheckPath(Path.Combine(root, "cordis.plugin.json"), root);
        return LocalBuildFiles.SelectProject(root, out bundle);
    }

    private static bool SameSourcePath(string left, string right) =>
        Path.IsPathFullyQualified(left) && string.Equals(
            Path.GetFullPath(left),
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Resolve the Host's command once. Starting a process in an author's directory must never select an executable there.
    private static string ResolveHostDotnet(string command)
    {
        if (Path.IsPathFullyQualified(command))
            return Path.GetFullPath(command);
        if (command != "dotnet")
            throw new ArgumentException(
                "Configure the local-source Host with an absolute dotnet executable.",
                nameof(command));
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (configured is not null && Path.IsPathFullyQualified(configured))
            return Path.GetFullPath(configured);
        var filename = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, filename)))
                return Path.GetFullPath(Path.Combine(directory, filename));
        throw new FileNotFoundException("Configure an absolute dotnet executable for local source evaluation.");
    }
}
