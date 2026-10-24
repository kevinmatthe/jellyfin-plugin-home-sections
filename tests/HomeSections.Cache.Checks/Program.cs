using System.Reflection;
using Jellyfin.Plugin.HomeScreenSections;
using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Data;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using Jellyfin.Plugin.HomeScreenSections.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

int failures = 0;
Check("client page hash reuses one generated snapshot", () =>
{
    using var f = new Fixture();
    var user = Guid.NewGuid(); var hash = Guid.NewGuid();
    var first = f.Service.MonitorLiveUpdatedSectionsForUser(user, "en", 1, 1, hash);
    var second = f.Service.MonitorLiveUpdatedSectionsForUser(user, "en", 1, 1, hash);
    Require(f.Section.Created == 1, $"expected one generation, got {f.Section.Created}");
    Require(first![0].AdditionalData == second![0].AdditionalData, "same page returned a different snapshot");
    Require(f.Cache.PageHashOwnerIds.TryGetValue(hash, out var owner) && owner == user, "client hash was not bound to its user");
});
Check("pagination reads different entries from the same snapshot", () =>
{
    using var f = new Fixture(); var user = Guid.NewGuid(); var hash = Guid.NewGuid();
    var first = f.Service.MonitorLiveUpdatedSectionsForUser(user, "en", 1, 1, hash)![0].AdditionalData!.Split(':');
    var second = f.Service.MonitorLiveUpdatedSectionsForUser(user, "en", 2, 1, hash)![0].AdditionalData!.Split(':');
    Require(first[1] == second[1], "page two regenerated the snapshot");
    Require(first[2] != second[2], "page two repeated page one");
});
Check("another user's page hash never returns their sections", () =>
{
    using var f = new Fixture(); var a = Guid.NewGuid(); var b = Guid.NewGuid(); var hash = Guid.NewGuid();
    f.Service.MonitorLiveUpdatedSectionsForUser(a, "en", 1, 1, hash);
    var second = f.Service.MonitorLiveUpdatedSectionsForUser(b, "en", 1, 1, hash)!;
    Require(second.All(s => s.AdditionalData!.StartsWith(b.ToString("N")+":")), "cross-user cached data was returned");
});
Check("cache reads enforce the snapshot's user", () =>
{
    using var f = new Fixture(); var a = Guid.NewGuid(); var hash = Guid.NewGuid();
    f.Cache.PageHashOwnerIds.TryAdd(hash, a);
    f.Cache.PageHashExpiry.TryAdd(hash, DateTime.UtcNow.AddHours(1));
    f.Service.CacheSectionsForUser(a, hash);
    Require(f.Service.GetCachedSectionsForUser(Guid.NewGuid(), "en", 1, 1, hash) == null, "direct cache read crossed user boundary");
});
Check("only one user can claim a client-generated page hash", () =>
{
    using var f = new Fixture(); var hash = Guid.NewGuid();
    var method = typeof(HomeScreenSectionService).GetMethod("DoesPageBelongToUser", BindingFlags.Instance|BindingFlags.NonPublic)!;
    int accepted = 0;
    Parallel.For(0, 12, _ => { if ((bool)method.Invoke(f.Service, [hash, Guid.NewGuid()])!) Interlocked.Increment(ref accepted); });
    Require(accepted == 1, $"expected one owner, got {accepted}");
});
Check("simultaneous first requests build one shared snapshot", () =>
{
    using var f = new Fixture(); var user = Guid.NewGuid(); var hash = Guid.NewGuid();
    using var started = new CountdownEvent(2);
    f.BeforeSettings = () => { started.Signal(); Require(started.Wait(TimeSpan.FromSeconds(5)), "concurrent requests failed to start"); };
    var requests = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(
        () => f.Service.MonitorLiveUpdatedSectionsForUser(user, "en", 1, 1, hash),
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    Task.WaitAll(requests);
    Require(f.Section.Created == 1, $"concurrent requests generated {f.Section.Created} snapshots");
    Require(requests[0].Result![0].AdditionalData == requests[1].Result![0].AdditionalData, "concurrent snapshots differ");
});
Check("requests without a hash can reuse a user's active snapshot", () =>
{
    using var f = new Fixture(); var user = Guid.NewGuid();
    f.Service.MonitorLiveUpdatedSectionsForUser(user, "en", 1, 1);
    f.Service.MonitorLiveUpdatedSectionsForUser(user, "en", 1, 1);
    Require(f.Section.Created == 1, "hashless cache reuse regressed");
});
LatestSectionChecks.Run(Check);
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    try { action(); Console.WriteLine("PASS "+name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL "+name+": "+error.GetBaseException().Message); }
}
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

sealed class Fixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hss-cache-check-"+Guid.NewGuid().ToString("N"));
    public UserSectionsDataCache Cache { get; } = new();
    public FixtureSection Section { get; } = new();
    public HomeScreenSectionService Service { get; }
    public Action? BeforeSettings { get; set; }
    public Fixture()
    {
        Directory.CreateDirectory(_directory);
        var paths = Proxy<IApplicationPaths>((method, _) => method.ReturnType == typeof(string) ? _directory : null);
        var manager = Proxy<IHomeScreenManager>((method, args) => method.Name switch
        {
            "GetSectionTypes" => new IHomeScreenSection[] { Section },
            "GetUserSettings" => Settings((Guid)args![0]!),
            _ => null
        });
        var translations = Proxy<ITranslationManager>((method, args) => method.Name == "Translate" ? args![2] : null);
        var config = Proxy<IServerConfigurationManager>((method, _) => method.Name == "get_Configuration" ? new ServerConfiguration { UICulture = "en" } : null);
        var serializer = Proxy<IXmlSerializer>((method, _) => method.Name.StartsWith("Deserialize") ? throw new FileNotFoundException() : null);
        var plugin = new HomeScreenSectionsPlugin(paths, serializer, config, new EmptyServices(), manager, translations);
        plugin.Configuration.SectionSettings = [new SectionSettings { SectionId = "Fixture", Enabled = true, OrderIndex = 1, LowerLimit = 2, UpperLimit = 3 }];
        Service = new HomeScreenSectionService(manager, NullLogger<HomeScreenSectionsPlugin>.Instance, translations, Cache, config);
    }
    private ModularHomeUserSettings Settings(Guid user)
    {
        BeforeSettings?.Invoke();
        return new ModularHomeUserSettings { UserId = user, EnabledSections = ["Fixture"] };
    }
    public void Dispose() => Directory.Delete(_directory, true);
    internal static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var instance = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)instance).Handler = handler;
        return instance;
    }
}
public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }
sealed class FixtureSection : IHomeScreenSection
{
    private int _created;
    public int Created => _created;
    public string? Section => "Fixture";
    public string? DisplayText { get; set; }
    public int? Limit => 2;
    public string? Route => null;
    public string? AdditionalData { get; set; }
    public object? OriginalPayload => null;
    public QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection query) => new();
    public IEnumerable<IHomeScreenSection> CreateInstances(Guid? user, int count)
    {
        var generation = Interlocked.Increment(ref _created);
        return Enumerable.Range(0, count).Select(i => (IHomeScreenSection)new FixtureSection { AdditionalData = $"{user:N}:{generation}:{i}" }).ToArray();
    }
    public HomeScreenSectionInfo GetInfo() => new() { Section = Section, AdditionalData = AdditionalData, PluginConfigurationOptions = [] };
}
