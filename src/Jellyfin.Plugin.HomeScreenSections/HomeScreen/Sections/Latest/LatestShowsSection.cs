using Jellyfin.Plugin.HomeScreenSections.Configuration;
using Jellyfin.Plugin.HomeScreenSections.Helpers;
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

namespace Jellyfin.Plugin.HomeScreenSections.HomeScreen.Sections.Latest
{
    public class LatestShowsSection : LatestSectionBase
    {
        public override string? Section => "LatestShows";
        
        public override string? Route => "tvshows";

        public override string? DisplayText { get; set; } = "Latest Shows";

        private readonly ITVSeriesManager m_tvSeriesManager;
        
        public LatestShowsSection(IUserViewManager userViewManager,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ITVSeriesManager tvSeriesManager,
            IDtoService dtoService,
            IServiceProvider serviceProvider) : base(userViewManager, userManager, libraryManager, dtoService, serviceProvider)
        {
            m_tvSeriesManager = tvSeriesManager;
        }

        public override SectionViewMode DefaultViewMode => SectionViewMode.Landscape;
        protected override BaseItemKind SectionItemKind => BaseItemKind.Episode;
        protected override CollectionType CollectionType => CollectionType.tvshows;
        protected override string? LibraryId => HomeScreenSectionsPlugin.Instance?.Configuration?.DefaultTVShowsLibraryId;
        protected override CollectionTypeOptions CollectionTypeOptions => CollectionTypeOptions.tvshows;

        public override QueryResult<BaseItemDto> GetResults(HomeScreenSectionPayload payload, IQueryCollection queryCollection)
        {
            DtoOptions? dtoOptions = new DtoOptions
            {
                Fields = new List<ItemFields>
                {
                    ItemFields.PrimaryImageAspectRatio,
                    ItemFields.Path
                }
            };

            dtoOptions.ImageTypeLimit = 1;
            dtoOptions.ImageTypes = new List<ImageType>
            {
                ImageType.Thumb,
                ImageType.Backdrop,
                ImageType.Primary,
            };

            User? user = m_userManager.GetUserById(payload.UserId);

            var config = HomeScreenSectionsPlugin.Instance?.Configuration;
            var sectionSettings = config?.SectionSettings.FirstOrDefault(x => x.SectionId == Section);
            // If HideWatchedItems is enabled for this section, set isPlayed to false to hide watched items; otherwise, include all.
            bool? isPlayed = sectionSettings?.HideWatchedItems == true ? false : null;

            VirtualFolderInfo[] folders = m_libraryManager.GetVirtualFolders()
                .Where(x => x.CollectionType == CollectionTypeOptions)
                .FilterToUserPermitted(m_libraryManager, user);

            const int resultLimit = 16;
            const int episodePageSize = 200;
            DateTime currentDate = DateTime.Now;
            DateTime stopDate = new DateTime(1925, 1, 1);
            DateTime earliestDate = currentDate.AddDays(-30 * Math.Ceiling((currentDate - stopDate).TotalDays / 30));
            var latestDates = new Dictionary<Guid, DateTime>();
            var allowedSeries = new HashSet<Guid>();
            var missingSeries = new HashSet<Guid>();
            var lightweight = new DtoOptions { Fields = Array.Empty<ItemFields>(), EnableImages = false };

            foreach (var library in folders)
            {
                var item = m_libraryManager.GetParentItem(Guid.Parse(library.ItemId), user?.Id);
                var folder = item as Folder ?? m_libraryManager.GetUserRootFolder();
                int startIndex = 0;
                while (true)
                {
                    // Walk actual episodes newest-first, rather than issuing an
                    // empty query for every month back to 1925. Pagination still
                    // finds older series when one long-running show fills a page.
                    var batch = folder.GetItems(new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = new[] { SectionItemKind },
                        OrderBy = new[] { (ItemSortBy.PremiereDate, SortOrder.Descending) },
                        Limit = episodePageSize,
                        StartIndex = startIndex,
                        IsVirtualItem = false,
                        IsPlayed = isPlayed,
                        Recursive = true,
                        ParentId = folder.Id,
                        MaxPremiereDate = currentDate,
                        MinPremiereDate = earliestDate,
                        EnableTotalRecordCount = false,
                        DtoOptions = lightweight
                    }).Items;
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    var candidates = batch.OfType<Episode>()
                        .Where(episode => !episode.IsUnaired && episode.PremiereDate.HasValue)
                        .Select(episode => (SeriesId: episode.SeriesId != Guid.Empty ? episode.SeriesId : episode.FindSeriesId(),
                            PremiereDate: episode.PremiereDate!.Value))
                        .Where(episode => episode.SeriesId != Guid.Empty)
                        .ToArray();
                    var unknownIds = candidates.Select(episode => episode.SeriesId).Distinct()
                        .Where(id => !allowedSeries.Contains(id) && !missingSeries.Contains(id)).ToArray();
                    if (unknownIds.Length > 0)
                    {
                        // Resolve each distinct series in a batch, avoiding the
                        // Episode.Series database lookup on every episode.
                        var foundIds = m_libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            ItemIds = unknownIds,
                            IncludeItemTypes = new[] { BaseItemKind.Series },
                            DtoOptions = lightweight,
                            EnableTotalRecordCount = false
                        }).OfType<Series>().Select(series => series.Id).ToHashSet();
                        allowedSeries.UnionWith(foundIds);
                        missingSeries.UnionWith(unknownIds.Where(id => !foundIds.Contains(id)));
                    }
                    foreach (var episode in candidates.Where(episode => allowedSeries.Contains(episode.SeriesId)))
                    {
                        if (!latestDates.TryGetValue(episode.SeriesId, out var previous) || episode.PremiereDate > previous)
                        {
                            latestDates[episode.SeriesId] = episode.PremiereDate;
                        }
                    }

                    if (batch.Count < episodePageSize)
                    {
                        break;
                    }
                    if (latestDates.Count >= resultLimit)
                    {
                        var cutoff = latestDates.Values.OrderByDescending(date => date).Take(resultLimit).Last();
                        var oldestScanned = batch.Last().PremiereDate;
                        if (oldestScanned.HasValue && oldestScanned.Value <= cutoff)
                        {
                            break; // Unseen episodes cannot improve the newest sixteen.
                        }
                    }
                    startIndex += batch.Count;
                }
            }
            
            // Fetch the full series objects with proper DtoOptions for images
            var seriesIdArray = latestDates.OrderByDescending(entry => entry.Value).Take(resultLimit).Select(entry => entry.Key).ToArray();
            if (seriesIdArray.Length == 0)
            {
                return new QueryResult<BaseItemDto>();
            }
            var seriesItems = m_libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = seriesIdArray,
                EnableTotalRecordCount = false,
                DtoOptions = dtoOptions
            });
            
            // Maintain the order from our sorted list
            var orderedSeries = seriesIdArray
                .Select(id => seriesItems.FirstOrDefault(s => s.Id == id))
                .Where(s => s != null)
                .ToList();
            
            return new QueryResult<BaseItemDto>(Array.ConvertAll(orderedSeries.ToArray(),
                i => m_dtoService.GetBaseItemDto(i!, dtoOptions, user)));
        }

        protected override LatestSectionBase CreateInstance()
        {
            return new LatestShowsSection(m_userViewManager, m_userManager, m_libraryManager, m_tvSeriesManager, m_dtoService, m_serviceProvider);
        }
    }
}
