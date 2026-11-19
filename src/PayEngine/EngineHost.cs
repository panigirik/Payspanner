using Microsoft.Extensions.DependencyInjection;
using PayEngine.Contracts;
using PayEngine.Loading;
using PayEngine.Model;
using PayEngine.Runtime;
namespace PayEngine;
public static class EngineHost
{
    private static readonly Dictionary<string, string> EmptySettings = new(StringComparer.Ordinal);
    public static EngineSession Open(
        string definitionsDirectory,
        IReadOnlyDictionary<string, string>? settings,
        IOutbound outbound,
        IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionsDirectory); ArgumentNullException.ThrowIfNull(outbound); ArgumentNullException.ThrowIfNull(clock); var bound = settings ?? EmptySettings;
        var loaded = DefinitionCatalog.Load(definitionsDirectory, bound); return SessionFactory.Create(loaded, bound, outbound, clock);
    }
}
internal static class SessionFactory
{
    internal static EngineSession Create(
        LoadBatch batch,
        IReadOnlyDictionary<string, string> settings,
        IOutbound outbound,
        IClock clock)
    {
        var services = new ServiceCollection(); services.AddSingleton(outbound); services.AddSingleton(clock); services.AddSingleton(settings); var sections = new List<string>(batch.Plans.Count);
        foreach (var plan in batch.Plans)
        {
            sections.Add(plan.Section); services.AddSingleton(plan);
            var deps = new EngineDeps
            {
                Plan = plan,
                Outbound = outbound,
                Clock = clock,
                Settings = settings,
            };
            if (plan.PayinExecute) { services.AddSingleton<IPayinExecutor>(new PayinWorker(deps)); }
            if (plan.PayoutExecute) { services.AddSingleton<IPayoutExecutor>(new PayoutWorker(deps)); }
            if (plan.PayinCallback) { services.AddSingleton<IPayinCallbackHandler>(new PayinCallbackWorker(deps)); }
            if (plan.PayoutCallback) { services.AddSingleton<IPayoutCallbackHandler>(new PayoutCallbackWorker(deps)); }
        }
        using var container = services.BuildServiceProvider();
        var payins = container.GetServices<IPayinExecutor>().ToArray(); var payouts = container.GetServices<IPayoutExecutor>().ToArray();
        var payinCallbacks = container.GetServices<IPayinCallbackHandler>().ToArray(); var payoutCallbacks = container.GetServices<IPayoutCallbackHandler>().ToArray();
        var boundOutbound = container.GetRequiredService<IOutbound>(); var boundClock = container.GetRequiredService<IClock>(); var boundPlans = container.GetServices<ProviderPlan>().ToArray();
        if (!ReferenceEquals(boundOutbound, outbound) || !ReferenceEquals(boundClock, clock) || boundPlans.Length != sections.Count) { throw new InvalidOperationException("The plan was not registered."); }
        return new EngineSession(batch.Problems.ToArray(), sections.ToArray(), payins, payouts, payinCallbacks, payoutCallbacks);
    }
}
public sealed class EngineSession
{
    private readonly IReadOnlyList<string> problems; private readonly IReadOnlyCollection<string> sections;
    private readonly Dictionary<string, IPayinExecutor> payins; private readonly Dictionary<string, IPayoutExecutor> payouts; private readonly Dictionary<string, IPayinCallbackHandler> payinCallbacks;
    private readonly Dictionary<string, IPayoutCallbackHandler> payoutCallbacks;
    internal EngineSession(
        IReadOnlyList<string> problems,
        IReadOnlyCollection<string> sections,
        IEnumerable<IPayinExecutor> payinWorkers,
        IEnumerable<IPayoutExecutor> payoutWorkers,
        IEnumerable<IPayinCallbackHandler> payinHandlers,
        IEnumerable<IPayoutCallbackHandler> payoutHandlers)
    {
        this.problems = problems; this.sections = sections; this.payins = Index(payinWorkers); this.payouts = Index(payoutWorkers);
        this.payinCallbacks = Index(payinHandlers); this.payoutCallbacks = Index(payoutHandlers);
    }
    public IReadOnlyList<string> Problems => this.problems; public IReadOnlyCollection<string> Sections => this.sections;
    public bool TryPayin(string section, out IPayinExecutor executor)
    {
        return Take(this.payins, section, out executor);
    }
    public bool TryPayout(string section, out IPayoutExecutor executor)
    {
        return Take(this.payouts, section, out executor);
    }
    public bool TryPayinCallback(string section, out IPayinCallbackHandler handler)
    {
        return Take(this.payinCallbacks, section, out handler);
    }
    public bool TryPayoutCallback(string section, out IPayoutCallbackHandler handler)
    {
        return Take(this.payoutCallbacks, section, out handler);
    }
    private static Dictionary<string, T> Index<T>(IEnumerable<T> workers)
        where T : class
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var worker in workers)
        {
            if (worker is ISectioned sectioned) { map[sectioned.Section] = worker; }
        }
        return map;
    }
    private static bool Take<T>(Dictionary<string, T> map, string section, out T executor)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(section);
        if (map.TryGetValue(section, out var found)) { executor = found; return true; }
        executor = null!; return false;
    }
}
