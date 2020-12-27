using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DataAugmentation.DailySeries;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource.TmdbSkyHook.Fanart;
using NzbDrone.Core.Tv;
using System;
using System.Collections.Generic;
using System.Linq;
using TMDbLib.Client;
using TMDbLib.Objects.Find;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;

namespace NzbDrone.Core.MetadataSource.TmdbSkyHook
{
    public class TmdbSkyHookProxy : IProvideSeriesInfo, ISearchForNewSeries
    {
        private readonly IConfigService _configService;
        private readonly IDailySeriesService _dailySeriesService;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly ISeriesService _seriesService;

        private static readonly string BaseImageUrl = "https://image.tmdb.org/t/p";

        private static TMDbClient TmdbClient;

        public TmdbSkyHookProxy(
            IConfigService configService,
            IDailySeriesService dailySeriesService,
            IHttpClient httpClient,
            Logger logger,
            ISeriesService seriesService)
        {
            _configService = configService;
            _dailySeriesService = dailySeriesService;
            _httpClient = httpClient;
            _logger = logger;
            _seriesService = seriesService;
        }

        public Tuple<Series, List<Episode>> GetSeriesInfo(int tvdbSeriesId)
        {
            try
            {
                if (TmdbClient == null)
                {
                    TmdbClient = new TMDbClient(_configService.TmdbApiKey);
                }

                var findContainer = TmdbClient.FindAsync(FindExternalSource.TvDb, tvdbSeriesId.ToString()).Result;
                if (findContainer?.TvResults == null || !findContainer.TvResults.Any())
                {
                    throw new SeriesNotFoundException(tvdbSeriesId);
                }

                var tmdbSeriesId = findContainer.TvResults.FirstOrDefault().Id;

                var tvShowExtraMethods =
                    TvShowMethods.Credits |
                    TvShowMethods.ExternalIds |
                    TvShowMethods.ContentRatings;

                var tvShow = TmdbClient.GetTvShowAsync(tmdbSeriesId, tvShowExtraMethods, "es").Result;
                var fanartTv = GetFanartTv(tvdbSeriesId);

                var tvSeasonEpisodes = new List<TvSeasonEpisode>();
                foreach (var season in tvShow.Seasons)
                {
                    var tvSeason = TmdbClient.GetTvSeasonAsync(tmdbSeriesId, season.SeasonNumber, language: "es").Result;
                    tvSeasonEpisodes.AddRange(tvSeason.Episodes);
                }

                var episodes = tvSeasonEpisodes.SelectList(MapEpisode);
                var series = MapSeries(tvShow, fanartTv);

                return new Tuple<Series, List<Episode>>(series, episodes);
            }
            catch (SeriesNotFoundException ex)
            {
                _logger.Warn(ex, $"The series with TvdbId: '{tvdbSeriesId}' not found. Maybe exist but is not linked in TMDB.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, ex.Message);
                throw new TmdbSkyHookException("Search for TvdbId: '{0}' failed. Invalid response received from Tmdb.", tvdbSeriesId);
            }
        }

        public List<Series> SearchForNewSeries(string title)
        {
            try
            {
                if (TmdbClient == null)
                {
                    TmdbClient = new TMDbClient(_configService.TmdbApiKey);
                }

                var lowerTitle = title.ToLowerInvariant();

                if (lowerTitle.StartsWith("tvdb:") || lowerTitle.StartsWith("tvdbid:"))
                {
                    var slug = lowerTitle.Split(':')[1].Trim();

                    if (slug.IsNullOrWhiteSpace() || slug.Any(char.IsWhiteSpace) || !int.TryParse(slug, out int tvdbId) || tvdbId <= 0)
                    {
                        return new List<Series>();
                    }

                    try
                    {
                        var existingSeries = _seriesService.FindByTvdbId(tvdbId);
                        if (existingSeries != null)
                        {
                            return new List<Series> { existingSeries };
                        }

                        var newSeries = GetSeriesInfo(tvdbId);

                        return new List<Series> { GetSeriesInfo(tvdbId).Item1 };
                    }
                    catch (SeriesNotFoundException)
                    {
                        return new List<Series>();
                    }
                }

                var searchContainer = TmdbClient.SearchTvShowAsync(title.ToLower().Trim()).Result;

                return searchContainer.Results.SelectList(MapSearchResult);
            }
            catch (HttpException)
            {
                throw new TmdbSkyHookException("Search for '{0}' failed. Unable to communicate with SkyHook.", title);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, ex.Message);
                throw new TmdbSkyHookException("Search for '{0}' failed. Invalid response received from SkyHook.", title);
            }
        }

