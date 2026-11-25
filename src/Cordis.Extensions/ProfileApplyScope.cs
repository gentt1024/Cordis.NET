using Cordis.Composition;

namespace Cordis.Extensions;

internal sealed class ProfileApplyScope(IProfileApplyLease lease, Action<Exception> report) : IAsyncDisposable
{
    internal static async Task<ProfileApplyScope?> PrepareAsync(
        Func<ProfileApplyRequest, Task<IProfileApplyLease>>? prepare,
        ProfileApplyRequest request,
        Action<Exception> report)
    {
        if (prepare is null)
            return null;
        var lease = await prepare(request) ??
            throw new InvalidOperationException("Profile apply preparation returned no lease.");
        return new(lease, report);
    }

    internal async Task CommittedAsync()
    {
        try
        {
            await lease.CommittedAsync();
        }
        catch (Exception error)
        {
            report(
                new InvalidOperationException(
                    "The running change committed, but application publication failed.",
                    error));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await lease.DisposeAsync();
        }
        catch (Exception error)
        {
            report(new InvalidOperationException("The application apply lease failed to release.", error));
        }
    }
}
