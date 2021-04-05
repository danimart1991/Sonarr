using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Tv;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TMDbLib.Client;
using TMDbLib.Objects.TvShows;

namespace NzbDrone.Core.Extras.Metadata.Consumers.XbmcTmdb
{
    public class XbmcTmdbMetadata : MetadataBase<XbmcTmdbMetadataSettings>
    {
        private readonly IConfigService _configService;
        private readonly IDetectXbmcTmdbNfo _detectNfo;
        private readonly Logger _logger;
        private readonly IMapCoversToLocal _mediaCoverService;

        private static readonly Regex SeriesImagesRegex = new Regex(@"^(?<type>poster|banner|fanart|clearart|landscape|clearlogo)\.(?:png|jpg)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SeasonImagesRegex = new Regex(@"^season(?<season>\d{2,}|-all|-specials)-(?<type>poster|banner|fanart|landscape)\.(?:png|jpg)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex EpisodeImageRegex = new Regex(@"-thumb\.(?:png|jpg)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string BaseImageUrl = "https://image.tmdb.org/t/p";

        private static TMDbClient TmdbClient;

        public XbmcTmdbMetadata(
            IConfigService configService,
            IDetectXbmcTmdbNfo detectNfo,
            IMapCoversToLocal mediaCoverService,
            Logger logger)
        {
            _configService = configService;
            _detectNfo = detectNfo;
            _logger = logger;
            _mediaCoverService = mediaCoverService;
        }

        public override string Name => "Kodi (XBMC) / Emby from TMDB";

        public override string GetFilenameAfterMove(Series series, EpisodeFile episodeFile, MetadataFile metadataFile)
        {
            var episodeFilePath = Path.Combine(series.Path, episodeFile.RelativePath);

            if (metadataFile.Type == MetadataType.EpisodeImage)
            {
                return GetEpisodeImageFilename(episodeFilePath);
            }

            if (metadataFile.Type == MetadataType.EpisodeMetadata)
            {
                return GetEpisodeMetadataFilename(episodeFilePath);
            }

            _logger.Debug("Unknown episode file metadata: {0}", metadataFile.RelativePath);
            return Path.Combine(series.Path, metadataFile.RelativePath);
        }

        public override MetadataFile FindMetadataFile(Series series, string path)
        {
            var filename = Path.GetFileName(path);

            if (filename == null) return null;

            var metadata = new MetadataFile
            {
                SeriesId = series.Id,
                Consumer = GetType().Name,
                RelativePath = series.Path.GetRelativePath(path)
            };

            if (SeriesImagesRegex.IsMatch(filename))
            {
                metadata.Type = MetadataType.SeriesImage;
                return metadata;
            }

            var seasonMatch = SeasonImagesRegex.Match(filename);

            if (seasonMatch.Success)
            {
                metadata.Type = MetadataType.SeasonImage;

                var seasonNumberMatch = seasonMatch.Groups["season"].Value;

                if (seasonNumberMatch.Contains("specials"))
                {
                    metadata.SeasonNumber = 0;
                }

                else if (int.TryParse(seasonNumberMatch, out int seasonNumber))
                {
                    metadata.SeasonNumber = seasonNumber;
                }

                else
                {
                    return null;
                }

                return metadata;
            }

            if (EpisodeImageRegex.IsMatch(filename))
            {
                metadata.Type = MetadataType.EpisodeImage;
                return metadata;
            }

            if (filename.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase))
            {
                metadata.Type = MetadataType.SeriesMetadata;
                return metadata;
            }

            var parseResult = Parser.Parser.ParseTitle(filename);

            if (parseResult != null &&
                !parseResult.FullSeason &&
                Path.GetExtension(filename).Equals(".nfo", StringComparison.OrdinalIgnoreCase) &&
                _detectNfo.IsXbmcTmdbNfoFile(path))
            {
                metadata.Type = MetadataType.EpisodeMetadata;
                return metadata;
            }

            return null;
        }

        public override MetadataFileResult SeriesMetadata(Series series)
        {
            if (!Settings.SeriesMetadata)
            {
                return null;
            }

            _logger.Debug("Generating tvshow.nfo for: {0}", series.Title);

            if (_configService.TmdbApiKey.IsNullOrWhiteSpace())
            {
                _logger.Warn("TMDb API Key not set. Please set it in Sonnar General Settings section.");
                return null;
            }

            if (TmdbClient == null)
            {
                TmdbClient = new TMDbClient(_configService.TmdbApiKey);
            }

            var tvShowMethods =
                TvShowMethods.Videos |
                TvShowMethods.Credits;

            // TvRageId in Database is TMDbId
            var tmdbTvShow = TmdbClient
                .GetTvShowAsync(series.TvRageId, extraMethods: tvShowMethods, language: "es")
                .Result;

            var sb = new StringBuilder();
            var xws = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = false
            };

            using (var xw = XmlWriter.Create(sb, xws))
            {
                if (tmdbTvShow == null)
                {
                    _logger.Error("Unable to get series from TMDB with ID: {0}", series.TvRageId);
                    return null;
                }

                var tvShow = new XElement("tvshow");

                tvShow.Add(new XElement("title", series.Title));

                tvShow.Add(new XElement("showtitle", series.Title));
                tvShow.Add(new XElement("sorttitle", series.SortTitle));

                if (tmdbTvShow.OriginalName.IsNotNullOrWhiteSpace())
                {
                    tvShow.Add(new XElement("originaltitle", tmdbTvShow.OriginalName));
                }

                if (series.Ratings != null && series.Ratings.Votes > 0)
                {
                    var setRating = new XElement("ratings");
                    var setRatethemoviedb = new XElement("rating", new XAttribute("name", "themoviedb"), new XAttribute("max", "10"), new XAttribute("default", "true"));
                    setRatethemoviedb.Add(new XElement("value", series.Ratings.Value));
                    setRatethemoviedb.Add(new XElement("votes", series.Ratings.Votes));
                    setRating.Add(setRatethemoviedb);
                    tvShow.Add(setRating);
                }

                if (series.Ratings != null && series.Ratings.Votes > 0)
                {
                    tvShow.Add(new XElement("rating", series.Ratings.Value));
                }

                foreach (var season in tmdbTvShow.Seasons)
                {
                    tvShow.Add(new XElement("namedseason", new XAttribute("number", season.SeasonNumber), season.Name));
                }

                tvShow.Add(new XElement("plot", series.Overview));

                // TODO: This in TMDBLib updates to 1.7
                //if (tmdbTvShow.TagLine.IsNotNullOrWhiteSpace())
                //{
                //    tvShow.Add(new XElement("tagline", tmdbTvShow.TagLine));
                //}

                if (series.Images != null && series.Images.Any())
                {
                    foreach (var image in series.Images.Where(image => image.CoverType != MediaCoverTypes.Fanart))
                    {
                        tvShow.Add(MapUrlThumb(image.Url, image.CoverType));
                    }

                    if (series.Images.Any(image => image.CoverType == MediaCoverTypes.Fanart))
                    {
                        var fanart = new XElement("fanart");
                        foreach (var image in series.Images.Where(image => image.CoverType == MediaCoverTypes.Fanart))
                        {
                            fanart.Add(MapUrlThumb(image.Url, MediaCoverTypes.Fanart));
                        }

                        tvShow.Add(fanart);
                    }
                }

                if (series.Seasons != null && series.Seasons.Any())
                {
                    foreach (var season in series.Seasons)
                    {
                        if (season.Images != null && season.Images.Any())
                        {
                            foreach (var image in season.Images)
                            {
                                tvShow.Add(MapUrlThumb(image.Url, image.CoverType, season.SeasonNumber));
                            }
                        }
                    }
                }

                tvShow.Add(new XElement("mpaa", series.Certification));

                if (tmdbTvShow.EpisodeRunTime != null && tmdbTvShow.EpisodeRunTime.Any())
                {
                    tvShow.Add(new XElement("runtime", tmdbTvShow.EpisodeRunTime.Average()));
                }

                var episodeGuideUrl = string.Format("http://www.thetvdb.com/api/1D62F2F90030C444/series/{0}/all/es.zip", series.TvdbId);
                tvShow.Add(new XElement("episodeguide", new XElement("url", episodeGuideUrl)));
                tvShow.Add(new XElement("episodeguideurl", episodeGuideUrl));

                if (series.TvdbId > 0)
                {
                    var uniqueIdTVDB = new XElement("uniqueid", series.TvdbId);
                    uniqueIdTVDB.SetAttributeValue("type", "tvdb");
                    uniqueIdTVDB.SetAttributeValue("default", false);
                    tvShow.Add(uniqueIdTVDB);

                    tvShow.Add(new XElement("tvdbid", series.TvdbId));
                }

                // TvRageId in Database is TMDbId
                if (series.TvRageId > 0)
                {
                    var uniqueIdTMDB = new XElement("uniqueid", series.TvRageId);
                    uniqueIdTMDB.SetAttributeValue("type", "tmdb");
                    uniqueIdTMDB.SetAttributeValue("default", true);
                    tvShow.Add(uniqueIdTMDB);

                    tvShow.Add(new XElement("tmdbid", series.TvRageId));
                }

                if (series.ImdbId.IsNotNullOrWhiteSpace())
                {
                    var uniqueIdIMDB = new XElement("uniqueid", series.ImdbId);
                    uniqueIdIMDB.SetAttributeValue("type", "imdb");
                    uniqueIdIMDB.SetAttributeValue("default", false);
                    tvShow.Add(uniqueIdIMDB);

                    tvShow.Add(new XElement("imdb_id", series.ImdbId));
                }

                foreach (var genre in series.Genres)
                {
                    tvShow.Add(new XElement("genre", genre));
                }

                if (series.FirstAired.HasValue)
                {
                    tvShow.Add(new XElement("premiered", series.FirstAired.Value.ToString("yyyy-MM-dd")));
                }

                if (tmdbTvShow.FirstAirDate.HasValue)
                {
                    tvShow.Add(new XElement("releasedate", tmdbTvShow.FirstAirDate.Value.ToString("yyyy-MM-dd")));
                }

                if (series.Status == SeriesStatusType.Continuing)
                {
                    tvShow.Add(new XElement("status", "Continuing"));
                }
                else if (series.Status == SeriesStatusType.Ended)
                {
                    tvShow.Add(new XElement("status", "Ended"));

                    if (tmdbTvShow.LastAirDate.HasValue)
                    {
                        tvShow.Add(new XElement("enddate", tmdbTvShow.LastAirDate.Value.ToString("yyyy-MM-dd")));
                    }
                }

                tvShow.Add(new XElement("studio", series.Network));

                if (tmdbTvShow.Videos?.Results != null && tmdbTvShow.Videos.Results.Any(video => video.Type == "Trailer"))
                {
                    var trailer = tmdbTvShow.Videos.Results.FirstOrDefault(video => video.Type == "Trailer");
                    string trailerUrl = null;

                    if (trailer.Site == "YouTube")
                    {
                        trailerUrl = $"https://www.youtube.com/watch?v={trailer.Key}";
                    }
                    else if (trailer.Site == "Vimeo")
                    {
                        trailerUrl = $"https://vimeo.com/{trailer.Key}";
                    }

                    if (!string.IsNullOrEmpty(trailerUrl))
                    {
                        tvShow.Add(new XElement("trailer", trailerUrl));
                    }
                }

                foreach (var cast in tmdbTvShow.Credits.Cast.OrderBy(cast => cast.Order))
                {
                    var actorElement = new XElement("actor");

                    if (cast.Name.IsNotNullOrWhiteSpace() && cast.Character.IsNotNullOrWhiteSpace())
                    {
                        actorElement.Add(new XElement("name", cast.Name));
                        actorElement.Add(new XElement("role", cast.Character));
                        actorElement.Add(new XElement("type", "Actor"));
                        actorElement.Add(new XElement("order", cast.Order));

                        if (cast.ProfilePath.IsNotNullOrWhiteSpace())
                        {
                            actorElement.Add(MapRelativeUrlThumb(cast.ProfilePath));
                        }

                        tvShow.Add(actorElement);
                    }
                }

                foreach (var crew in tmdbTvShow.Credits.Crew)
                {
                    if (crew.Name.IsNotNullOrWhiteSpace() && crew.Job.IsNotNullOrWhiteSpace())
                    {
                        var actorElement = new XElement("actor");

                        actorElement.Add(new XElement("name", crew.Name));
                        actorElement.Add(new XElement("role", crew.Job));
                        actorElement.Add(new XElement("type", crew.Department));

                        if (crew.ProfilePath.IsNotNullOrWhiteSpace())
                        {
                            actorElement.Add(MapRelativeUrlThumb(crew.ProfilePath));
                        }

                        tvShow.Add(actorElement);
                    }
                }

                foreach (var writer in tmdbTvShow.Credits.Crew.Where(crew => (crew.Job == "Screenplay" || crew.Job == "Story" || crew.Job == "Novel" || crew.Job == "Writer") && crew.Name.IsNotNullOrWhiteSpace()))
                {
                    tvShow.Add(new XElement("credits", writer.Name));
                }

                foreach (var director in tmdbTvShow.Credits.Crew.Where(crew => crew.Job == "Director" && crew.Name.IsNotNullOrWhiteSpace()))
                {
                    tvShow.Add(new XElement("director", director.Name));
                }

                var doc = new XDocument(tvShow);
                doc.Save(xw);

                _logger.Debug("Saving tvshow.nfo for {0}", series.Title);

                return new MetadataFileResult("tvshow.nfo", doc.ToString());
            }
        }