        private Series MapSearchResult(SearchTv searchTv)
        {
            var series = _seriesService.FindByTvRageId(searchTv.Id);
            if (series == null)
            {
                if (TmdbClient == null)
                {
                    TmdbClient = new TMDbClient(_configService.TmdbApiKey);
                }

                var tvShowExtraMethods =
                    TvShowMethods.Credits |
                    TvShowMethods.ExternalIds |
                    TvShowMethods.ContentRatings;

                var tvShow = TmdbClient.GetTvShowAsync(searchTv.Id, tvShowExtraMethods, "es").Result;

                FanartTv fanartTv = null;
                if (tvShow?.ExternalIds?.TvdbId != null)
                {
                    fanartTv = GetFanartTv(int.Parse(tvShow.ExternalIds.TvdbId));
                }

                series = MapSeries(tvShow, fanartTv);
            }

            return series;
        }

        private Series MapSeries(TvShow show, FanartTv fanartTv)
        {
            var series = new Series
            {
                TvdbId = int.Parse(show.ExternalIds?.TvdbId ?? "-1"),

                // As TvRageId is deprecated, we use it as TmdbId
                TvRageId = show.Id,
                ImdbId = show.ExternalIds?.ImdbId,
                Title = show.Name,
                CleanTitle = Parser.Parser.CleanSeriesTitle(show.Name),
                SortTitle = SeriesTitleNormalizer.Normalize(show.Name, show.Id),
                Overview = show.Overview
            };

            if (show.FirstAirDate.HasValue)
            {
                series.FirstAired = show.FirstAirDate.Value.ToUniversalTime();
                series.Year = series.FirstAired.Value.Year;
            }

            if (show.EpisodeRunTime != null && show.EpisodeRunTime.Any())
            {
                series.Runtime = show.EpisodeRunTime.Sum(a => a) / show.EpisodeRunTime.Count;
            }

            if (show.Networks != null && show.Networks.Any())
            {
                series.Network = show.Networks.FirstOrDefault().Name;
            }

            //if (show.TimeOfDay != null)
            //{
            //    series.AirTime = string.Format("{0:00}:{1:00}", show.TimeOfDay.Hours, show.TimeOfDay.Minutes);
            //}

            series.TitleSlug = show.Id + "-" + series.CleanTitle;
            series.Status = MapSeriesStatus(show.Status);
            series.Ratings = MapRatings(show.VoteCount, show.VoteAverage);
            series.Genres = show.Genres.Select(genre => genre.Name).ToList();

            var certification = MapCertification(show.ContentRatings?.Results ?? new List<ContentRating>());
            if (certification.IsNotNullOrWhiteSpace())
            {
                series.Certification = certification;
            }

            if (_dailySeriesService.IsDailySeries(series.TvdbId))
            {
                series.SeriesType = SeriesTypes.Daily;
            }

            series.Actors = show.Credits.Cast.Select(MapActors).ToList();
            series.Seasons = show.Seasons.Select(season => MapSeason(season, fanartTv)).ToList();

            series.Images = new List<MediaCover.MediaCover>();
            if (!string.IsNullOrEmpty(show.PosterPath))
            {
                series.Images.Add(MapTmdbImage(show.PosterPath, MediaCoverTypes.Poster));
            }
            if (!string.IsNullOrEmpty(show.BackdropPath))
            {
                series.Images.Add(MapTmdbImage(show.BackdropPath, MediaCoverTypes.Fanart));
            }

            if (fanartTv != null)
            {
                var isoLanguageTwoLetterCode = "es";
                var seriesbanner = GetBestOptionForFanartTvArt(fanartTv.tvbanner, isoLanguageTwoLetterCode);
                if (seriesbanner != null)
                {
                    series.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Banner, seriesbanner.url));
                }

