﻿using System;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Audiotica.Core.Exceptions;
using Audiotica.Core.Windows.Helpers;
using Audiotica.Database.Models;
using Audiotica.Windows.Common;
using Audiotica.Windows.Services.Interfaces;
using Audiotica.Windows.Services.NavigationService;
using Audiotica.Windows.Views;
using Autofac;

namespace Audiotica.Windows.Controls
{
    public sealed partial class TrackViewer
    {
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register("IsSelected", typeof (bool), typeof (TrackViewer), null);

        public static readonly DependencyProperty IsCatalogProperty =
            DependencyProperty.Register("IsCatalog", typeof(bool), typeof(TrackViewer), null);

        private Track _track;

        public TrackViewer()
        {
            InitializeComponent();
        }

        public bool IsSelected

        {
            get { return (bool) GetValue(IsSelectedProperty); }

            set { SetValue(IsSelectedProperty, value); }
        }

        public bool IsCatalog

        {
            get { return (bool) GetValue(IsCatalogProperty); }

            set { SetValue(IsCatalogProperty, value); }
        }

        public Track Track
        {
            get { return _track; }
            set
            {
                _track = value;
                Bindings.Update();
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            using (var lifetimeScope = App.Current.Kernel.BeginScope())
            {
                var playerService = lifetimeScope.Resolve<IPlayerService>();
                try
                {
                    var queue = await playerService.AddAsync(Track);
                    // player auto plays when there is only one track
                    if (playerService.PlaybackQueue.Count > 1)
                        playerService.Play(queue);
                }
                catch (AppException ex)
                {
                    CurtainPrompt.ShowError(ex.Message ?? "Something happened.");
                }
            }
        }

        private async void AddCollection_Click(object sender, RoutedEventArgs e)
        {
            var button = (MenuFlyoutItem) sender;
            button.IsEnabled = false;

            using (var scope = App.Current.Kernel.BeginScope())
            {
                var trackSaveService = scope.Resolve<ITrackSaveService>();

                try
                {
                    await trackSaveService.SaveAsync(Track);
                }
                catch (AppException ex)
                {
                    Track.Status = TrackStatus.None;
                    CurtainPrompt.ShowError(ex.Message ?? "Problem saving song.");
                }
                finally
                {
                    button.IsEnabled = true;
                }
            }
        }

        private async void AddQueue_Click(object sender, RoutedEventArgs e)
        {
            using (var scope = App.Current.Kernel.BeginScope())
            {
                var backgroundAudioService = scope.Resolve<IPlayerService>();
                try
                {
                    await backgroundAudioService.AddAsync(Track);
                    CurtainPrompt.Show("Added to queue");
                }
                catch (AppException ex)
                {
                    CurtainPrompt.ShowError(ex.Message ?? "Something happened.");
                }
            }
        }

        private async void AddUpNext_Click(object sender, RoutedEventArgs e)
        {
            using (var scope = App.Current.Kernel.BeginScope())
            {
                var backgroundAudioService = scope.Resolve<IPlayerService>();
                try
                {
                    await backgroundAudioService.AddUpNextAsync(Track);
                    CurtainPrompt.Show("Added up next");
                }
                catch (AppException ex)
                {
                    CurtainPrompt.ShowError(ex.Message ?? "Something happened.");
                }
            }
        }

        private void Viewer_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var grid = (Grid) sender;
            FlyoutEx.ShowAttachedFlyoutAtPointer(grid);

        }

        private void ExploreArtist_Click(object sender, RoutedEventArgs e)
        {
            using (var scope = App.Current.Kernel.BeginScope())
            {
                var navigationService = scope.Resolve<INavigationService>();
                navigationService.Navigate(typeof (ArtistPage), Track.DisplayArtist);
            }
        }

        private void Download_Click(object sender, RoutedEventArgs e)
        {
            using (var scope = App.Current.Kernel.BeginScope())
            {
                var downloadService = scope.Resolve<IDownloadService>();
                downloadService.StartDownloadAsync(Track);
            }
        }
    }
}