        public override MetadataFileResult EpisodeMetadata(Series series, EpisodeFile episodeFile)
        {
            if (!Settings.EpisodeMetadata)
            {
                return null;
            }

            _logger.Debug("Generating Episode Metadata for: {0}", Path.Combine(series.Path, episodeFile.RelativePath));

            if (_configService.TmdbApiKey.IsNullOrWhiteSpace())
            {
                _logger.Warn("TMDb API Key not set. Please set it in Sonnar General Settings section.");
                return null;
            }

            if (TmdbClient == null)
            {
                TmdbClient = new TMDbClient(_configService.TmdbApiKey);
            }

            var tvEpisodeMethods =
                    TvEpisodeMethods.Credits |
                    TvEpisodeMethods.ExternalIds;

            var xmlResult = string.Empty;
            foreach (var episode in episodeFile.Episodes.Value)
            {
                var sb = new StringBuilder();
                var xws = new XmlWriterSettings
                {
                    OmitXmlDeclaration = true,
                    Indent = false
                };

                // TvRageId in Database is TMDbId
                var tvEpisode = TmdbClient
                    .GetTvEpisodeAsync(series.TvRageId, episode.SeasonNumber, episode.EpisodeNumber, extraMethods: tvEpisodeMethods, language: "es")
                    .Result;

                if (tvEpisode == null)
                {
                    _logger.Error("Unable to get episode S{0}E{1} from TMDB with Series ID: {2}", episode.SeasonNumber, episode.EpisodeNumber, series.TvRageId);
                    continue;
                }

                using (var xw = XmlWriter.Create(sb, xws))
                {
                    var doc = new XDocument();

                    var details = new XElement("episodedetails");
                    details.Add(new XElement("title", episode.Title));

                    details.Add(new XElement("showtitle", series.Title));

                    // TODO: Add when in Database
                    //if (series.OriginalName.IsNotNullOrWhiteSpace())
                    //{
                    //    details.Add(new XElement("originaltitle", series.OriginalName));
                    //}

                    if (episode.Ratings?.Votes != null && episode.Ratings.Votes > 0)
                    {
                        var ratings = new XElement("ratings");
                        var ratingTmdb = new XElement("rating");
                        ratingTmdb.SetAttributeValue("name", "tmdb");
                        ratingTmdb.SetAttributeValue("max", "10");
                        ratingTmdb.SetAttributeValue("default", true);
                        ratingTmdb.Add(new XElement("value", episode.Ratings.Value));
                        ratingTmdb.Add(new XElement("votes", episode.Ratings.Votes));
                        ratings.Add(ratingTmdb);
                        details.Add(ratings);
                    }

                    details.Add(new XElement("season", episode.SeasonNumber));
                    details.Add(new XElement("episode", episode.EpisodeNumber));

                    if (episode.SeasonNumber == 0 && episode.AiredAfterSeasonNumber.HasValue)
                    {
                        details.Add(new XElement("displayafterseason", episode.AiredAfterSeasonNumber));
                    }
                    else if (episode.SeasonNumber == 0 && episode.AiredBeforeSeasonNumber.HasValue)
                    {
                        details.Add(new XElement("displayseason", episode.AiredBeforeSeasonNumber));
                        details.Add(new XElement("displayepisode", episode.AiredBeforeEpisodeNumber ?? -1));
                    }

                    details.Add(new XElement("plot", episode.Overview));

                    if (episodeFile.MediaInfo.RunTime != null)
                    {
                        details.Add(new XElement("runtime", episodeFile.MediaInfo.RunTime.TotalMinutes));
                    }

                    // TODO: This in TMDBLib updates to 1.7
                    //if (episode.TagLine.IsNotNullOrWhiteSpace())
                    //{
                    //    details.Add(new XElement("tagline", episode.TagLine));
                    //}

                    if (episode.Images == null || !episode.Images.Any())
                    {
                        details.Add(new XElement("thumb"));
                    }
                    else
                    {
                        foreach (var image in episode.Images)
                        {
                            details.Add(MapUrlThumb(image.Url, image.CoverType));
                        }
                    }

                    details.Add(new XElement("mpaa", series.Certification));

                    var uniqueTmdbId = new XElement("uniqueid", tvEpisode.Id);
                    uniqueTmdbId.SetAttributeValue("type", "tmdb");
                    uniqueTmdbId.SetAttributeValue("default", true);
                    details.Add(uniqueTmdbId);

                    details.Add(new XElement("tmdbid", tvEpisode.Id));

                    if (!string.IsNullOrEmpty(tvEpisode.ExternalIds.ImdbId))
                    {
                        var uniqueImdbId = new XElement("uniqueid", tvEpisode.ExternalIds.ImdbId);
                        uniqueImdbId.SetAttributeValue("type", "imdb");
                        uniqueImdbId.SetAttributeValue("default", false);
                        details.Add(uniqueImdbId);

                        details.Add(new XElement("imdb_id", tvEpisode.ExternalIds.ImdbId));
                    }

                    if (!string.IsNullOrEmpty(tvEpisode.ExternalIds.TvdbId))
                    {
                        var uniqueTvdbId = new XElement("uniqueid", tvEpisode.ExternalIds.TvdbId);
                        uniqueTvdbId.SetAttributeValue("type", "tvdb");
                        uniqueTvdbId.SetAttributeValue("default", false);
                        details.Add(uniqueTvdbId);

                        details.Add(new XElement("tvdbid", tvEpisode.ExternalIds.TvdbId));
                    }

                    if (!string.IsNullOrEmpty(tvEpisode.ExternalIds.TvrageId))
                    {
                        var uniqueTvrageId = new XElement("uniqueid", tvEpisode.ExternalIds.TvrageId);
                        uniqueTvrageId.SetAttributeValue("type", "tvrage");
                        uniqueTvrageId.SetAttributeValue("default", false);
                        details.Add(uniqueTvrageId);

                        details.Add(new XElement("tvrageid", tvEpisode.ExternalIds.TvrageId));
                    }

                    foreach (var genre in series.Genres)
                    {
                        details.Add(new XElement("genre", genre));
                    }

                    foreach (var writer in tvEpisode.Credits.Crew.Where(crew => (crew.Job == "Screenplay" || crew.Job == "Story" || crew.Job == "Novel" || crew.Job == "Writer") && crew.Name.IsNotNullOrWhiteSpace()))
                    {
                        details.Add(new XElement("credits", writer.Name));
                    }

                    foreach (var director in tvEpisode.Credits.Crew.Where(crew => crew.Job == "Director" && crew.Name.IsNotNullOrWhiteSpace()))
                    {
                        details.Add(new XElement("director", director.Name));
                    }

                    foreach (var cast in tvEpisode.Credits.Cast.Union(tvEpisode.Credits.GuestStars).OrderBy(cast => cast.Order))
                    {
                        if (cast.Name.IsNotNullOrWhiteSpace() && cast.Character.IsNotNullOrWhiteSpace())
                        {
                            var actorElement = new XElement("actor");

                            actorElement.Add(new XElement("name", cast.Name));
                            actorElement.Add(new XElement("role", cast.Character));
                            actorElement.Add(new XElement("order", cast.Order));

                            if (cast.ProfilePath.IsNotNullOrWhiteSpace())
                            {
                                actorElement.Add(MapRelativeUrlThumb(cast.ProfilePath));
                            }

                            details.Add(actorElement);
                        }
                    }

                    foreach (var crew in tvEpisode.Credits.Crew)
                    {
                        if (crew.Name.IsNotNullOrWhiteSpace() && crew.Job.IsNotNullOrWhiteSpace())
                        {
                            var actorElement = new XElement("actor");

                            actorElement.Add(new XElement("name", crew.Name));
                            actorElement.Add(new XElement("role", crew.Job));
                            actorElement.Add(new XElement("type", crew.Department));

                            if (crew.ProfilePath.IsNotNullOrWhiteSpace())
                            {
                                actorElement.Add(MapRelativeUrlThumb(crew.ProfilePath));
                            }

                            details.Add(actorElement);
                        }
                    }

                    details.Add(new XElement("aired", episode.AirDate));
                    details.Add(new XElement("studio", series.Network));

                    if (episodeFile.MediaInfo != null)
                    {
                        var sceneName = episodeFile.GetSceneOrFileName();

                        var fileInfo = new XElement("fileinfo");
                        var streamDetails = new XElement("streamdetails");

                        var video = new XElement("video");
                        video.Add(new XElement("aspect", episodeFile.MediaInfo.Width / episodeFile.MediaInfo.Height));
                        video.Add(new XElement("bitrate", episodeFile.MediaInfo.VideoBitrate));
                        video.Add(new XElement("codec", MediaInfoFormatter.FormatVideoCodec(episodeFile.MediaInfo, sceneName)));
                        video.Add(new XElement("framerate", episodeFile.MediaInfo.VideoFps));
                        video.Add(new XElement("height", episodeFile.MediaInfo.Height));
                        video.Add(new XElement("scantype", episodeFile.MediaInfo.ScanType));
                        video.Add(new XElement("width", episodeFile.MediaInfo.Width));

                        if (episodeFile.MediaInfo.RunTime != null)
                        {
                            video.Add(new XElement("duration", episodeFile.MediaInfo.RunTime.TotalMinutes));
                            video.Add(new XElement("durationinseconds", episodeFile.MediaInfo.RunTime.TotalSeconds));
                        }

                        streamDetails.Add(video);

                        var audio = new XElement("audio");
                        var audioChannelCount = episodeFile.MediaInfo.AudioChannelsStream > 0 ? episodeFile.MediaInfo.AudioChannelsStream : episodeFile.MediaInfo.AudioChannelsContainer;
                        audio.Add(new XElement("bitrate", episodeFile.MediaInfo.AudioBitrate));
                        audio.Add(new XElement("channels", audioChannelCount));
                        audio.Add(new XElement("codec", MediaInfoFormatter.FormatAudioCodec(episodeFile.MediaInfo, sceneName)));
                        audio.Add(new XElement("language", episodeFile.MediaInfo.AudioLanguages));
                        streamDetails.Add(audio);

                        if (episodeFile.MediaInfo.Subtitles.IsNotNullOrWhiteSpace())
                        {
                            var subtitle = new XElement("subtitle");
                            subtitle.Add(new XElement("language", episodeFile.MediaInfo.Subtitles));
                            streamDetails.Add(subtitle);
                        }

                        fileInfo.Add(streamDetails);
                        details.Add(fileInfo);
                    }

                    doc.Add(details);
                    doc.Save(xw);

                    xmlResult += doc.ToString();
                    xmlResult += Environment.NewLine;
                }
            }

            return new MetadataFileResult(GetEpisodeMetadataFilename(episodeFile.RelativePath), xmlResult.Trim(Environment.NewLine.ToCharArray()));
        }

