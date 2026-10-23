namespace Cordis.Composition;

/// <summary>An immutable resource identity compared by its exact kind and id.</summary>
public sealed record TypertAccessResource
{
    private TypertAccessResource(string kind, string? id)
    {
        Kind = kind;
        Id = id;
    }

    /// <summary>The resource used by actions that name no other resource.</summary>
    public static TypertAccessResource Global
    {
        get;
    } = new("global", null);

    /// <summary>The case-sensitive resource kind.</summary>
    public string Kind
    {
        get;
    }

    /// <summary>The resource identity, absent only for the global resource.</summary>
    public string? Id
    {
        get;
    }

    /// <summary>Create one non-global resource with a nonempty kind and identity.</summary>
    public static TypertAccessResource Create(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (kind == "global")
            throw new ArgumentException("The global resource has no id. Use Global instead.", nameof(kind));
        return new(kind, id);
    }
}

/// <summary>The closed allow or deny answer to an access question.</summary>
public abstract class TypertAccessDecision
{
    private TypertAccessDecision()
    {
    }

    /// <summary>The shared allowing decision.</summary>
    public static Allow Allowed
    {
        get;
    } = new();

    /// <summary>Allow the requested action.</summary>
    public sealed class Allow : TypertAccessDecision
    {
    }

    /// <summary>Deny the requested action with a business-owned reason.</summary>
    public sealed class Deny(string Reason) : TypertAccessDecision
    {
        /// <summary>The business-owned refusal reason.</summary>
        public string Reason
        {
            get;
        } = Reason;
    }
}

/// <summary>A named action set over one resource kind.</summary>
public sealed record TypertAccessRole(string Name, string Resource, IReadOnlyList<string> Actions);

/// <summary>One role on one resource, optionally refined by a synchronous condition.</summary>
public sealed record TypertAccessBinding(
    TypertAccessRole Role,
    TypertAccessResource Resource,
    Func<TypertAccessCondition, TypertAccessDecision>? Condition = null);

/// <summary>The action, resource and optional validated call arguments seen by a condition.</summary>
public sealed record TypertAccessCondition(
    string Action,
    TypertAccessResource Resource,
    IReadOnlyDictionary<string, object?>? Arguments = null);

/// <summary>One principal's action on every listed resource.</summary>
public sealed record TypertAccessRequest(
    TypertPrincipal Principal,
    string Action,
    IReadOnlyList<TypertAccessResource> Resources,
    IReadOnlyDictionary<string, object?>? Arguments = null);

/// <summary>Business-owned synchronous bindings and optional refusal explanations for one principal kind.</summary>
public sealed record TypertAccessPolicy(
    Func<TypertPrincipal, IReadOnlyList<TypertAccessBinding>> Bindings,
    Func<TypertAccessRequest, string?>? Explain = null);

/// <summary>Fiber-owned access policies, resource decisions and Remote list filtering.</summary>
/// <remarks>Use the owning Context execution domain for reads and mutations.</remarks>
public sealed class TypertRemoteAccess
{
    private static readonly TypertAccessDecision.Deny NoAllow = new("no-allow");
    private static readonly TypertAccessDecision.Deny PolicyFailed = new("policy-failed");
    private readonly Context context;
    private readonly Dictionary<string, PolicyEntry> policies = new(StringComparer.Ordinal);
    private readonly HashSet<PolicyEntry> reported = [];

    /// <summary>Create the access service for one Host Context.</summary>
    public TypertRemoteAccess(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    /// <summary>Register one non-owner principal kind's policy under the caller's Fiber.</summary>
    public EffectHandle RegisterPolicy(Context owner, string kind, TypertAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(owner);
        EnsureAccess();
        if (!ReferenceEquals(owner.Root, context.Root))
            throw new ArgumentException("Access policy owners must share the service's Cordis root.", nameof(owner));
        ArgumentException.ThrowIfNullOrEmpty(kind);
        if (kind == "owner")
            throw new ArgumentException("The owner principal cannot have an access policy.", nameof(kind));
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.Bindings);
        if (policies.ContainsKey(kind))
            throw new InvalidOperationException($"Remote access principal kind '{kind}' already has a policy.");
        var entry = new PolicyEntry(kind, policy);
        return owner.Effect(
            () =>
            {
                policies.Add(kind, entry);
                Invalidate();
                return (Action)(() =>
                {
                    if (!ReferenceEquals(policies.GetValueOrDefault(kind), entry))
                        return;
                    policies.Remove(kind);
                    Invalidate();
                });
            },
            $"remoteAccess.registerPolicy({kind})");
    }

