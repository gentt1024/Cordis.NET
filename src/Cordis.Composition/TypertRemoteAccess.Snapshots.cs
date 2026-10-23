using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Cordis.Composition;

public sealed partial class TypertRemoteAccess
{
    internal const string FollowEndpoint = "remoteAccess/follow";
    private readonly HashSet<Follower> followers = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool ownsLifetime;

    /// <summary>Get the opening baseline for a page's Host-selected identity.</summary>
    public TypertAccessFrame.Baseline SnapshotFor(TypertPrincipal principal)
    {
        EnsureAccess();
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Kind == "owner"
            ? new TypertAccessFrame.Baseline.Owner()
            : new TypertAccessFrame.Baseline.Granted(GrantsFor(principal));
    }

    /// <summary>Follow the calling Peer's baseline and grant changes until cancellation or service withdrawal.</summary>
    public async IAsyncEnumerable<TypertAccessFrame> FollowAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureAccess();
        cancellationToken.ThrowIfCancellationRequested();
        using var signal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        signal.Token.ThrowIfCancellationRequested();
        var principal = TypertPrincipal.PrincipalOf(TypertInvocation.Current);
        if (principal is null)
        {
            yield return new TypertAccessFrame.Baseline.Owner();
            try
            {
                await Task.Delay(Timeout.Infinite, signal.Token);
            }
            catch (OperationCanceledException) when (signal.IsCancellationRequested)
            {
            }

            yield break;
        }