        public override List<ImageFileResult> SeriesImages(Series series)
        {
            if (!Settings.SeriesImages)
            {
                return new List<ImageFileResult>();
            }

            return ProcessSeriesImages(series).ToList();
        }

        public override List<ImageFileResult> SeasonImages(Series series, Season season)
        {
            if (!Settings.SeasonImages)
            {
                return new List<ImageFileResult>();
            }

            return ProcessSeasonImages(series, season).ToList();
        }

        public override List<ImageFileResult> EpisodeImages(Series series, EpisodeFile episodeFile)
        {
            if (!Settings.EpisodeImages)
            {
                return new List<ImageFileResult>();
            }

            try
            {
                var screenshot = episodeFile.Episodes.Value.First().Images.SingleOrDefault(i => i.CoverType == MediaCoverTypes.Screenshot);

                if (screenshot == null)
                {
                    _logger.Debug("Episode screenshot not available");
                    return new List<ImageFileResult>();
                }

                return new List<ImageFileResult>
                   {
                       new ImageFileResult(GetEpisodeImageFilename(episodeFile.RelativePath), screenshot.Url)
                   };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to process episode image for file: {0}", Path.Combine(series.Path, episodeFile.RelativePath));

                return new List<ImageFileResult>();
            }
        }

        private IEnumerable<ImageFileResult> ProcessSeriesImages(Series series)
        {
            foreach (var image in series.Images)
            {
                var source = _mediaCoverService.GetCoverPath(series.Id, image.CoverType);
                var destination = image.CoverType.ToString().ToLowerInvariant() + Path.GetExtension(source);

                yield return new ImageFileResult(destination, source);
            }
        }