    /// <summary>Decide whether a principal may perform an action on every resource.</summary>
    public TypertAccessDecision Decide(TypertAccessRequest request)
    {
        EnsureAccess();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Principal);
        ArgumentNullException.ThrowIfNull(request.Action);
        ArgumentNullException.ThrowIfNull(request.Resources);
        if (request.Principal.Kind == "owner")
            return TypertAccessDecision.Allowed;
        if (!policies.TryGetValue(request.Principal.Kind, out var entry))
            return NoAllow;
        var bindings = BindingsOf(entry, request.Principal, request.Action);
        if (bindings is null)
            return PolicyFailed;
        foreach (var resource in request.Resources)
        {
            var decision = Evaluate(entry, bindings, request.Action, resource, request.Arguments);
            if (decision is TypertAccessDecision.Allow)
                continue;
            return decision ?? Explain(entry, request);
        }

        return TypertAccessDecision.Allowed;
    }

    /// <summary>Check one list item's resource using the current invocation and fresh bindings, without call arguments.</summary>
    public bool Allows(TypertAccessResource resource)
    {
        EnsureAccess();
        var invocation = TypertInvocation.Current;
        var principal = TypertPrincipal.PrincipalOf(invocation);
        if (principal is null)
            return true;
        if (!policies.TryGetValue(principal.Kind, out var entry))
            return false;
        var bindings = BindingsOf(entry, principal, invocation!.Endpoint);
        return bindings is not null &&
            Evaluate(entry, bindings, invocation.Endpoint, resource, null) is TypertAccessDecision.Allow;
    }

    /// <summary>Allow the next failure of each policy to be diagnosed again.</summary>
    public void Invalidate()
    {
        EnsureAccess();
        reported.Clear();
    }

    private IReadOnlyList<TypertAccessBinding>? BindingsOf(PolicyEntry entry, TypertPrincipal principal, string action)
    {
        try
        {
            return entry.Policy.Bindings(principal) ??
                throw new InvalidOperationException("An access policy returned null bindings.");
        }
        catch (Exception error)
        {
            Report(entry, $"list bindings for {action}", error);
            return null;
        }
    }

    private TypertAccessDecision? Evaluate(
        PolicyEntry entry,
        IReadOnlyList<TypertAccessBinding> bindings,
        string action,
        TypertAccessResource resource,
        IReadOnlyDictionary<string, object?>? arguments)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(resource);
            TypertAccessDecision.Deny? refusal = null;
            foreach (var binding in bindings)
            {
                if (binding?.Role?.Actions is null || binding.Resource is null ||
                    binding.Role.Resource != binding.Resource.Kind || binding.Role.Name is null ||
                    binding.Role.Actions.Any(candidate => candidate is null))
                    throw new InvalidOperationException("An access policy returned a malformed binding.");
                if (binding.Resource != resource || !binding.Role.Actions.Contains(action, StringComparer.Ordinal))
                    continue;
                if (binding.Condition is null)
                    return TypertAccessDecision.Allowed;
                switch (binding.Condition(new(action, resource, arguments)))
                {
                    case TypertAccessDecision.Allow:
                        return TypertAccessDecision.Allowed;
                    case TypertAccessDecision.Deny { Reason: not null } deny:
                        refusal ??= deny;
                        break;
                    default:
                        throw new InvalidOperationException("An access condition returned a malformed decision.");
                }
            }

            return refusal;
        }
        catch (Exception error)
        {
            Report(entry, $"evaluate a condition for {action}", error);
            return PolicyFailed;
        }
    }

    private TypertAccessDecision Explain(PolicyEntry entry, TypertAccessRequest request)
    {
        try
        {
            var reason = entry.Policy.Explain?.Invoke(request);
            return reason is null ? NoAllow : new TypertAccessDecision.Deny(reason);
        }
        catch (Exception error)
        {
            Report(entry, $"explain a refusal of {request.Action}", error);
            return PolicyFailed;
        }
    }

    private void Report(PolicyEntry entry, string action, Exception error)
    {
        if (!reported.Add(entry))
            return;
        try
        {
            context.Logger.Warn(
                $"Remote access: the '{entry.Kind}' policy failed to {action}; further failures are not logged until access state changes.");
            context.Logger.Warn(error);
        }
        catch (Exception)
        {
            /* Diagnostics cannot replace a policy refusal. */
        }
    }

    private void EnsureAccess() => _ = context.Get<object>("remoteAccess", strict: false);

    private sealed class PolicyEntry(string kind, TypertAccessPolicy policy)
    {
        internal string Kind
        {
            get;
        } = kind;

        internal TypertAccessPolicy Policy
        {
            get;
        } = policy;
    }
}
