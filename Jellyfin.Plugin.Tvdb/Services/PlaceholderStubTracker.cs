using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tvdb.Services
{
    /// <summary>
    /// Tracks placeholder stubs that need to be replaced when runtime becomes available.
    /// </summary>
    public class PlaceholderStubTracker
    {
        private readonly string _trackerFilePath;
        private readonly object _lock = new object();
        private readonly ILogger<PlaceholderStubTracker>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaceholderStubTracker"/> class.
        /// </summary>
        /// <param name="pluginDirectory">The plugin directory where the tracker file will be stored.</param>
        /// <param name="logger">Optional logger instance.</param>
        public PlaceholderStubTracker(string pluginDirectory, ILogger<PlaceholderStubTracker>? logger = null)
        {
            _trackerFilePath = Path.Combine(pluginDirectory, "Placeholder_Stubs_List.txt");
            _logger = logger;
        }

        /// <summary>
        /// Adds a placeholder stub to the tracking list.
        /// </summary>
        /// <param name="tvdbSeriesId">The TVDB series ID.</param>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="episodeNumber">The episode number.</param>
        /// <param name="filePath">The full path to the stub file.</param>
        public void AddPlaceholderStub(int tvdbSeriesId, int seasonNumber, int episodeNumber, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    var entries = ReadAllEntries();

                    // Check if already exists (shouldn't happen, but be safe)
                    if (!entries.Any(e => e.TvdbSeriesId == tvdbSeriesId &&
                                          e.SeasonNumber == seasonNumber &&
                                          e.EpisodeNumber == episodeNumber))
                    {
                        entries.Add(new PlaceholderStubEntry
                        {
                            TvdbSeriesId = tvdbSeriesId,
                            SeasonNumber = seasonNumber,
                            EpisodeNumber = episodeNumber,
                            FilePath = filePath
                        });

                        WriteAllEntries(entries);
                        _logger?.LogDebug("Added placeholder stub for series {SeriesId} S{Season}E{Episode}", tvdbSeriesId, seasonNumber, episodeNumber);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to add placeholder stub to tracker");
                }
            }
        }

        /// <summary>
        /// Gets all tracked placeholder stubs for a series.
        /// </summary>
        /// <param name="tvdbSeriesId">The TVDB series ID.</param>
        /// <returns>List of placeholder stub entries for the series.</returns>
        public IReadOnlyList<PlaceholderStubEntry> GetPlaceholderStubsForSeries(int tvdbSeriesId)
        {
            lock (_lock)
            {
                try
                {
                    var entries = ReadAllEntries();
                    return entries.Where(e => e.TvdbSeriesId == tvdbSeriesId).ToList();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to read placeholder stubs for series {SeriesId}", tvdbSeriesId);
                    return new List<PlaceholderStubEntry>();
                }
            }
        }

        /// <summary>
        /// Gets all tracked placeholder stubs.
        /// </summary>
        /// <returns>List of placeholder stub entries.</returns>
        public IReadOnlyList<PlaceholderStubEntry> GetAllPlaceholderStubs()
        {
            lock (_lock)
            {
                return ReadAllEntries();
            }
        }

        /// <summary>
        /// Removes a placeholder stub from the tracking list.
        /// </summary>
        /// <param name="tvdbSeriesId">The TVDB series ID.</param>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="episodeNumber">The episode number.</param>
        public void RemovePlaceholderStub(int tvdbSeriesId, int seasonNumber, int episodeNumber)
        {
            lock (_lock)
            {
                try
                {
                    var entries = ReadAllEntries();
                    entries.RemoveAll(e => e.TvdbSeriesId == tvdbSeriesId &&
                                           e.SeasonNumber == seasonNumber &&
                                           e.EpisodeNumber == episodeNumber);
                    WriteAllEntries(entries);
                    _logger?.LogDebug("Removed placeholder stub for series {SeriesId} S{Season}E{Episode}", tvdbSeriesId, seasonNumber, episodeNumber);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to remove placeholder stub from tracker");
                }
            }
        }

        private List<PlaceholderStubEntry> ReadAllEntries()
        {
            var entries = new List<PlaceholderStubEntry>();

            if (!File.Exists(_trackerFilePath))
            {
                return entries;
            }

            try
            {
                var lines = File.ReadAllLines(_trackerFilePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var parts = line.Split('|');
                    if (parts.Length == 4)
                    {
                        if (int.TryParse(parts[0], out var tvdbSeriesId) &&
                            int.TryParse(parts[1], out var seasonNumber) &&
                            int.TryParse(parts[2], out var episodeNumber))
                        {
                            entries.Add(new PlaceholderStubEntry
                            {
                                TvdbSeriesId = tvdbSeriesId,
                                SeasonNumber = seasonNumber,
                                EpisodeNumber = episodeNumber,
                                FilePath = parts[3]
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to read placeholder stub tracker file");
            }

            return entries;
        }

        private void WriteAllEntries(List<PlaceholderStubEntry> entries)
        {
            try
            {
                var lines = entries.Select(e => $"{e.TvdbSeriesId}|{e.SeasonNumber}|{e.EpisodeNumber}|{e.FilePath}");
                File.WriteAllLines(_trackerFilePath, lines);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write placeholder stub tracker file");
            }
        }
    }
}
