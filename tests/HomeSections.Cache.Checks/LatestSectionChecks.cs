using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.HomeScreenSections;
using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Latest;
using Jellyfin.Plugin.HomeScreenSections.Library;
using Jellyfin.Plugin.HomeScreenSections.Model.Dto;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

static class LatestSectionChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("latest books with no eligible dates performs one bounded query", () =>
        {
            using var f = new LatestFixture([[new Book(), new Book()]]);
            var result = f.Read();
            Require(result.Items.Count == 0, "unknown publication dates must retain their existing exclusion");
            Require(f.Queries.Count == 1, $"expected one query, got {f.Queries.Count}");
            Require(f.Queries.All(q => !q.EnableTotalRecordCount), "home rows must not count every matching item");
        });
        check("latest books returns sparse older releases without monthly backtracking", () =>
        {
            var books = new[] { BookAt(-1500), BookAt(-5000), BookAt(-9000) };
            using var f = new LatestFixture([books]);
            var result = f.Read();
            Require(result.Items.Select(i => i.Id).SequenceEqual(books.Select(b => b.Id)), "release-date order or older books changed");
            Require(f.Queries.Count == 1, $"sparse results caused {f.Queries.Count} date-window queries");
        });
        check("latest books globally limits and deduplicates multiple visible libraries", () =>
        {
            var a = Enumerable.Range(0, 20).Select(i => BookAt(-i-1)).ToArray();
            var b = Enumerable.Range(0, 20).Select(i => BookAt(-i-1.5)).ToArray();
            b[0] = a[0];
            using var f = new LatestFixture([a,b]);
            var result = f.Read();
            var expected = a.Concat(b).DistinctBy(i => i.Id).OrderByDescending(i => i.PremiereDate).Take(16).Select(i => i.Id);
            Require(result.Items.Select(i => i.Id).SequenceEqual(expected), "expected exactly the newest 16 unique items across libraries");
            Require(f.Queries.Count == 2 && f.Queries.All(q => q.Limit == 16), "queries must remain bounded per library");
        });
        check("latest books preserves watched filtering and excludes future releases", () =>
        {
            var watched = BookAt(-1); var unread = BookAt(-5); var future = BookAt(30);
            using var f = new LatestFixture([[watched,unread,future]], hideWatched:true);
            f.Played.Add(watched.Id);
            var result = f.Read();
            Require(result.Items.Count == 1 && result.Items[0].Id == unread.Id, "watched or future entries leaked into the row");
            Require(f.Queries.All(q => q.User?.Id == f.User.Id && q.IsPlayed == false), "queries lost the requesting user or watched filter");
        });
        check("latest books never queries a library the user cannot access", () =>
        {
            var allowed = BookAt(-10); var hidden = BookAt(-1);
            using var f = new LatestFixture([[allowed],[hidden]], deniedLibrary:1);
            var result = f.Read();
            Require(result.Items.Count == 1 && result.Items[0].Id == allowed.Id, "an inaccessible library contributed items");
            Require(f.Queries.All(q => q.ParentId == f.Folders[0].Id), "an inaccessible library was queried");
        });
        check("latest shows returns sparse series without monthly backtracking", () =>
        {
            var episodes = new[] { EpisodeAt(Guid.NewGuid(), -1500), EpisodeAt(Guid.NewGuid(), -5000) };
            using var f = new LatestFixture([episodes], shows:true);
            var result = f.Read();
            Require(result.Items.Select(i => i.Id).SequenceEqual(episodes.Select(e => e.SeriesId)), "latest episode ordering changed");
            Require(f.Queries.Count == 1, $"sparse series caused {f.Queries.Count} episode date-window queries");
            Require(f.Queries.All(q => !q.EnableTotalRecordCount), "episode queries requested unnecessary totals");
        });
        check("latest shows pages past a long-running series to find 16 distinct series", () =>
        {
            var daily = Guid.NewGuid();
            var episodes = Enumerable.Range(0,250).Select(i => EpisodeAt(daily,-i-1))
                .Concat(Enumerable.Range(0,16).Select(i => EpisodeAt(Guid.NewGuid(),-i-251))).ToArray();
            using var f = new LatestFixture([episodes], shows:true);
            var result = f.Read();
            var expected = episodes.Select(e => e.SeriesId).Distinct().Take(16);
            Require(result.Items.Select(i => i.Id).SequenceEqual(expected), "a long-running series hid older series or disrupted ordering");
            Require(f.Queries.Count == 2, $"expected two bounded episode pages, got {f.Queries.Count}");
        });
        check("latest shows skips series materialization when no episodes qualify", () =>
        {
            using var f = new LatestFixture([Array.Empty<BaseItem>()], shows:true);
            Require(f.Read().Items.Count == 0, "an empty episode library returned series");
            Require(f.Queries.Count == 1 && f.SeriesQueries.Count == 0, "empty libraries must not backtrack or run an unscoped series query");
        });
        check("latest shows preserves access scope in batched series lookups", () =>
        {
            var allowed = EpisodeAt(Guid.NewGuid(), -10); var hidden = EpisodeAt(Guid.NewGuid(), -1);
            using var f = new LatestFixture([[allowed],[hidden]], deniedLibrary:1, shows:true, hideWatched:true);
            var result = f.Read();
            Require(result.Items.Count == 1 && result.Items[0].Id == allowed.SeriesId, "a hidden library contributed a series");
            Require(f.Queries.All(q => q.ParentId == f.Folders[0].Id && q.User?.Id == f.User.Id && q.IsPlayed == false), "episode queries lost visibility or watched scope");
            Require(f.SeriesQueries.All(q => q.User?.Id == f.User.Id && !q.ItemIds.Contains(hidden.SeriesId)), "batched series lookup crossed access boundaries");
        });
        check("latest shows scans a newer library beyond older candidates from another library", () =>
        {
            var older = Enumerable.Range(0,16).Select(i => EpisodeAt(Guid.NewGuid(),-1000-i)).ToArray();
            var daily = Guid.NewGuid();
            var newer = Enumerable.Range(0,250).Select(i => EpisodeAt(daily,-i-1))
                .Concat(Enumerable.Range(0,16).Select(i => EpisodeAt(Guid.NewGuid(),-251-i))).ToArray();
            using var f = new LatestFixture([older,newer], shows:true);
            var expected = newer.Select(e => e.SeriesId).Distinct().Take(16);
            Require(f.Read().Items.Select(i => i.Id).SequenceEqual(expected), "older cached candidates stopped newer episode pagination early");
            Require(f.Queries.Count == 3, "multi-library scans must use bounded episode pages");
        });
    }

    private static Book BookAt(double days) => new() { Id = Guid.NewGuid(), PremiereDate = DateTime.Now.AddDays(days) };
    private static Episode EpisodeAt(Guid seriesId, double days) => new() { Id = Guid.NewGuid(), SeriesId=seriesId, PremiereDate=DateTime.Now.AddDays(days) };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