                var seriesclearart = GetBestOptionForFanartTvArt(fanartTv.hdclearart, isoLanguageTwoLetterCode);
                if (seriesclearart != null)
                {
                    series.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.ClearArt, seriesclearart.url));
                }

                var seriesclearlogo = GetBestOptionForFanartTvArt(fanartTv.hdtvlogo, isoLanguageTwoLetterCode);
                if (seriesclearlogo != null)
                {
                    series.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.ClearLogo, seriesclearlogo.url));
                }

                var serieslandscape = GetBestOptionForFanartTvArt(fanartTv.tvthumb, isoLanguageTwoLetterCode);
                if (serieslandscape != null)
                {
                    series.Images.Add(new MediaCover.MediaCover(MediaCoverTypes.Landscape, serieslandscape.url));
                }
            }

            series.Monitored = true;

            return series;
        }

        private static Actor MapActors(Cast arg)
        {
            var newActor = new Actor
            {
                Name = arg.Name,
                Character = arg.Character
            };

            if (arg.ProfilePath != null)
            {
                newActor.Images = new List<MediaCover.MediaCover>
                {
                    MapTmdbImage(arg.ProfilePath, MediaCoverTypes.Headshot)
                };
            }

            return newActor;
        }

        private static Episode MapEpisode(TvSeasonEpisode tvEpisode)
        {
            var episode = new Episode
            {
                Overview = tvEpisode.Overview,
                SeasonNumber = tvEpisode.SeasonNumber,
                EpisodeNumber = tvEpisode.EpisodeNumber,
                //episode.AbsoluteEpisodeNumber = tvEpisode.AbsoluteEpisodeNumber;
                Title = tvEpisode.Name
                //episode.AiredAfterSeasonNumber = tvEpisode.AiredAfterSeasonNumber;
                //episode.AiredBeforeSeasonNumber = tvEpisode.AiredBeforeSeasonNumber;
                //episode.AiredBeforeEpisodeNumber = tvEpisode.AiredBeforeEpisodeNumber;
            };

            if (tvEpisode.AirDate.HasValue)
            {
                episode.AirDate = tvEpisode.AirDate.Value.ToString("yyyy-MM-dd");
                episode.AirDateUtc = tvEpisode.AirDate;
            }

            episode.Ratings = MapRatings(tvEpisode.VoteCount, tvEpisode.VoteAverage);

            //Don't include series fanart images as episode screenshot
            if (!string.IsNullOrEmpty(tvEpisode.StillPath))
            {
                episode.Images.Add(MapTmdbImage(tvEpisode.StillPath, MediaCoverTypes.Screenshot));
            }

            return episode;
        }

        private Season MapSeason(SearchTvSeason seasonResource, FanartTv fanartTv)
        {
            var images = new List<MediaCover.MediaCover>();
            if (!string.IsNullOrEmpty(seasonResource.PosterPath))
            {
                images.Add(MapTmdbImage(seasonResource.PosterPath, MediaCoverTypes.Poster));
            }
            if (fanartTv != null)
            {
                var isoLanguageTwoLetterCode = "es";

                if (fanartTv.seasonbanner != null)
                {
                    var seasonbanners = fanartTv.seasonbanner.Where(banner => banner.season == seasonResource.SeasonNumber.ToString());
                    if (seasonbanners != null)
                    {
                        var seasonbanner = GetBestOptionForFanartTvArt(seasonbanners.Cast<FanartTvArt>().ToList(), isoLanguageTwoLetterCode);
                        if (seasonbanner != null)
                        {
                            images.Add(new MediaCover.MediaCover(MediaCoverTypes.Banner, seasonbanner.url));
                        }
                    }
                }

                if (fanartTv.seasonthumb != null)
                {
                    var seasonthumbs = fanartTv.seasonthumb.Where(thumb => thumb.season == seasonResource.SeasonNumber.ToString());
                    if (seasonthumbs != null)
                    {
                        var seasonthumb = GetBestOptionForFanartTvArt(seasonthumbs.Cast<FanartTvArt>().ToList(), isoLanguageTwoLetterCode);
                        if (seasonthumb != null)
                        {
                            images.Add(new MediaCover.MediaCover(MediaCoverTypes.Landscape, seasonthumb.url));
                        }
                    }
                }
            }

            return new Season
            {
                SeasonNumber = seasonResource.SeasonNumber,
                Images = images,
                Monitored = seasonResource.SeasonNumber > 0
            };
        }

        private static SeriesStatusType MapSeriesStatus(string status)
        {
            if (status.Equals("ended", StringComparison.InvariantCultureIgnoreCase) ||
                status.Equals("canceled", StringComparison.InvariantCultureIgnoreCase))
            {
                return SeriesStatusType.Ended;
            }

            if (status.Equals("upcoming", StringComparison.InvariantCultureIgnoreCase))
            {
                return SeriesStatusType.Upcoming;
            }

            // Continuing + Returning Series
            return SeriesStatusType.Continuing;
        }

        private static Ratings MapRatings(int voteCount, double voteAverage)
        {
            if (voteCount == 0)
            {
                return new Ratings();
            }

            return new Ratings
            {
                Votes = voteCount,
                Value = (decimal)voteAverage
            };
        }

        private static string MapCertification(List<ContentRating> contentRatings)
        {
            var ourRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "ES", StringComparison.OrdinalIgnoreCase));
            var usRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
            var minimumRelease = contentRatings.FirstOrDefault();
            string certification = null;

            if (ourRelease != null && !string.Equals(ourRelease.Rating, "NR", StringComparison.OrdinalIgnoreCase))
            {
                switch (ourRelease.Rating.ToUpperInvariant())
                {
                    case "INFANTIL":
                    case "TP":
                        certification = "ES-APTA";
                        break;
                    case "7":
                        certification = "ES-7";
                        break;
                    case "10":
                    case "12":
                    case "13":
                        certification = "ES-12";
                        break;
                    case "16":
                        certification = "ES-16";
                        break;
                    case "18":
                    case "X":
                        certification = "ES-18";
                        break;
                    default:
                        break;
                }
            }
            else if (usRelease != null)
            {
                return certification = usRelease.Rating;
            }
            else if (minimumRelease != null)
            {
                certification = minimumRelease.Rating;
            }

            return certification;
        }

        private static MediaCover.MediaCover MapTmdbImage(string relativeUrl, MediaCoverTypes mediaCoverType)
        {
            var url = BaseImageUrl + "/original" + relativeUrl;
            return new MediaCover.MediaCover(mediaCoverType, url);
        }

        private FanartTv GetFanartTv(int tvdbId)
        {
            FanartTv fanartTv = null;

            if (_configService.FanartApiKey.IsNotNullOrWhiteSpace())
            {
                try
                {
                    var fanartRequest = new HttpRequest($"http://webservice.fanart.tv/v3/tv/{tvdbId}?api_key={_configService.FanartApiKey}", HttpAccept.Json);
                    var fanartResponse = _httpClient.Execute(fanartRequest);

                    var content = fanartResponse.Content;

                    if (content.IsNotNullOrWhiteSpace())
                    {
                        fanartTv = Json.Deserialize<FanartTv>(content);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Can't obtain data from Fanart for Series TvdbId: {0}", tvdbId);
                }
            }

            return fanartTv;
        }

        private FanartTvArt GetBestOptionForFanartTvArt(List<FanartTvArt> fanarts, string preferedLanguageTwoLetterCode)
        {
            FanartTvArt bestFanArt = null;

            if (fanarts != null && fanarts.Any())
            {
                bestFanArt = fanarts
                    .OrderBy(art => art.likes)
                    .FirstOrDefault(art => art.lang == preferedLanguageTwoLetterCode);

                if (bestFanArt == null)
                {
                    bestFanArt = fanarts.FirstOrDefault(art => art.lang.IsNullOrWhiteSpace());
                }

                if (bestFanArt == null)
                {
                    bestFanArt = fanarts.FirstOrDefault(art => art.lang == "en");
                }
            }

            return bestFanArt;
        }
    }
}