        private IEnumerable<ImageFileResult> ProcessSeasonImages(Series series, Season season)
        {
            foreach (var image in season.Images)
            {
                var filename = string.Format("season{0:00}-{1}.jpg", season.SeasonNumber, image.CoverType.ToString().ToLower());

                if (season.SeasonNumber == 0)
                {
                    filename = string.Format("season-specials-{0}.jpg", image.CoverType.ToString().ToLower());
                }

                yield return new ImageFileResult(filename, image.Url);
            }
        }

        private string GetEpisodeMetadataFilename(string episodeFilePath)
        {
            return Path.ChangeExtension(episodeFilePath, "nfo");
        }

        private string GetEpisodeImageFilename(string episodeFilePath)
        {
            return Path.ChangeExtension(episodeFilePath, "").Trim('.') + "-thumb.jpg";
        }

        private XElement MapRelativeUrlThumb(string relativeUrl)
        {
            return new XElement("thumb", BaseImageUrl + "/original" + relativeUrl);
        }

        private XElement MapUrlThumb(string url, MediaCoverTypes mediaCoverType, int? season = null)
        {
            string previewSize = null;

            switch (mediaCoverType)
            {
                case MediaCoverTypes.Headshot:
                case MediaCoverTypes.Poster:
                    previewSize = "/w185/";
                    break;
                case MediaCoverTypes.Banner:
                case MediaCoverTypes.ClearArt:
                case MediaCoverTypes.Fanart:
                case MediaCoverTypes.Landscape:
                case MediaCoverTypes.ClearLogo:
                case MediaCoverTypes.Screenshot:
                    previewSize = "/w300/";
                    break;
                case MediaCoverTypes.Unknown:
                default:
                    break;
            }

            var preview = url.Replace("/original/", previewSize).Replace("/fanart/", "/preview/");
            var thumb = new XElement("thumb", url);

            if (preview.IsNotNullOrWhiteSpace())
            {
                thumb.Add(new XAttribute("preview", preview));
            }

            if (mediaCoverType != MediaCoverTypes.Fanart && mediaCoverType != MediaCoverTypes.Screenshot)
            {
                thumb.Add(new XAttribute("aspect", mediaCoverType));
            }

            if (season.HasValue)
            {
                thumb.Add(new XAttribute("type", "season"), new XAttribute("season", season.Value));
            }

            return thumb;
        }
    }
}
