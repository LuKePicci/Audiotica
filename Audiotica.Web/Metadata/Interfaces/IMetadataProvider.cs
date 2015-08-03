﻿using System;
using System.Threading.Tasks;
using Audiotica.Web.Models;

namespace Audiotica.Web.Metadata.Interfaces
{
    public interface IMetadataProvider : IConfigurableProvider
    {
        Task<WebResults> SearchAsync(string query, WebResults.Type searchType = WebResults.Type.Song,
            int limit = 10, string pagingToken = null);

        Task<WebAlbum> GetAlbumAsync(string albumToken);
        Task<WebSong> GetSongAsync(string songToken);
        Task<WebArtist> GetArtistAsync(string artistToken);
        Task<WebResults> GetArtistTopSongsAsync(string artistToken, int limit = 50, string pageToken = null);
        Task<WebResults> GetArtistAlbumsAsync(string artistToken, int limit = 50, string pageToken = null);
        Task<Uri> GetArtworkAsync(string album, string artist);
        Task<string> GetLyricAsync(string song, string artist);
    }
}