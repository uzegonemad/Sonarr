﻿using System;
using System.Net;
using MonoTorrent;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Configuration;
using NLog;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download
{
    public abstract class TorrentClientBase<TSettings> : DownloadClientBase<TSettings>
        where TSettings : IProviderConfig, new()
    {
        protected readonly IHttpClient _httpClient;
        protected readonly ITorrentFileInfoReader _torrentFileInfoReader;

        protected TorrentClientBase(ITorrentFileInfoReader torrentFileInfoReader,
                                    IHttpClient httpClient,
                                    IConfigService configService,
                                    IDiskProvider diskProvider,
                                    IRemotePathMappingService remotePathMappingService,
                                    Logger logger)
            : base(configService, diskProvider, remotePathMappingService, logger)
        {
            _httpClient = httpClient;
            _torrentFileInfoReader = torrentFileInfoReader;
        }
        
        public override DownloadProtocol Protocol
        {
            get
            {
                return DownloadProtocol.Torrent;
            }
        }

        protected abstract string AddFromMagnetLink(RemoteEpisode remoteEpisode, string hash, string magnetLink);
        protected abstract string AddFromTorrentFile(RemoteEpisode remoteEpisode, string hash, string filename, byte[] fileContent);

        public override string Download(RemoteEpisode remoteEpisode)
        {
            var torrentInfo = remoteEpisode.Release as TorrentInfo;

            string magnetUrl = null;
            string torrentUrl = null;

            if (remoteEpisode.Release.DownloadUrl.IsNotNullOrWhiteSpace() && remoteEpisode.Release.DownloadUrl.StartsWith("magnet:"))
            {
                magnetUrl = remoteEpisode.Release.DownloadUrl;
            }
            else
            {
                torrentUrl = remoteEpisode.Release.DownloadUrl;
            }

            if (torrentInfo != null && !torrentInfo.MagnetUrl.IsNullOrWhiteSpace())
            {
                magnetUrl = torrentInfo.MagnetUrl;
            }

            string hash = null;

            if (magnetUrl.IsNotNullOrWhiteSpace())
            {
                try
                {
                    hash = DownloadFromMagnetUrl(remoteEpisode, magnetUrl);
                }
                catch (NotSupportedException ex)
                {
                    if (torrentUrl.IsNullOrWhiteSpace())
                    {
                        throw new ReleaseDownloadException(remoteEpisode.Release, "Magnet not supported by download client. ({0})", ex.Message);
                    }

                    _logger.Debug("Magnet not supported by download client, trying torrent. ({0})", ex.Message);
                }
            }

            if (hash == null && torrentUrl.IsNotNullOrWhiteSpace())
            {
                hash = DownloadFromWebUrl(remoteEpisode, torrentUrl);
            }

            return hash;
        }

        private string DownloadFromWebUrl(RemoteEpisode remoteEpisode, string torrentUrl)
        {
            byte[] torrentFile = null;

            try
            {
                var request = new HttpRequest(torrentUrl);
                request.Headers.Accept = "application/x-bittorrent";
                request.AllowAutoRedirect = false;

                var response = _httpClient.Get(request);

                if (response.StatusCode == HttpStatusCode.SeeOther || response.StatusCode == HttpStatusCode.Found)
                {
                    var locationHeader = (string)response.Headers.GetValueOrDefault("Location", null);

                    _logger.Trace("Torrent request is being redirected to: {0}", locationHeader);

                    if (locationHeader != null)
                    {
                        if (locationHeader.StartsWith("magnet:"))
                        {
                            return DownloadFromMagnetUrl(remoteEpisode, locationHeader);
                        }

                        return DownloadFromWebUrl(remoteEpisode, locationHeader);
                    }

                    throw new WebException("Remote website tried to redirect without providing a location.");
                }

                torrentFile = response.ResponseData;

                _logger.Debug("Downloading torrent for episode '{0}' finished ({1} bytes from {2})", remoteEpisode.Release.Title, torrentFile.Length, torrentUrl);
            }
            catch (HttpException ex)
            {
                _logger.ErrorException(string.Format("Downloading torrent file for episode '{0}' failed ({1})",
                    remoteEpisode.Release.Title, torrentUrl), ex);

                throw new ReleaseDownloadException(remoteEpisode.Release, "Downloading torrent failed", ex);
            }
            catch (WebException ex)
            {
                _logger.ErrorException(string.Format("Downloading torrent file for episode '{0}' failed ({1})",
                    remoteEpisode.Release.Title, torrentUrl), ex);

                throw new ReleaseDownloadException(remoteEpisode.Release, "Downloading torrent failed", ex);
            }

            var filename = string.Format("{0}.torrent", FileNameBuilder.CleanFileName(remoteEpisode.Release.Title));
            var hash = _torrentFileInfoReader.GetHashFromTorrentFile(torrentFile);
            var actualHash = AddFromTorrentFile(remoteEpisode, hash, filename, torrentFile);

            if (actualHash.IsNotNullOrWhiteSpace() && hash != actualHash)
            {
                _logger.Debug(
                    "{0} did not return the expected InfoHash for '{1}', Sonarr could potentially lose track of the download in progress.",
                    Definition.Implementation, remoteEpisode.Release.DownloadUrl);
            }

            return actualHash;
        }

        private string DownloadFromMagnetUrl(RemoteEpisode remoteEpisode, string magnetUrl)
        {
            string hash = null;
            string actualHash = null;

            try
            {
                hash = new MagnetLink(magnetUrl).InfoHash.ToHex();
            }
            catch (FormatException ex)
            {
                _logger.ErrorException(string.Format("Failed to parse magnetlink for episode '{0}': '{1}'",
                    remoteEpisode.Release.Title, magnetUrl), ex);

                return null;
            }

            if (hash != null)
            {
                actualHash = AddFromMagnetLink(remoteEpisode, hash, magnetUrl);
            }

            if (actualHash.IsNotNullOrWhiteSpace() && hash != actualHash)
            {
                _logger.Debug(
                    "{0} did not return the expected InfoHash for '{1}', Sonarr could potentially lose track of the download in progress.",
                    Definition.Implementation, remoteEpisode.Release.DownloadUrl);
            }

            return actualHash;
        }
    }
}
