using System.Collections.Concurrent;

namespace Cordis.Fixtures;

public interface IRetirementService : IAsyncDisposable
{
    string VerifyReady();
    string Execute();
    Task<string> ExecuteAsync();
    Action CaptureCallback();
    void CloseAdmission();
}

public sealed record RetirementContribution(string Version, string Id, IRetirementService Service);

public sealed class RetirementProbe(string resourceDirectory)
{
    public string ResourceDirectory
    {
        get;
    } = resourceDirectory;

    public bool AllowRetirement
    {
        get;
        set;
    } = true;

    public bool FailStop
    {
        get;
        set;
    }

    public bool FailRecovery
    {
        get;
        set;
    }

    public bool FailCandidateStop
    {
        get;
        set;
    }

    // The real Host fixture supplies its DI-owned authority at the business boundary.
    public Func<string, Action, bool> PerformBusiness
    {
        get;
        set;
    } = (_, action) =>
    {
        action();
        return true;
    };

    public Func<string, Action, bool> CompleteBusiness
    {
        get;
        set;
    } = (_, action) =>
    {
        action();
        return true;
    };

    public ConcurrentQueue<RetirementContribution> Contributions
    {
        get;
    } = new();

    public bool ExclusiveResources
    {
        get;
        set;
    } = true;

    public ConcurrentQueue<string> History
    {
        get;
    } = new();

    public TaskCompletionSource Entered
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Stopping
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action? Tick;
    public void Pulse() => Tick?.Invoke();
}
