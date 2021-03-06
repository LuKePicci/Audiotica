using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Audiotica.Core.Common;
using Audiotica.Core.Extensions;
using Audiotica.Database.Models;
using Audiotica.Database.Services.Interfaces;
using Audiotica.Web.Extensions;
using Audiotica.Web.Metadata.Interfaces;
using Audiotica.Web.Models;

namespace Audiotica.Converters
{
    public class WebToAlbumConverter : IConverter<WebAlbum, Album>
    {
        private readonly ILibraryService _libraryService;
        private readonly List<IBasicMetadataProvider> _providers;
        private readonly IConverter<WebArtist, Artist> _webArtistConverter;
        private readonly IConverter<WebSong, Track> _webTrackConverter;

        public WebToAlbumConverter(IEnumerable<IMetadataProvider> providers, ILibraryService libraryService,
            IConverter<WebArtist, Artist> webArtistConverter, IConverter<WebSong, Track> webTrackConverter)
        {
            _libraryService = libraryService;
            _webArtistConverter = webArtistConverter;
            _webTrackConverter = webTrackConverter;
            _providers = providers.FilterAndSort<IBasicMetadataProvider>();
        }

        public async Task<WebAlbum> FillPartialAsync(WebAlbum other)
        {
            var provider = _providers.FirstOrDefault(p => p.GetType() == other.MetadataProvider);

            if (other.IsPartial)
            {
                var web = await provider.GetAlbumAsync(other.Token);
                other.SetFrom(web);
            }

            return other;
        }

        public async Task<List<WebAlbum>> FillPartialAsync(IEnumerable<WebAlbum> others)
        {
            var tasks = others.Select(FillPartialAsync).ToList();
            return (await Task.WhenAll(tasks)).ToList();
        }

        public async Task<Album> ConvertAsync(WebAlbum other, bool ignoreLibrary = false)
        {
            await FillPartialAsync(other);

            // fill the album partial for each track
            foreach (var webSong in other.Tracks)
                webSong.Album = other;
            
            var album = new Album
            {
                Title = other.Title,
                ArtworkUri = other.Artwork.ToString(),
                Artist = await _webArtistConverter.ConvertAsync(other.Artist),
                Year = (uint?)other.ReleaseDate?.Year,
            };

            // TODO: ISupportIncrementalLoading?
            if (other.Tracks != null)
            {
                // only let 10 concurrent conversions
                using (var semaphoreSlim = new SemaphoreSlim(10, 10))
                {
                    // ReSharper disable AccessToDisposedClosure
                    var trackTasks = other.Tracks.Select(async p =>
                    {
                        await semaphoreSlim.WaitAsync();
                        var track = await _webTrackConverter.ConvertAsync(p);
                        semaphoreSlim.Release();
                        return track;
                    });
                    album.Tracks =
                        other.Tracks != null
                            ? new OptimizedObservableCollection<Track>(
                                await Task.WhenAll(trackTasks))
                            : null;
                    // ReSharper restore AccessToDisposedClosure
                }
            }

            var libraryAlbum = _libraryService.Albums.FirstOrDefault(p => p.Title.EqualsIgnoreCase(album.Title));
            other.PreviousConversion = libraryAlbum ?? album;

            return ignoreLibrary ? album : libraryAlbum ?? album;
        }

        public async Task<List<Album>> ConvertAsync(IEnumerable<WebAlbum> others, bool ignoreLibrary = false)
        {
            var tasks = others.Select(p => ConvertAsync(p, ignoreLibrary)).ToList();
            return (await Task.WhenAll(tasks)).ToList();
        }
    }
}