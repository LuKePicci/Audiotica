﻿#region

using System;
using System.Linq;
using System.Threading.Tasks;
using Audiotica.Core.Common;
using Audiotica.Core.Utilities;
using Audiotica.Data;
using Audiotica.Data.Collection.Model;
using Audiotica.Data.Model.Spotify.Models;
using IF.Lastfm.Core.Objects;

#endregion

namespace Audiotica
{
    public static class SpotifyHelper
    {
        public static Artist ToArtist(this SimpleArtist simpleArtist)
        {
            return new Artist
            {
                Name = simpleArtist.Name.Trim().Replace("  ", " "),
                ProviderId = "spotify." + simpleArtist.Id
            };
        }

        public static Album ToAlbum(this FullAlbum fullAlbum)
        {
            var album = new Album
            {
                ProviderId = "spotify." + fullAlbum.Id,
                Name = fullAlbum.Name.Trim().Replace("  ", " "),
                ReleaseDate = GetDateTime(fullAlbum),
                Genre = fullAlbum.Genres != null ? fullAlbum.Genres.FirstOrDefault() : ""
            };

            return album;
        }

        private static DateTime GetDateTime(FullAlbum album)
        {
            if (album.ReleaseDatePrecision == "year")
            {
                return new DateTime(int.Parse(album.ReleaseDate), 0, 0);
            }
            return DateTime.Parse(album.ReleaseDate);
        }

        public static Song ToSong(this SimpleTrack track)
        {
            var song = new Song
            {
                ProviderId = "spotify." + track.Id,
                Name = track.Name.Trim().Replace("  ", " "),
                TrackNumber = track.TrackNumber
            };
            return song;
        }

        public static async Task SaveTrackAsync(SimpleTrack track, FullAlbum album)
        {
            var artist = track is FullTrack ? (track as FullTrack).Artist : track.Artist;
            var url = await Mp3MatchEngine.FindMp3For(track.Name, artist.Name);

            if (string.IsNullOrEmpty(url))
                CurtainToast.ShowError("NoMatchFoundToast".FromLanguageResource());

            else
            {
                var preparedSong = track.ToSong();
                preparedSong.ArtistName = artist.Name;
                preparedSong.Album = album.ToAlbum();
                preparedSong.Artist = album.Artist.ToArtist();
                preparedSong.Album.PrimaryArtist = preparedSong.Artist;
                preparedSong.AudioUrl = url;

                try
                {
                    await App.Locator.CollectionService.AddSongAsync(preparedSong, album.Images[0].Url);
                    CurtainToast.Show("SongSavedToast".FromLanguageResource());
                }
                catch (Exception e)
                {
                    CurtainToast.ShowError(e.Message);
                }
            }
        }
    }
}