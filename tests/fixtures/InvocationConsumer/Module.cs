using Cordis;
using Cordis.Clr;
using Cordis.Composition;
using System.Text.Json;

public sealed class Module : IClrPluginModule
{
    public IPlugin CreatePlugin() =>
        new Plugin<object?>
        {
            Inject = ["cmdlineArgs", "appReady", "appExit"],
            Apply = (context, _) =>
            {
                var args = context.Get<CommandLineArguments>("cmdlineArgs")!.Get();
                var ready = context.Get<IApplicationReady>("appReady")!;
                var exit = context.Get<ApplicationExit>("appExit")!;
                Console.WriteLine("APP_ARGS:" + JsonSerializer.Serialize(args));
                context.Effect(() => new Cleanup(args.Contains("--hang-cleanup"), args.Contains("--fail-cleanup")));
                try
                {
                    ((IList<string>)args).Add("tampered");
                    throw new InvalidOperationException("Arguments must be immutable");
                }
                catch (NotSupportedException)
                {
                }

                using (ready.OnReady(() => throw new InvalidOperationException("Withdrawn listener ran")))
                {
                }

                if (args.FirstOrDefault() is "--help" or "--invalid")
                {
                    Console.WriteLine(args[0] == "--help" ? "APP_HELP" : "APP_ERROR");
                    exit(args[0] == "--help" ? 0 : 7);
                    return;
                }

                context.Effect(() => ready.OnReady(() =>
                {
                    Console.WriteLine("APP_READY");
                    using var late = ready.OnReady(() => Console.WriteLine("APP_LATE_READY"));
                    exit(0);
                    if (args.Contains("--fail-ready"))
                        throw new InvalidOperationException("Application readiness failed");
                }));
            },
        };

    private sealed class Cleanup(bool hang, bool fail) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (hang)
            {
                Console.WriteLine("APP_CLEANUP_PENDING");
                await Task.Delay(Timeout.Infinite);
            }

            await Task.Delay(20);
            Console.WriteLine("APP_DISPOSED");
            if (fail)
                throw new InvalidOperationException("Application cleanup failed");
        }
    }
}
