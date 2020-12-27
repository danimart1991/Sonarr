using System.Net;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.MetadataSource.TmdbSkyHook
{
    public class TmdbSkyHookException : NzbDroneClientException
    {
        public TmdbSkyHookException(string message) : base(HttpStatusCode.ServiceUnavailable, message)
        {
        }

        public TmdbSkyHookException(string message, params object[] args)
            : base(HttpStatusCode.ServiceUnavailable, message, args)
        {
        }
    }
}
