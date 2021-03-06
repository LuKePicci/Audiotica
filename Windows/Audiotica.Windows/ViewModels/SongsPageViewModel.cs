using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Navigation;
using Audiotica.Core.Extensions;
using Audiotica.Core.Utilities.Interfaces;
using Audiotica.Core.Windows.Helpers;
using Audiotica.Database.Models;
using Audiotica.Database.Services.Interfaces;
using Audiotica.Windows.Engine.Mvvm;
using Audiotica.Windows.Enums;
using Audiotica.Windows.Services.Interfaces;

namespace Audiotica.Windows.ViewModels
{
    public class SongsPageViewModel : ViewModelBase
    {
        private readonly ILibraryCollectionService _libraryCollectionService;
        private readonly IPlayerService _playerService;
        private readonly ISettingsUtility _settingsUtility;
        private bool? _isSelectMode = false;
        private int _selectedIndex;
        private ObservableCollection<object> _selectedItems = new ObservableCollection<object>();
        private double _verticalOffset;
        private CollectionViewSource _viewSource;

        public SongsPageViewModel(
            ILibraryCollectionService libraryCollectionService,
            ILibraryService libraryService,
            ISettingsUtility settingsUtility,
            IPlayerService playerService)
        {
            _libraryCollectionService = libraryCollectionService;
            _settingsUtility = settingsUtility;
            _playerService = playerService;
            LibraryService = libraryService;

            SortItems =
                Enum.GetValues(typeof (TrackSort))
                    .Cast<TrackSort>()
                    .Select(sort => new ListBoxItem { Content = sort.GetEnumText(), Tag = sort })
                    .ToList();
            SortChangedCommand = new DelegateCommand<ListBoxItem>(SortChangedExecute);
            ShuffleAllCommand = new DelegateCommand(ShuffleAllExecute);

            var defaultSort = _settingsUtility.Read(ApplicationSettingsConstants.SongSort,
                TrackSort.DateAdded,
                SettingsStrategy.Roam);
            DefaultSort = SortItems.IndexOf(SortItems.FirstOrDefault(p => (TrackSort)p.Tag == defaultSort));
            ChangeSort(defaultSort);
        }

        public int DefaultSort { get; }

        public bool? IsSelectMode
        {
            get
            {
                return _isSelectMode;
            }
            set
            {
                Set(ref _isSelectMode, value);
            }
        }

        public ILibraryService LibraryService { get; set; }

        public int SelectedIndex
        {
            get
            {
                return _selectedIndex;
            }
            set
            {
                Set(ref _selectedIndex, value);
            }
        }

        public ObservableCollection<object> SelectedItems
        {
            get
            {
                return _selectedItems;
            }
            set
            {
                Set(ref _selectedItems, value);
            }
        }

        public DelegateCommand ShuffleAllCommand { get; }

        public DelegateCommand<ListBoxItem> SortChangedCommand { get; }

        public List<ListBoxItem> SortItems { get; }

        public double VerticalOffset
        {
            get
            {
                return _verticalOffset;
            }
            set
            {
                Set(ref _verticalOffset, value);
            }
        }

        public CollectionViewSource ViewSource
        {
            get
            {
                return _viewSource;
            }
            set
            {
                Set(ref _viewSource, value);
            }
        }

        public void ChangeSort(TrackSort sort)
        {
            _settingsUtility.Write(ApplicationSettingsConstants.SongSort, sort, SettingsStrategy.Roam);
            ViewSource = new CollectionViewSource { IsSourceGrouped = sort != TrackSort.DateAdded };

            switch (sort)
            {
                case TrackSort.AtoZ:
                    ViewSource.Source = _libraryCollectionService.TracksByTitle;
                    break;
                case TrackSort.DateAdded:
                    ViewSource.Source = _libraryCollectionService.TracksByDateAdded;
                    break;
                case TrackSort.Artist:
                    ViewSource.Source = _libraryCollectionService.TracksByArtist;
                    break;
                case TrackSort.Album:
                    ViewSource.Source = _libraryCollectionService.TracksByAlbum;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(sort), sort, null);
            }
        }

        public override void OnNavigatedTo(object parameter, NavigationMode mode, IDictionary<string, object> state)
        {
            if (state.ContainsKey("VerticalOffset"))
            {
                VerticalOffset = (double)state["VerticalOffset"];
                SelectedIndex = int.Parse(state["SelectedIndex"].ToString());
            }
        }

        public override void OnSaveState(IDictionary<string, object> state, bool suspending)
        {
            state["VerticalOffset"] = VerticalOffset;
            state["SelectedIndex"] = SelectedIndex;
        }

        private async void ShuffleAllExecute()
        {
            var playable = LibraryService.Tracks
                .Where(p => p.Status == TrackStatus.None || p.Status == TrackStatus.Downloading)
                .ToList();
            if (!playable.Any())
            {
                return;
            }

            var tracks = playable.Shuffle();
            await _playerService.NewQueueAsync(tracks);
        }

        private void SortChangedExecute(ListBoxItem item)
        {
            if (!(item?.Tag is TrackSort))
            {
                return;
            }
            var sort = (TrackSort)item.Tag;
            ChangeSort(sort);
        }
    }
}