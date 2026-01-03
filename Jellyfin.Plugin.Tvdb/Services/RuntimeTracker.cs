using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tvdb.Services
{
    /// <summary>
    /// Tracks AverageRuntime values for series by TVDB series ID.
    /// Runtime entries are never automatically removed - once saved, they persist for future use.
    /// </summary>
    public class RuntimeTracker
    {
        private readonly string _trackerFilePath;
        private readonly object _lock = new object();
        private readonly ILogger<RuntimeTracker>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeTracker"/> class.
        /// </summary>
        /// <param name="pluginDirectory">The plugin directory where the tracker file will be stored.</param>
        /// <param name="logger">Optional logger instance.</param>
        public RuntimeTracker(string pluginDirectory, ILogger<RuntimeTracker>? logger = null)
        {
            _trackerFilePath = Path.Combine(pluginDirectory, "Series_Runtime_Index.txt");
            _logger = logger;
        }

        /// <summary>
        /// Adds or updates the AverageRuntime for a series.
        /// </summary>
        /// <param name="tvdbSeriesId">The TVDB series ID.</param>
        /// <param name="averageRuntimeMinutes">The average runtime in minutes.</param>
        public void SetSeriesRuntime(int tvdbSeriesId, int averageRuntimeMinutes)
        {
            lock (_lock)
            {
                try
                {
                    var entries = ReadAllEntries();

                    // Remove existing entry if present
                    entries.RemoveAll(e => e.TvdbSeriesId == tvdbSeriesId);

                    // Add new entry
                    entries.Add(new SeriesRuntimeEntry
                    {
                        TvdbSeriesId = tvdbSeriesId,
                        AverageRuntimeMinutes = averageRuntimeMinutes
                    });

                    WriteAllEntries(entries);
                    _logger?.LogDebug("Saved AverageRuntime {Runtime} minutes for series TVDB ID {SeriesId}", averageRuntimeMinutes, tvdbSeriesId);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to save runtime for series TVDB ID {SeriesId}", tvdbSeriesId);
                }
            }
        }

        /// <summary>
        /// Gets the AverageRuntime for a series, if available.
        /// </summary>
        /// <param name="tvdbSeriesId">The TVDB series ID.</param>
        /// <returns>The average runtime in minutes, or null if not found.</returns>
        public int? GetSeriesRuntime(int tvdbSeriesId)
        {
            lock (_lock)
            {
                try
                {
                    var entries = ReadAllEntries();
                    var entry = entries.FirstOrDefault(e => e.TvdbSeriesId == tvdbSeriesId);
                    return entry?.AverageRuntimeMinutes;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to read runtime for series TVDB ID {SeriesId}", tvdbSeriesId);
                    return null;
                }
            }
        }

        private List<SeriesRuntimeEntry> ReadAllEntries()
        {
            var entries = new List<SeriesRuntimeEntry>();

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
                    if (parts.Length == 2)
                    {
                        if (int.TryParse(parts[0], out var tvdbSeriesId) &&
                            int.TryParse(parts[1], out var runtimeMinutes))
                        {
                            entries.Add(new SeriesRuntimeEntry
                            {
                                TvdbSeriesId = tvdbSeriesId,
                                AverageRuntimeMinutes = runtimeMinutes
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to read runtime tracker file");
            }

            return entries;
        }

        private void WriteAllEntries(List<SeriesRuntimeEntry> entries)
        {
            try
            {
                var lines = entries.Select(e => $"{e.TvdbSeriesId}|{e.AverageRuntimeMinutes}");
                File.WriteAllLines(_trackerFilePath, lines);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write runtime tracker file");
            }
        }

        /// <summary>
        /// Represents a series runtime entry in the tracker.
        /// </summary>
        private sealed class SeriesRuntimeEntry
        {
            /// <summary>
            /// Gets or sets the TVDB series ID.
            /// </summary>
            public int TvdbSeriesId { get; set; }

            /// <summary>
            /// Gets or sets the average runtime in minutes.
            /// </summary>
            public int AverageRuntimeMinutes { get; set; }
        }
    }
}