sealed class LatestFixture : IDisposable
{
    private readonly Fixture _plugin = new();
    private readonly IHomeScreenSection _section;
    private readonly ILibraryManager? _previousLibrary = BaseItem.LibraryManager;
    public User User { get; } = new("reader", "auth", "reset");
    public List<InternalItemsQuery> Queries { get; } = [];
    public List<InternalItemsQuery> SeriesQueries { get; } = [];
    private readonly Dictionary<Guid, Series> _series = [];
    public List<QueryFolder> Folders { get; } = [];
    public HashSet<Guid> Played { get; } = [];

    public LatestFixture(BaseItem[][] libraries, bool hideWatched = false, int? deniedLibrary = null, bool shows = false)
    {
        for (int i = 0; i < libraries.Length; i++)
        {
            var books = libraries[i];
            foreach (var episode in books.OfType<Episode>())
                _series.TryAdd(episode.SeriesId, new Series { Id=episode.SeriesId });
            var folder = new QueryFolder { Id = Guid.NewGuid(), Name = "Fixture library "+i };
            folder.ReadItems = query =>
            {
                Queries.Add(query);
                var matching = books.Where(b => b.PremiereDate.HasValue
                    && (!query.MinPremiereDate.HasValue || b.PremiereDate >= query.MinPremiereDate)
                    && (!query.MaxPremiereDate.HasValue || b.PremiereDate <= query.MaxPremiereDate)
                    && (!query.IsPlayed.HasValue || Played.Contains(b.Id) == query.IsPlayed));
                var ordered = matching.OrderByDescending(b => b.PremiereDate).Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToArray();
                return new QueryResult<BaseItem>(ordered);
            };
            Folders.Add(folder);
        }
        var users = Fixture.Proxy<IUserManager>((method, _) => method.Name == "GetUserById" ? User : null);
        var library = Fixture.Proxy<ILibraryManager>((method, args) => method.Name switch
        {
            "GetVirtualFolders" => Folders.Select(f => new VirtualFolderInfo { ItemId = f.Id.ToString(), Name = f.Name, CollectionType = shows ? CollectionTypeOptions.tvshows : CollectionTypeOptions.books }).ToList(),
            "GetItemsResult" => new QueryResult<BaseItem>(Folders.Cast<BaseItem>().ToArray()),
            "GetParentItem" => Folders.First(f => f.Id == (Guid)args![0]!),
            "GetItemById" => _series.TryGetValue((Guid)args![0]!, out var series) ? (BaseItem)series : Folders.Where((f,i) => i != deniedLibrary && f.Id == (Guid)args![0]!).FirstOrDefault(),
            "GetItemList" => ReadSeries((InternalItemsQuery)args![0]!),
            _ => throw new InvalidOperationException("Unexpected library operation "+method.Name)
        });
        var dto = Fixture.Proxy<IDtoService>((method, args) => method.Name == "GetBaseItemDto"
            ? new BaseItemDto { Id = ((BaseItem)args![0]!).Id } : null);
        BaseItem.LibraryManager = library;
        HomeScreenSectionsPlugin.Instance.Configuration.SectionSettings = [new SectionSettings { SectionId=shows ? "LatestShows" : "LatestBooks", HideWatchedItems=hideWatched }];
        var views = Fixture.Proxy<IUserViewManager>((_,_) => null);
        _section = shows ? new LatestShowsSection(views, users, library, Fixture.Proxy<ITVSeriesManager>((_,_)=>null), dto, new EmptyServices())
            : new LatestBooksSection(views, users, library, dto, new EmptyServices());
    }
    public QueryResult<BaseItemDto> Read() => _section.GetResults(new HomeScreenSectionPayload { UserId=User.Id, AdditionalData="books" }, new QueryCollection());
    private IReadOnlyList<BaseItem> ReadSeries(InternalItemsQuery query)
    {
        SeriesQueries.Add(query);
        return query.ItemIds.Where(_series.ContainsKey).Select(id => (BaseItem)_series[id]).ToList();
    }
    public void Dispose() { BaseItem.LibraryManager = _previousLibrary!; _plugin.Dispose(); }
}

sealed class QueryFolder : Folder
{
    public Func<InternalItemsQuery, QueryResult<BaseItem>> ReadItems { get; set; } = null!;
    protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query) => ReadItems(query);
}
