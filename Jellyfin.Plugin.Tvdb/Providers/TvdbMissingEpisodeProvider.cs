using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Plugin.Tvdb.Configuration;
using Jellyfin.Plugin.Tvdb.Services;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tvdb.Sdk;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Season = MediaBrowser.Controller.Entities.TV.Season;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.Tvdb.Providers
{
    /// <summary>
    /// Tvdb Missing Episode provider.
    /// </summary>
    public class TvdbMissingEpisodeProvider : IHostedService
    {
        /// <summary>
        /// The provider name.
        /// </summary>
        public static readonly string ProviderName = "Missing Episode Fetcher";

        private readonly TvdbClientManager _tvdbClientManager;
        private readonly IBaseItemManager _baseItemManager;
        private readonly IProviderManager _providerManager;
        private readonly ILocalizationManager _localization;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<TvdbMissingEpisodeProvider> _logger;
        private readonly RuntimeTracker? _runtimeTracker;
        private readonly PlaceholderStubTracker? _placeholderTracker;
        private readonly string? _pluginPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="TvdbMissingEpisodeProvider"/> class.
        /// </summary>
        /// <param name="tvdbClientManager">Instance of the <see cref="TvdbClientManager"/> class.</param>
        /// <param name="baseItemManager">Instance of the <see cref="IBaseItemManager"/> interface.</param>
        /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
        /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{TvdbMissingEpisodeProvider}"/> interface.</param>
        public TvdbMissingEpisodeProvider(
            TvdbClientManager tvdbClientManager,
            IBaseItemManager baseItemManager,
            IProviderManager providerManager,
            ILocalizationManager localization,
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            ILogger<TvdbMissingEpisodeProvider> logger)
        {
            _tvdbClientManager = tvdbClientManager;
            _baseItemManager = baseItemManager;
            _providerManager = providerManager;
            _localization = localization;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _logger = logger;

            // Initialize trackers if plugin directory is available
            _pluginPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (_pluginPath != null)
            {
                // Pass null for logger since we can't create typed loggers without ILoggerFactory
                // The trackers will work without logging
                _runtimeTracker = new RuntimeTracker(_pluginPath, null);
                _placeholderTracker = new PlaceholderStubTracker(_pluginPath, null);
            }
        }

        private static bool IncludeMissingSpecials => TvdbPlugin.Instance?.Configuration.IncludeMissingSpecials ?? false;

        private static bool RemoveAllMissingEpisodesOnRefresh => TvdbPlugin.Instance?.Configuration.RemoveAllMissingEpisodesOnRefresh ?? false;

        private static bool CreateStubFilesForMissingEpisodes => TvdbPlugin.Instance?.Configuration.CreateStubFilesForMissingEpisodes ?? true;

        private static bool EpisodeExists(EpisodeBaseRecord episodeRecord, IReadOnlyList<Episode> existingEpisodes)
        {
            return existingEpisodes.Any(episode => EpisodeEquals(episode, episodeRecord));
        }

        private static bool EpisodeEquals(Episode episode, EpisodeBaseRecord otherEpisodeRecord)
        {
            return otherEpisodeRecord.Number.HasValue
                && episode.ContainsEpisodeNumber(otherEpisodeRecord.Number.Value)
                && episode.ParentIndexNumber == otherEpisodeRecord.SeasonNumber;
        }

        /// <summary>
        /// Is Metadata fetcher enabled for Series, Season or Episode.
        /// </summary>
        /// <param name="item">Series, Season or Episode.</param>
        /// <returns>true if enabled.</returns>
        private bool IsEnabledForLibrary(BaseItem item)
        {
            Series? series = item switch
            {
                Episode episode => episode.Series,
                Season season => season.Series,
                _ => item as Series
            };

            if (series == null)
            {
                _logger.LogDebug("Given input is not in {@ValidTypes}: {Type}", new[] { nameof(Series), nameof(Season), nameof(Episode) }, item.GetType());
                return false;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(series);
            var typeOptions = libraryOptions.GetTypeOptions(series.GetType().Name);
            return _baseItemManager.IsMetadataFetcherEnabled(series, typeOptions, ProviderName);
        }

        // TODO use the new async events when provider manager is updated
        private void OnProviderManagerRefreshComplete(object? sender, GenericEventArgs<BaseItem> genericEventArgs)
        {
            if (!IsEnabledForLibrary(genericEventArgs.Argument))
            {
                _logger.LogDebug("{ProviderName} not enabled for {InputName}", ProviderName, genericEventArgs.Argument.Name);
                return;
            }

            _logger.LogDebug("{MethodName}: Try Refreshing for Item {Name} {Type}", nameof(OnProviderManagerRefreshComplete), genericEventArgs.Argument.Name, genericEventArgs.Argument.GetType());
            if (genericEventArgs.Argument is Series series)
            {
                _logger.LogDebug("{MethodName}: Refreshing Series {SeriesName}", nameof(OnProviderManagerRefreshComplete), series.Name);
                HandleSeries(series).GetAwaiter().GetResult();
            }

            if (genericEventArgs.Argument is Season season)
            {
                _logger.LogDebug("{MethodName}: Refreshing {SeriesName} {SeasonName}", nameof(OnProviderManagerRefreshComplete), season.Series?.Name, season.Name);
                HandleSeason(season).GetAwaiter().GetResult();
            }
        }

        private async Task HandleSeries(Series series)
        {
            if (!series.HasTvdbId())
            {
                _logger.LogDebug("No TVDB Id available.");
                return;
            }

            var tvdbId = series.GetTvdbId();

            var children = series.Children.ToList();
            var existingSeasons = new List<Season>();

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child is Season season && child.IndexNumber.HasValue)
                {
                    existingSeasons.Add(season);
                }
            }

            var allEpisodes = RemoveAllMissingEpisodesOnRefresh
                ? Array.Empty<EpisodeBaseRecord>()
                : await GetAllEpisodes(tvdbId, series.DisplayOrder, series.GetPreferredMetadataLanguage()).ConfigureAwait(false);

            if (!IncludeMissingSpecials)
            {
                allEpisodes = allEpisodes.Where(e => e.SeasonNumber != 0).ToList();
            }

            var allSeasons = allEpisodes
                .Where(ep => ep.SeasonNumber.HasValue)
                .Select(ep => ep.SeasonNumber!.Value)
                .Distinct()
                .ToList();

            // Create stub files for all missing episodes BEFORE creating virtual items
            // This ensures all files exist before metadata refresh
            if (CreateStubFilesForMissingEpisodes && !string.IsNullOrEmpty(series.Path))
            {
                await CreateStubFilesForAllEpisodesAsync(series, allEpisodes, existingSeasons, allSeasons, CancellationToken.None).ConfigureAwait(false);
            }

            // Add missing seasons
            var newSeasons = AddMissingSeasons(series, existingSeasons, allSeasons);

            // Add new seasons to existingSeasons
            existingSeasons.AddRange(newSeasons);

            // Run HandleSeason for each season
            foreach (var newSeason in existingSeasons)
            {
                await HandleSeason(newSeason, allEpisodes).ConfigureAwait(false);
            }

            // Get seasons that does not match the TVDB seasons and has no episodes
            var orphanedSeasons = existingSeasons
                .Where(season => !allSeasons.Contains(season.IndexNumber!.Value) && season.GetEpisodes().Count == 0)
                .ToList();

            DeleteVirtualItems(orphanedSeasons);
        }

        private async Task HandleSeason(Season season, IReadOnlyList<EpisodeBaseRecord>? allEpisodesRemote = null)
        {
            var series = season.Series;
            if (!series.HasTvdbId())
            {
                _logger.LogDebug("No TVDB Id available.");
                return;
            }

            var tvdbId = series.GetTvdbId();
            var allEpisodes = RemoveAllMissingEpisodesOnRefresh
                ? Array.Empty<EpisodeBaseRecord>()
                : allEpisodesRemote ??
                await GetAllEpisodes(tvdbId, series.DisplayOrder, season.GetPreferredMetadataLanguage())
                    .ConfigureAwait(false);
            // Skip if called from HandleSeries since it will be filtered there, allEpisodesRemote will not be null when called from HandleSeries
            // Remove specials if IncludeMissingSpecials is false
            if (allEpisodesRemote is null && !IncludeMissingSpecials)
            {
                allEpisodes = allEpisodes.Where(e => e.SeasonNumber != 0).ToList();
            }

            var seasonEpisodes = allEpisodes.Where(e => e.SeasonNumber == season.IndexNumber).ToList();
            var existingEpisodes = season.GetEpisodes().OfType<Episode>().ToHashSet();

            foreach (var episodeRecord in seasonEpisodes)
            {
                var foundEpisodes = existingEpisodes.Where(episode => EpisodeEquals(episode, episodeRecord)).ToList();
                if (foundEpisodes.Count != 0)
                {
                    // So we have at least one existing episode for our episodeRecord
                    var physicalEpisodes = foundEpisodes.Where(e => !e.IsVirtualItem);
                    if (physicalEpisodes.Any())
                    {
                        // if there is a physical episode we can delete existing virtual episode entries
                        var virtualEpisodes = foundEpisodes.Where(e => e.IsVirtualItem).ToList();
                        DeleteVirtualItems(virtualEpisodes);
                        existingEpisodes.ExceptWith(virtualEpisodes);
                    }

                    continue;
                }

                AddVirtualEpisode(episodeRecord, season);
            }

            var orphanedEpisodes = existingEpisodes
                .Where(e => e.ParentIndexNumber == season.IndexNumber)
                .Where(e => e.IsVirtualItem)
                .Where(e => !seasonEpisodes.Any(episodeRecord => EpisodeEquals(e, episodeRecord)))
                .ToList();
            DeleteVirtualItems(orphanedEpisodes);
        }

        private void OnLibraryManagerItemUpdated(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            _logger.LogDebug("{MethodName}: Refreshing Item {ItemName} [{Reason}]", nameof(OnLibraryManagerItemUpdated), itemChangeEventArgs.Item.Name, itemChangeEventArgs.UpdateReason);
            // Only interested in real Season and Episode items
            if (itemChangeEventArgs.Item.IsVirtualItem
                || !(itemChangeEventArgs.Item is Season || itemChangeEventArgs.Item is Episode))
            {
                _logger.LogDebug("Skip: Updated item is {ItemType}.", itemChangeEventArgs.Item.IsVirtualItem ? "Virtual" : "no Season or Episode");
                return;
            }

            if (!IsEnabledForLibrary(itemChangeEventArgs.Item))
            {
                _logger.LogDebug("{ProviderName} not enabled for {InputName}", ProviderName, itemChangeEventArgs.Item.Name);
                return;
            }

            var existingVirtualItems = GetVirtualItems(itemChangeEventArgs.Item, itemChangeEventArgs.Parent);
            DeleteVirtualItems(existingVirtualItems);
        }

        private IReadOnlyList<BaseItem> GetVirtualItems(BaseItem item, BaseItem? parent)
        {
            var query = new InternalItemsQuery
            {
                IsVirtualItem = true,
                IndexNumber = item.IndexNumber,
                // If the item is an Episode, filter on ParentIndexNumber as well (season number)
                ParentIndexNumber = item is Episode ? item.ParentIndexNumber : null,
                IncludeItemTypes = new[] { item.GetBaseItemKind() },
                Parent = parent,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(true)
            };

            var existingVirtualItems = _libraryManager.GetItemList(query);
            return existingVirtualItems;
        }

        private void DeleteVirtualItems<T>(IReadOnlyList<T> existingVirtualItems)
            where T : BaseItem
        {
            var deleteOptions = new DeleteOptions
            {
                DeleteFileLocation = false
            };

            // Remove the virtual season/episode that matches the newly updated item
            for (var i = 0; i < existingVirtualItems.Count; i++)
            {
                var currentItem = existingVirtualItems[i];
                _logger.LogDebug("Delete VirtualItem {Name} - S{Season:00}E{Episode:00}", currentItem.Name, currentItem.ParentIndexNumber, currentItem.IndexNumber);
                _libraryManager.DeleteItem(currentItem, deleteOptions);
            }
        }

        // TODO use async events
        private void OnLibraryManagerItemRemoved(object? sender, ItemChangeEventArgs itemChangeEventArgs)
        {
            _logger.LogDebug("{MethodName}: Refreshing {ItemName} [{Reason}]", nameof(OnLibraryManagerItemRemoved), itemChangeEventArgs.Item.Name, itemChangeEventArgs.UpdateReason);
            // No action needed if the item is virtual
            if (itemChangeEventArgs.Item.IsVirtualItem || !IsEnabledForLibrary(itemChangeEventArgs.Item))
            {
                _logger.LogDebug("Skip: {Message}.", itemChangeEventArgs.Item.IsVirtualItem ? "Updated item is Virtual" : "Update not enabled");
                return;
            }

            // Create a new virtual season if the real one was deleted.
            // Similarly, create a new virtual episode if the real one was deleted.
            if (itemChangeEventArgs.Item is Season season)
            {
                var newSeason = AddVirtualSeason(season.IndexNumber!.Value, season.Series);
                HandleSeason(newSeason).GetAwaiter().GetResult();
            }
            else if (itemChangeEventArgs.Item is Episode episode)
            {
                if (!episode.Series.HasTvdbId())
                {
                    _logger.LogDebug("No TVDB Id available.");
                    return;
                }

                var tvdbId = episode.Series.GetTvdbId();
                var displayOrder = episode.Series.DisplayOrder;

                var episodeRecords = GetAllEpisodes(tvdbId, displayOrder, episode.GetPreferredMetadataLanguage()).GetAwaiter().GetResult();

                EpisodeBaseRecord? episodeRecord = null;
                if (episodeRecords.Count > 0)
                {
                    episodeRecord = episodeRecords.FirstOrDefault(e => EpisodeEquals(episode, e));
                }

                AddVirtualEpisode(episodeRecord, episode.Season);
            }
        }

        private async Task<IReadOnlyList<EpisodeBaseRecord>> GetAllEpisodes(int tvdbId, string displayOrder, string acceptedLanguage)
        {
            try
            {
                // If displayOrder is not set, use aired order
                if (string.IsNullOrEmpty(displayOrder))
                {
                    displayOrder = "official";
                }

                // Fetch all episodes for the series
                var seriesInfo = await _tvdbClientManager.GetSeriesEpisodesAsync(tvdbId, acceptedLanguage, displayOrder, CancellationToken.None).ConfigureAwait(false);
                var allEpisodes = seriesInfo.Episodes;
                if (allEpisodes is null || !allEpisodes.Any())
                {
                    _logger.LogWarning("Unable to get episodes from TVDB: Episode Query returned null for TVDB Id: {TvdbId}", tvdbId);
                    return Array.Empty<EpisodeBaseRecord>();
                }

                _logger.LogDebug("{MethodName}: For TVDB Id '{TvdbId}' found #{Count} [{Episodes}]", nameof(GetAllEpisodes), tvdbId, allEpisodes.Count, string.Join(", ", allEpisodes.Select(e => $"S{e.SeasonNumber}E{e.Number}")));
                return allEpisodes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to get episodes from TVDB for Id '{TvdbId}'", tvdbId);
                return Array.Empty<EpisodeBaseRecord>();
            }
        }

        private IEnumerable<Season> AddMissingSeasons(Series series, List<Season> existingSeasons, IReadOnlyList<int> allSeasons)
        {
            var missingSeasons = allSeasons.Except(existingSeasons.Select(s => s.IndexNumber!.Value)).ToList();
            for (var i = 0; i < missingSeasons.Count; i++)
            {
                var season = missingSeasons[i];
                yield return AddVirtualSeason(season, series);
            }
        }

        private void AddMissingEpisodes(
            Dictionary<int, List<Episode>> existingEpisodes,
            IReadOnlyList<EpisodeBaseRecord> allEpisodeRecords,
            IReadOnlyList<Season> existingSeasons)
        {
            for (var i = 0; i < allEpisodeRecords.Count; i++)
            {
                var episodeRecord = allEpisodeRecords[i];

                // skip if it exists already
                if (episodeRecord.SeasonNumber.HasValue
                    && existingEpisodes.TryGetValue(episodeRecord.SeasonNumber.Value, out var episodes)
                    && EpisodeExists(episodeRecord, episodes))
                {
                    _logger.LogDebug("{MethodName}: Skip, already existing S{Season:00}E{Episode:00}", nameof(AddMissingEpisodes), episodeRecord.SeasonNumber, episodeRecord.Number);
                    continue;
                }

                var existingSeason = existingSeasons.First(season => season.IndexNumber.HasValue && season.IndexNumber.Value == episodeRecord.SeasonNumber);

                AddVirtualEpisode(episodeRecord, existingSeason);
            }
        }

        private Season AddVirtualSeason(int season, Series series)
        {
            string seasonName;
            if (season == 0)
            {
                seasonName = _libraryManager.GetLibraryOptions(series).SeasonZeroDisplayName;
            }
            else
            {
                seasonName = string.Format(
                    CultureInfo.InvariantCulture,
                    _localization.GetLocalizedString("NameSeasonNumber"),
                    season.ToString(CultureInfo.InvariantCulture));
            }

            _logger.LogDebug("Creating Season {SeasonName} entry for {SeriesName}", seasonName, series.Name);

            var newSeason = new Season
            {
                Name = seasonName,
                IndexNumber = season,
                Id = _libraryManager.GetNewItemId(
                    series.Id + season.ToString(CultureInfo.InvariantCulture) + seasonName,
                    typeof(Season)),
                IsVirtualItem = true,
                SeriesId = series.Id,
                SeriesName = series.Name,
                SeriesPresentationUniqueKey = series.GetPresentationUniqueKey()
            };

            series.AddChild(newSeason);
            _providerManager.QueueRefresh(newSeason.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);

            return newSeason;
        }

        private void AddVirtualEpisode(EpisodeBaseRecord? episode, Season? season)
        {
            if (episode?.SeasonNumber == null || season == null)
            {
                return;
            }

            // Try to find the stub file path that was created earlier
            string? stubFilePath = null;
            if (CreateStubFilesForMissingEpisodes && !string.IsNullOrEmpty(season.Series.Path))
            {
                stubFilePath = GetStubFilePath(episode, season);
                // Verify the file actually exists (might not if stub creation failed)
                if (!string.IsNullOrEmpty(stubFilePath) && !File.Exists(stubFilePath))
                {
                    stubFilePath = null;
                }
            }

            // Put as much metadata into it as possible
            var newEpisode = new Episode
            {
                Name = episode.Name,
                IndexNumber = episode.Number,
                ParentIndexNumber = episode.SeasonNumber,
                Id = _libraryManager.GetNewItemId(
                    $"{season.Series.Id}{episode.SeasonNumber}Episode {episode.Number}",
                    typeof(Episode)),
                IsVirtualItem = string.IsNullOrEmpty(stubFilePath), // Only virtual if no stub file exists
                Path = stubFilePath,
                SeasonId = season.Id,
                SeriesId = season.Series.Id,
                Overview = episode.Overview,
                SeriesName = season.Series.Name,
                SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey,
                SeasonName = season.Name,
                DateLastSaved = DateTime.UtcNow
            };

            // Below metadata info only applicable for Aired Order
            if (string.IsNullOrEmpty(season.Series.DisplayOrder))
            {
                newEpisode.AirsBeforeEpisodeNumber = episode.AirsBeforeEpisode;
                newEpisode.AirsAfterSeasonNumber = episode.AirsAfterSeason;
                newEpisode.AirsBeforeSeasonNumber = episode.AirsBeforeSeason;
            }

            if (DateTime.TryParse(episode!.Aired, out var premiereDate))
            {
                newEpisode.PremiereDate = premiereDate;
            }

            newEpisode.PresentationUniqueKey = newEpisode.GetPresentationUniqueKey();
            newEpisode.SetTvdbId(episode.Id);

            _logger.LogDebug(
                "Creating {Type} episode {SeriesName} S{Season:00}E{Episode:00}",
                newEpisode.IsVirtualItem ? "virtual" : "stub file",
                season.Series.Name,
                episode.SeasonNumber,
                episode.Number);

            season.AddChild(newEpisode);
            _providerManager.QueueRefresh(newEpisode.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
        }

        /// <summary>
        /// Checks placeholder stubs and updates them if runtime is now available.
        /// </summary>
        /// <param name="series">The series to check.</param>
        /// <param name="tvdbSeriesId">The TVDB series ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        private async Task CheckAndUpdatePlaceholderStubsAsync(Series series, int tvdbSeriesId, CancellationToken cancellationToken)
        {
            if (_placeholderTracker == null || _runtimeTracker == null)
            {
                return;
            }

            try
            {
                var placeholderStubs = _placeholderTracker.GetPlaceholderStubsForSeries(tvdbSeriesId);
                if (placeholderStubs.Count == 0)
                {
                    return;
                }

                _logger.LogDebug("Checking {Count} placeholder stubs for runtime updates in series {SeriesName}", placeholderStubs.Count, series.Name);

                // Get runtime for this series
                var runtimeMinutes = _runtimeTracker.GetSeriesRuntime(tvdbSeriesId);
                if (!runtimeMinutes.HasValue || runtimeMinutes.Value <= 0)
                {
                    return; // No runtime available yet
                }

                var config = TvdbPlugin.Instance?.Configuration;
                if (config == null)
                {
                    return;
                }

                var updatedCount = 0;

                foreach (var placeholder in placeholderStubs)
                {
                    if (!File.Exists(placeholder.FilePath))
                    {
                        // File doesn't exist, remove from tracker
                        _placeholderTracker.RemovePlaceholderStub(placeholder.TvdbSeriesId, placeholder.SeasonNumber, placeholder.EpisodeNumber);
                        continue;
                    }

                    // Change extension from .mp4 (placeholder) to .mkv (generated stub)
                    var newFilePath = Path.ChangeExtension(placeholder.FilePath, ".mkv");

                    try
                    {
                        // Delete old placeholder .mp4 stub
                        File.Delete(placeholder.FilePath);
                        _logger.LogDebug("Deleted placeholder stub: {FilePath}", placeholder.FilePath);

                        // Find the closest stub file and copy it
                        var stubFilePath = FindClosestStubFile(runtimeMinutes.Value);
                        if (stubFilePath != null)
                        {
                            File.Copy(stubFilePath, newFilePath, true); // Overwrite if exists
                            _placeholderTracker.RemovePlaceholderStub(placeholder.TvdbSeriesId, placeholder.SeasonNumber, placeholder.EpisodeNumber);
                            updatedCount++;
                            _logger.LogInformation("Successfully updated placeholder stub for series {SeriesName} episode S{SeasonNumber}E{EpisodeNumber} with runtime {Runtime} minutes", series.Name, placeholder.SeasonNumber, placeholder.EpisodeNumber, runtimeMinutes);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to find stub file for series {SeriesName} episode S{SeasonNumber}E{EpisodeNumber} with runtime {Runtime} minutes", series.Name, placeholder.SeasonNumber, placeholder.EpisodeNumber, runtimeMinutes);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating placeholder stub for series {SeriesName} episode S{SeasonNumber}E{EpisodeNumber}", series.Name, placeholder.SeasonNumber, placeholder.EpisodeNumber);
                    }
                }

                if (updatedCount > 0)
                {
                    _logger.LogInformation("Updated {Count} placeholder stubs with correct runtime for series {SeriesName}", updatedCount, series.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking and updating placeholder stubs for series {SeriesName}", series.Name);
            }
        }

        /// <summary>
        /// Find the closest stub file for the given runtime.
        /// Runtimes shorter than 10 minutes use the 10min stub.
        /// Runtimes longer than 4 hours (240 minutes) use the 240min stub.
        /// </summary>
        /// <param name="runtimeMinutes">Runtime in minutes.</param>
        /// <returns>Path to the closest stub file, or null if none found.</returns>
        private string? FindClosestStubFile(int runtimeMinutes)
        {
            try
            {
                var pluginPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (pluginPath == null)
                {
                    _logger.LogError("Cannot determine plugin directory for stub file lookup.");
                    return null;
                }

                var stubsPath = Path.Combine(pluginPath, "STUBS");
                if (!Directory.Exists(stubsPath))
                {
                    _logger.LogError("STUBS directory not found at: {0}", stubsPath);
                    return null;
                }

                var stubFiles = Directory.GetFiles(stubsPath, "*.mp4").Concat(Directory.GetFiles(stubsPath, "*.mkv")).ToList();
                if (stubFiles.Count == 0)
                {
                    _logger.LogError("No stub files found in STUBS directory: {0}", stubsPath);
                    return null;
                }

                // Parse stub file names to extract minute values
                var stubMinutes = new List<(int minutes, string filePath)>();
                foreach (var file in stubFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    // Look for pattern like "25min", "68min", etc.
                    var minIndex = fileName.IndexOf("min", StringComparison.OrdinalIgnoreCase);
                    if (minIndex > 0)
                    {
                        var minutePart = fileName.Substring(0, minIndex);
                        if (int.TryParse(minutePart, out var minutes))
                        {
                            stubMinutes.Add((minutes, file));
                        }
                    }
                }

                if (stubMinutes.Count == 0)
                {
                    _logger.LogError("No valid stub files found with 'min' naming pattern in STUBS directory");
                    return null;
                }

                // Apply runtime limits: minimum 10 minutes, maximum 240 minutes (4 hours)
                var targetMinutes = runtimeMinutes;
                if (targetMinutes < 10)
                {
                    targetMinutes = 10;
                    _logger.LogDebug("Runtime {0} minutes is below minimum, using 10min stub", runtimeMinutes);
                }
                else if (targetMinutes > 240)
                {
                    targetMinutes = 240;
                    _logger.LogDebug("Runtime {0} minutes is above maximum, using 240min stub", runtimeMinutes);
                }

                var closestStub = stubMinutes
                    .OrderBy(s => Math.Abs(s.minutes - targetMinutes))
                    .ThenBy(s => s.minutes) // If tie, prefer smaller (already rounded down)
                    .First();

                _logger.LogDebug("Found stub file for {0} minutes (original runtime: {1} minutes): {2} (stub has {3} minutes)", targetMinutes, runtimeMinutes, Path.GetFileName(closestStub.filePath), closestStub.minutes);

                return closestStub.filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding closest stub file for runtime {0} minutes", runtimeMinutes);
                return null;
            }
        }

        /// <summary>
        /// Creates stub files for all missing episodes in a series.
        /// This is called BEFORE creating virtual episodes to ensure all files exist.
        /// </summary>
        /// <param name="series">The series to create stub files for.</param>
        /// <param name="allEpisodes">All episodes from TVDB.</param>
        /// <param name="existingSeasons">Existing seasons in the series.</param>
        /// <param name="allSeasons">All season numbers from TVDB.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        private async Task CreateStubFilesForAllEpisodesAsync(
            Series series,
            IReadOnlyList<EpisodeBaseRecord> allEpisodes,
            IReadOnlyList<Season> existingSeasons,
            IReadOnlyList<int> allSeasons,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Creating stub files for all missing episodes in series {SeriesName}", series.Name);

            if (!series.HasTvdbId())
            {
                _logger.LogDebug("Series {SeriesName} does not have TVDB ID, skipping stub file creation", series.Name);
                return;
            }

            var tvdbSeriesId = series.GetTvdbId();

            // Check for runtime availability and update placeholder stubs if needed
            await CheckAndUpdatePlaceholderStubsAsync(series, tvdbSeriesId, cancellationToken).ConfigureAwait(false);

            // Get runtime for this series if available
            var runtimeMinutes = _runtimeTracker?.GetSeriesRuntime(tvdbSeriesId);
            var hasRuntime = runtimeMinutes.HasValue && runtimeMinutes.Value > 0;

            // Process each season
            foreach (var seasonNumber in allSeasons)
            {
                var seasonEpisodes = allEpisodes
                    .Where(e => e.SeasonNumber == seasonNumber && e.Number.HasValue)
                    .ToList();

                // Get existing season or create temp season info for path calculation
                var existingSeason = existingSeasons.FirstOrDefault(s => s.IndexNumber == seasonNumber);

                // Get existing episodes in this season to check what files already exist
                var existingEpisodeFiles = new HashSet<string>();
                if (existingSeason != null)
                {
                    foreach (var existingEpisode in existingSeason.GetEpisodes().OfType<Episode>())
                    {
                        if (!string.IsNullOrEmpty(existingEpisode.Path) && File.Exists(existingEpisode.Path))
                        {
                            existingEpisodeFiles.Add(existingEpisode.Path);
                        }
                    }
                }

                // Create season directory name
                string seasonDirName;
                if (seasonNumber == 0)
                {
                    var libraryOptions = _libraryManager.GetLibraryOptions(series);
                    seasonDirName = libraryOptions.SeasonZeroDisplayName ?? "Specials";
                }
                else
                {
                    seasonDirName = string.Format(CultureInfo.InvariantCulture, _localization.GetLocalizedString("NameSeasonNumber"), seasonNumber.ToString(CultureInfo.InvariantCulture));
                }

                var seasonPath = Path.Combine(series.Path, seasonDirName);

                // Create season directory if it doesn't exist
                try
                {
                    await Task.Run(() => Directory.CreateDirectory(seasonPath), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create season directory: {SeasonPath}", seasonPath);
                    continue;
                }

                // Create stub files for missing episodes
                foreach (var episodeRecord in seasonEpisodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!episodeRecord.Number.HasValue)
                    {
                        continue;
                    }

                    var episodeNumber = episodeRecord.Number.Value;
                    var episodeName = episodeRecord.Name ?? "Episode";
                    var sanitizedEpisodeName = SanitizeFileName(episodeName);

                    // Determine file paths for both .mkv and .mp4
                    var baseFileName = $"S{seasonNumber:D2}E{episodeNumber:D2} - {sanitizedEpisodeName}";
                    var mkvPath = Path.Combine(seasonPath, $"{baseFileName}.mkv");
                    var mp4Path = Path.Combine(seasonPath, $"{baseFileName}.mp4");

                    // Check if .mp4 placeholder stub exists but is not in Placeholder_Stubs_List (orphaned)
                    var placeholderStubsForSeries = _placeholderTracker?.GetPlaceholderStubsForSeries(tvdbSeriesId) ?? new List<PlaceholderStubEntry>();
                    var isInPlaceholderTracker = placeholderStubsForSeries.Any(p => p.SeasonNumber == seasonNumber && p.EpisodeNumber == episodeNumber);
                    var orphanedMp4Exists = File.Exists(mp4Path) && !isInPlaceholderTracker && !existingEpisodeFiles.Contains(mp4Path);

                    if (orphanedMp4Exists)
                    {
                        // This logic handles orphaned .mp4 stubs not in Placeholder_Stubs_List
                        if (hasRuntime)
                        {
                            // If hasRuntime is true:
                            // - Check if .mkv already exists (skip if valid - same validation as generated files)
                            if (File.Exists(mkvPath))
                            {
                                var mkvFileInfo = new FileInfo(mkvPath);
                                const long minimumValidSize = 20 * 1024; // 20KB minimum for valid stub file
                                if (mkvFileInfo.Length >= minimumValidSize)
                                {
                                    // Valid .mkv exists, delete orphaned .mp4 and skip
                                    try
                                    {
                                        File.Delete(mp4Path);
                                        _logger.LogDebug("Deleted orphaned placeholder stub (valid .mkv exists): {FilePath}", mp4Path);
                                    }
                                    catch (Exception deleteEx)
                                    {
                                        _logger.LogWarning(deleteEx, "Failed to delete orphaned placeholder stub: {FilePath}", mp4Path);
                                    }

                                    continue;
                                }
                                else
                                {
                                    // Invalid .mkv exists (too small), delete it and restore placeholder
                                    try
                                    {
                                        File.Delete(mkvPath);
                                        _logger.LogDebug("Deleted invalid .mkv stub (too small): {FilePath}", mkvPath);
                                    }
                                    catch (Exception deleteEx)
                                    {
                                        _logger.LogWarning(deleteEx, "Failed to delete invalid .mkv stub: {FilePath}", mkvPath);
                                    }

                                    // Place placeholder .mp4 stub using 30-minute stub file
                                    var placeholderStubPath = FindClosestStubFile(30);
                                    if (placeholderStubPath != null)
                                    {
                                        try
                                        {
                                            await Task.Run(() => File.Copy(placeholderStubPath, mp4Path, overwrite: true), cancellationToken).ConfigureAwait(false);
                                            _logger.LogDebug("Restored placeholder stub file (30min) after invalid .mkv: {FilePath}", mp4Path);
                                        }
                                        catch (Exception copyEx)
                                        {
                                            _logger.LogWarning(copyEx, "Failed to restore placeholder stub file: {FilePath}", mp4Path);
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Cannot restore placeholder stub: 30-minute stub file not found");
                                    }

                                    // Add to Placeholder_Stubs_List to be fixed in future runs
                                    _placeholderTracker?.AddPlaceholderStub(tvdbSeriesId, seasonNumber, episodeNumber, mp4Path);

                                    // End check on this file this run - don't change from generated stub to generated stub in same run
                                    continue;
                                }
                            }

                            // Delete the .mp4 placeholder stub
                            try
                            {
                                File.Delete(mp4Path);
                                _logger.LogDebug("Deleted orphaned placeholder stub: {FilePath}", mp4Path);
                            }
                            catch (Exception deleteEx)
                            {
                                _logger.LogWarning(deleteEx, "Failed to delete orphaned placeholder stub: {FilePath}", mp4Path);
                            }

                            // Create the new .mkv file with correct runtime
                            // Skip if we already know about this file from existing episodes
                            if (!existingEpisodeFiles.Contains(mkvPath))
                            {
                                // Find the closest stub file and copy it
                                var stubFilePath = FindClosestStubFile(runtimeMinutes!.Value);
                                if (stubFilePath != null)
                                {
                                    try
                                    {
                                        await Task.Run(() => File.Copy(stubFilePath, mkvPath, overwrite: true), cancellationToken).ConfigureAwait(false);
                                        _logger.LogInformation("Copied stub file for episode '{0}': {1} -> {2}", episodeRecord.Name ?? "Unknown", Path.GetFileName(stubFilePath), mkvPath);
                                    }
                                    catch (Exception copyEx)
                                    {
                                        _logger.LogError(copyEx, "Failed to copy stub file for episode '{0}': {1} -> {2}", episodeRecord.Name ?? "Unknown", stubFilePath, mkvPath);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("No suitable stub file found for episode '{0}' with runtime {1} minutes", episodeRecord.Name ?? "Unknown", runtimeMinutes);
                                }
                            }
                        }
                        else
                        {
                            // If hasRuntime is false:
                            // - Delete any orphaned .mkv stub (shouldn't exist, but cleans up if present)
                            if (File.Exists(mkvPath))
                            {
                                try
                                {
                                    File.Delete(mkvPath);
                                    _logger.LogDebug("Deleted orphaned .mkv stub (no runtime available): {FilePath}", mkvPath);
                                }
                                catch (Exception deleteEx)
                                {
                                    _logger.LogWarning(deleteEx, "Failed to delete orphaned .mkv stub: {FilePath}", mkvPath);
                                }
                            }

                            // Add to Placeholder_Stubs_List so it gets fixed in future runs
                            // Ensure the .mp4 placeholder exists (copy 30-minute stub if needed)
                            var mp4FileInfo = new FileInfo(mp4Path);
                            if (!File.Exists(mp4Path) || mp4FileInfo.Length <= 1024)
                            {
                                var placeholderStubPath = FindClosestStubFile(30);
                                if (placeholderStubPath != null)
                                {
                                    try
                                    {
                                        await Task.Run(() => File.Copy(placeholderStubPath, mp4Path, overwrite: true), cancellationToken).ConfigureAwait(false);
                                        _logger.LogDebug("Copied/updated placeholder stub file (30min): {FilePath}", mp4Path);
                                    }
                                    catch (Exception copyEx)
                                    {
                                        _logger.LogWarning(copyEx, "Failed to copy placeholder stub file: {FilePath}", mp4Path);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("Cannot create placeholder stub: 30-minute stub file not found");
                                }
                            }

                            // Add to tracker for future updates
                            _placeholderTracker?.AddPlaceholderStub(tvdbSeriesId, seasonNumber, episodeNumber, mp4Path);
                        }
                    }
                    else
                    {
                        // Normal flow: no orphaned .mp4 exists
                        if (hasRuntime)
                        {
                            // Copy appropriate stub file based on runtime
                            // Check if .mkv file already exists and is a real file (> 1KB)
                            if (File.Exists(mkvPath))
                            {
                                var fileInfo = new FileInfo(mkvPath);
                                if (fileInfo.Length > 1024)
                                {
                                    continue; // Real file exists, skip
                                }
                            }

                            // Skip if we already know about this file from existing episodes
                            if (existingEpisodeFiles.Contains(mkvPath))
                            {
                                continue;
                            }

                            // Find the closest stub file and copy it
                            var stubFilePath = FindClosestStubFile(runtimeMinutes!.Value);
                            if (stubFilePath != null)
                            {
                                try
                                {
                                    await Task.Run(() => File.Copy(stubFilePath, mkvPath, overwrite: true), cancellationToken).ConfigureAwait(false);
                                    _logger.LogInformation("Copied stub file for episode '{0}': {1} -> {2}", episodeRecord.Name ?? "Unknown", Path.GetFileName(stubFilePath), mkvPath);
                                }
                                catch (Exception copyEx)
                                {
                                    _logger.LogError(copyEx, "Failed to copy stub file for episode '{0}': {1} -> {2}", episodeRecord.Name ?? "Unknown", stubFilePath, mkvPath);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("No suitable stub file found for episode '{0}' with runtime {1} minutes", episodeRecord.Name ?? "Unknown", runtimeMinutes);
                            }
                        }
                        else
                        {
                            // Copy placeholder .mp4 file
                            // Check if .mp4 file already exists and is a real file (> 1KB)
                            if (File.Exists(mp4Path))
                            {
                                var fileInfo = new FileInfo(mp4Path);
                                if (fileInfo.Length > 1024)
                                {
                                    continue; // Real file exists, skip
                                }
                            }

                            // Skip if we already know about this file from existing episodes
                            if (existingEpisodeFiles.Contains(mp4Path))
                            {
                                continue;
                            }

                            // Copy placeholder stub file (30-minute stub)
                            var placeholderStubPath = FindClosestStubFile(30);
                            if (placeholderStubPath != null)
                            {
                                try
                                {
                                    await Task.Run(() => File.Copy(placeholderStubPath, mp4Path, overwrite: true), cancellationToken).ConfigureAwait(false);
                                    _logger.LogDebug("Copied placeholder stub file (30min) to: {FilePath}", mp4Path);

                                    // Track as placeholder for future update
                                    _placeholderTracker?.AddPlaceholderStub(tvdbSeriesId, seasonNumber, episodeNumber, mp4Path);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to copy placeholder stub file to: {FilePath}", mp4Path);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Cannot create placeholder stub: 30-minute stub file not found for episode {SeasonNumber}x{EpisodeNumber}", seasonNumber, episodeNumber);
                            }
                        }
                    }
                }
            }

            _logger.LogDebug("Finished creating stub files for series {SeriesName}", series.Name);
        }

        /// <summary>
        /// Gets the expected stub file path for an episode without creating it.
        /// </summary>
        /// <param name="episodeRecord">The episode record from TVDB.</param>
        /// <param name="season">The season containing the episode.</param>
        /// <returns>The expected file path, or null if it cannot be determined.</returns>
        private string? GetStubFilePath(EpisodeBaseRecord episodeRecord, Season season)
        {
            var series = season.Series;
            if (series == null || string.IsNullOrEmpty(series.Path) || !episodeRecord.Number.HasValue || !episodeRecord.SeasonNumber.HasValue)
            {
                return null;
            }

            var seasonNumber = episodeRecord.SeasonNumber.Value;
            var episodeNumber = episodeRecord.Number.Value;

            // Create season directory name
            string seasonDirName;
            if (seasonNumber == 0)
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(series);
                seasonDirName = libraryOptions.SeasonZeroDisplayName ?? "Specials";
            }
            else
            {
                seasonDirName = string.Format(CultureInfo.InvariantCulture, _localization.GetLocalizedString("NameSeasonNumber"), seasonNumber.ToString(CultureInfo.InvariantCulture));
            }

            var episodeName = episodeRecord.Name ?? "Episode";
            var sanitizedEpisodeName = SanitizeFileName(episodeName);
            // Note: This method returns the expected path, but actual extension depends on whether it's a placeholder (.mp4) or updated stub (.mkv)
            // For compatibility, we return .mp4 as the default, but the actual file may be .mkv if runtime data is available
            var fileName = $"S{seasonNumber:D2}E{episodeNumber:D2} - {sanitizedEpisodeName}.mp4";
            var filePath = Path.Combine(series.Path, seasonDirName, fileName);

            return filePath;
        }

        /// <summary>
        /// Sanitizes a file name by removing invalid characters.
        /// </summary>
        /// <param name="fileName">The file name to sanitize.</param>
        /// <returns>The sanitized file name.</returns>
        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "Episode";
            }

            // Remove invalid file system characters
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new StringBuilder(fileName.Length);

            foreach (var c in fileName)
            {
                if (!invalidChars.Contains(c))
                {
                    sanitized.Append(c);
                }
                else
                {
                    sanitized.Append('_');
                }
            }

            // Remove any trailing periods or spaces (Windows limitation)
            var result = sanitized.ToString().TrimEnd('.', ' ');

            // Ensure the name is not empty after sanitization
            if (string.IsNullOrWhiteSpace(result))
            {
                return "Episode";
            }

            return result;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted += OnProviderManagerRefreshComplete;
            _libraryManager.ItemUpdated += OnLibraryManagerItemUpdated;
            _libraryManager.ItemRemoved += OnLibraryManagerItemRemoved;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _providerManager.RefreshCompleted -= OnProviderManagerRefreshComplete;
            _libraryManager.ItemUpdated -= OnLibraryManagerItemUpdated;
            _libraryManager.ItemRemoved -= OnLibraryManagerItemRemoved;
            return Task.CompletedTask;
        }
    }
}
