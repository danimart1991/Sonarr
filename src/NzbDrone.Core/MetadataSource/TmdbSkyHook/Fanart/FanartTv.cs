using System.Collections.Generic;

namespace NzbDrone.Core.MetadataSource.TmdbSkyHook.Fanart
{
    public class FanartTv
    {
        public string Name { get; set; }
        public string thetvdb_id { get; set; }
        public List<FanartTvArt> clearlogo { get; set; }
        public List<FanartTvArt> hdtvlogo { get; set; }
        public List<FanartTvArt> clearart { get; set; }
        public List<FanartTvSeasonArt> showbackground { get; set; }
        public List<FanartTvArt> tvthumb { get; set; }
        public List<FanartTvArt> seasonposter { get; set; }
        public List<FanartTvSeasonArt> seasonthumb { get; set; }
        public List<FanartTvArt> hdclearart { get; set; }
        public List<FanartTvArt> tvbanner { get; set; }
        public List<FanartTvArt> characterart { get; set; }
        public List<FanartTvArt> tvposter { get; set; }
        public List<FanartTvSeasonArt> seasonbanner { get; set; }
    }
}
