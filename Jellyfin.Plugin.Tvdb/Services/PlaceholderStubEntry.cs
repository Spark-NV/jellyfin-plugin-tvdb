namespace Jellyfin.Plugin.Tvdb.Services
{
    /// <summary>
    /// Represents a placeholder stub entry in the tracker.
    /// </summary>
    public sealed class PlaceholderStubEntry
    {
        /// <summary>
        /// Gets or sets the TVDB series ID.
        /// </summary>
        public int TvdbSeriesId { get; set; }

        /// <summary>
        /// Gets or sets the season number.
        /// </summary>
        public int SeasonNumber { get; set; }

        /// <summary>
        /// Gets or sets the episode number.
        /// </summary>
        public int EpisodeNumber { get; set; }

        /// <summary>
        /// Gets or sets the file path to the stub file.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;
    }
}