        var follower = new Follower(principal);
        followers.Add(follower);
        try
        {
            var sent = GrantsFor(principal);
            yield return new TypertAccessFrame.Baseline.Granted(sent);
            while (await follower.ChangedAsync(signal.Token))
            {
                var next = GrantsFor(principal);
                var previous = sent;
                sent = next;
                foreach (var grant in next)
                {
                    var before = previous.FirstOrDefault(other => other.Resource == grant.Resource);
                    if (before is null || !before.Actions.SequenceEqual(grant.Actions, StringComparer.Ordinal))
                        yield return new TypertAccessFrame.Upsert(grant);
                }

                foreach (var grant in previous)
                    if (!next.Any(other => other.Resource == grant.Resource))
                        yield return new TypertAccessFrame.Remove(grant.Resource);
            }
        }
        finally
        {
            followers.Remove(follower);
        }
    }

    /// <summary>The explicit native binding for the standard follow endpoint.</summary>
    public TypertRemoteBinding TypertRemote
    {
        get;
    } = new(
        "remoteAccess",
        "remoteAccess",
        new Dictionary<string, TypertUnaryInvoker>(),
        new Dictionary<string, TypertStreamInvoker>
        {
            ["follow"] = Follow
        });

    /// <summary>Get the standard Host contribution for packaging or explicit registration.</summary>
    public static TypertContribution Contribution(string package = "cordis/remote-access") => new(
        package,
        [
            new("remoteAccess#follow", "remoteAccess", "remoteAccess", "follow", [], new FrameCodec())
            {
                IsStream = true,
                Cancellation = true
            }
        ]);

    /// <summary>Register the standard follow descriptor under the supplied Fiber; provide this service as remoteAccess separately.</summary>
    public EffectHandle RegisterRemote(Context owner, TypertRegistry registry)
    {
        EnsureAccess();
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register(owner, Contribution());
    }

    private static async IAsyncEnumerable<object?> Follow(
        object service,
        IReadOnlyList<object?> arguments,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var frame in ((TypertRemoteAccess)service).FollowAsync(token))
            yield return frame;
    }

    private IReadOnlyList<TypertAccessGrant> GrantsFor(TypertPrincipal principal)
    {
        if (!policies.TryGetValue(principal.Kind, out var entry))
            return [];
        var bindings = BindingsOf(entry, principal, "a snapshot");
        if (bindings is null)
            return [];
        try
        {
            var grants = new List<TypertAccessGrant>();
            var seen = new HashSet<TypertAccessResource>();
            // Validate the entire native policy result before publishing any grants.
            foreach (var binding in bindings)
                if (binding?.Role?.Actions is null || binding.Resource is null ||
                    binding.Role.Resource != binding.Resource.Kind || binding.Role.Name is null ||
                    binding.Role.Actions.Any(action => action is null))
                    throw new InvalidOperationException("An access policy returned a malformed binding.");
            foreach (var binding in bindings)
            {
                var resource = binding.Resource;
                if (!seen.Add(resource))
                    continue;
                var actions = bindings
                    .Where(other => other.Resource == resource)
                    .SelectMany(other => other.Role.Actions)
                    .Where(action => !action.StartsWith("event:", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Where(action => Evaluate(entry, bindings, action, resource, null) is TypertAccessDecision.Allow)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (actions.Length > 0)
                    grants.Add(new(resource, Array.AsReadOnly(actions)));
            }

            return grants.AsReadOnly();
        }
        catch (Exception error)
        {
            Report(entry, "compute a snapshot", error);
            return [];
        }
    }

    private static bool SameIdentity(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var l = left.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var r = right.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            return l.Length == r.Length && l
                .Zip(r)
                .All(pair => pair.First.Name == pair.Second.Name &&
                    SameIdentity(pair.First.Value, pair.Second.Value));
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            var l = left.EnumerateArray().ToArray();
            var r = right.EnumerateArray().ToArray();
            return l.Length == r.Length && l.Zip(r).All(pair => SameIdentity(pair.First, pair.Second));
        }

        return JsonElement.DeepEquals(left, right);
    }

    private sealed class Follower(TypertPrincipal principal)
    {
        private readonly Channel<bool> changed = Channel.CreateBounded<bool>(1);

        internal TypertPrincipal Principal
        {
            get;
        } = principal;

        internal void Touch() => changed.Writer.TryWrite(true);

        internal async Task<bool> ChangedAsync(CancellationToken token)
        {
            try
            {
                await changed.Reader.ReadAsync(token);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private sealed class FrameCodec : TypertCodec
    {
        private static readonly JsonElement FrameSchema = CreateSchema();

        public override string TypeSymbol =>
            "{ type: 'baseline'; owner: true } | { type: 'baseline'; owner: false; grants: readonly { resource: { kind: string; id?: string }; actions: readonly string[] }[] } | { type: 'upsert'; grant: { resource: { kind: string; id?: string }; actions: readonly string[] } } | { type: 'remove'; resource: { kind: string; id?: string } }";

        public override JsonElement Schema => FrameSchema;
        public override object Decode(JsonElement value) => TypertAccessFrame.FromJson(value);

        public override JsonElement Encode(object? value) => value is TypertAccessFrame frame
            ? frame.ToJson()
            : throw new JsonException("An access frame is required.");

        private static JsonElement CreateSchema()
        {
            using var document = JsonDocument.Parse(
                """
                {"oneOf":[
                  {"type":"object","properties":{"type":{"const":"baseline"},"owner":{"const":true}},"required":["type","owner"],"additionalProperties":false},
                  {"type":"object","properties":{"type":{"const":"baseline"},"owner":{"const":false},"grants":{"type":"array","items":{"$ref":"#/$defs/grant"}}},"required":["type","owner","grants"],"additionalProperties":false},
                  {"type":"object","properties":{"type":{"const":"upsert"},"grant":{"$ref":"#/$defs/grant"}},"required":["type","grant"],"additionalProperties":false},
                  {"type":"object","properties":{"type":{"const":"remove"},"resource":{"$ref":"#/$defs/resource"}},"required":["type","resource"],"additionalProperties":false}
                ],"$defs":{
                  "resource":{"oneOf":[{"type":"object","properties":{"kind":{"const":"global"}},"required":["kind"],"additionalProperties":false},{"type":"object","properties":{"kind":{"type":"string","not":{"enum":["","global"]}},"id":{"type":"string","not":{"const":""}}},"required":["kind","id"],"additionalProperties":false}]},
                  "grant":{"type":"object","properties":{"resource":{"$ref":"#/$defs/resource"},"actions":{"type":"array","items":{"type":"string"}}},"required":["resource","actions"],"additionalProperties":false}
                }}
                """);
            return document.RootElement.Clone();
        }
    }
}
