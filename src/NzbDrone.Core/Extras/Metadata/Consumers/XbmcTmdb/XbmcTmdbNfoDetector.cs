using System.Text.RegularExpressions;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.Extras.Metadata.Consumers.XbmcTmdb
{
    public interface IDetectXbmcTmdbNfo
    {
        bool IsXbmcTmdbNfoFile(string path);
    }

    public class XbmcTmdbNfoDetector : IDetectXbmcTmdbNfo
    {
        private readonly IDiskProvider _diskProvider;

        private readonly Regex _regex = new Regex("<(movie|tvshow|episodedetails|artist|album|musicvideo)>", RegexOptions.Compiled);

        public XbmcTmdbNfoDetector(IDiskProvider diskProvider)
        {
            _diskProvider = diskProvider;
        }

        public bool IsXbmcTmdbNfoFile(string path)
        {
            // Lets make sure we're not reading huge files.
            if (_diskProvider.GetFileSize(path) > 10.Megabytes())
            {
                return false;
            }

            // Check if it contains some of the kodi/xbmc xml tags
            var content = _diskProvider.ReadAllText(path);

            return _regex.IsMatch(content);
        }
    }
}
