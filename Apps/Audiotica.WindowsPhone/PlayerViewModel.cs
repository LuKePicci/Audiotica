﻿#region

using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Audiotica.Core;
using Audiotica.Core.Utilities;
using Audiotica.Core.Utils.Interfaces;
using Audiotica.Data.Collection;
using Audiotica.Data.Collection.Model;
using Audiotica.View;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using IF.Lastfm.Core.Api.Enums;
using Xamarin;

#endregion

namespace Audiotica
{
    public class PlayerViewModel : ViewModelBase
    {
        private readonly AudioPlayerHelper _helper;
        private readonly RelayCommand _nextRelayCommand;
        private readonly RelayCommand _playPauseRelayCommand;
        private readonly RelayCommand _prevRelayCommand;
        private readonly ICollectionService _service;
        private readonly IAppSettingsHelper _appSettingsHelper;
        private readonly DispatcherTimer _timer;
        private QueueSong _currentQueue;
        private TimeSpan _duration;
        private bool _isLoading;
        private double _npHeight;
        private double _npbHeight = double.NaN;
        private Symbol _playPauseIcon;
        private TimeSpan _position;
        private bool _isPlayerActive;

        public PlayerViewModel(AudioPlayerHelper helper, ICollectionService service, IAppSettingsHelper appSettingsHelper)
        {
            _helper = helper;
            _service = service;
            _appSettingsHelper = appSettingsHelper;

            if (!IsInDesignMode)
            {
                helper.TrackChanged += HelperOnTrackChanged;
                helper.PlaybackStateChanged += HelperOnPlaybackStateChanged;
                helper.Shutdown += HelperOnShutdown;

                _nextRelayCommand = new RelayCommand(NextSong);
                _prevRelayCommand = new RelayCommand(PrevSong);
                _playPauseRelayCommand = new RelayCommand(PlayPauseToggle);

                _timer = new DispatcherTimer {Interval = TimeSpan.FromSeconds(1)};
                _timer.Tick += TimerOnTick;
            }
            else
            {
                CurrentQueue = service.PlaybackQueue.FirstOrDefault();
                PlayPauseIcon = Symbol.Play;
            }
        }

        public bool IsRepeat
        {
            get { return _appSettingsHelper.Read<bool>("Repeat"); }
            set
            {
                _appSettingsHelper.Write("Repeat", value);
                RaisePropertyChanged();
            }
        }

        public bool IsShuffle
        {
            get { return _appSettingsHelper.Read<bool>("Shuffle"); }
            set
            {
                _appSettingsHelper.Write("Shuffle", value);
                _service.ShuffleModeChanged();
                RaisePropertyChanged();
                AudioPlayerHelper.OnShuffleChanged();
                Insights.Track("Shuffle", "Enabled", value ? "True" : "False");
            }
        }

        public bool IsPlayerActive
        {
            get { return _isPlayerActive; }
            set { Set(ref _isPlayerActive, value); }
        }

        public TimeSpan Duration
        {
            get { return _duration; }
            set { Set(ref _duration, value); }
        }

        public TimeSpan Position
        {
            get { return _position; }
            set { Set(ref _position, value); }
        }

        public QueueSong CurrentQueue
        {
            get { return _currentQueue; }
            set { Set(ref _currentQueue, value); }
        }

        public Symbol PlayPauseIcon
        {
            get { return _playPauseIcon; }
            set { Set(ref _playPauseIcon, value); }
        }

        public bool IsLoading
        {
            get { return _isLoading; }
            set { Set(ref _isLoading, value); }
        }

        public RelayCommand NextRelayCommand
        {
            get { return _nextRelayCommand; }
        }

        public RelayCommand PrevRelayCommand
        {
            get { return _prevRelayCommand; }
        }

        public RelayCommand PlayPauseRelayCommand
        {
            get { return _playPauseRelayCommand; }
        }

        public double NowPlayingHeight
        {
            get { return _npHeight; }
            set { Set(ref _npHeight, value); }
        }

        public double NowPlayingBarHeight
        {
            get { return _npbHeight; }
            set { Set(ref _npbHeight, value); }
        }

        public ICollectionService CollectionService
        {
            get { return _service; }
        }

        public AudioPlayerHelper AudioPlayerHelper
        {
            get { return _helper; }
        }

        private void TimerOnTick(object sender, object o)
        {
            Position = BackgroundMediaPlayer.Current.Position;
        }

        private void HelperOnShutdown(object sender, EventArgs eventArgs)
        {
            CurrentQueue = null;
            NowPlayingSheetUtility.CloseNowPlaying();
            IsPlayerActive = false;
        }

        private void HelperOnPlaybackStateChanged(object sender, PlaybackStateEventArgs playbackStateEventArgs)
        {
            IsLoading = false;
            switch (playbackStateEventArgs.State)
            {
                default:
                    PlayPauseIcon = Symbol.Play;
                    _timer.Stop();
                    break;
                case MediaPlayerState.Playing:
                    _timer.Start();
                    PlayPauseIcon = Symbol.Pause;
                    break;
                case MediaPlayerState.Buffering:
                case MediaPlayerState.Opening:
                    IsLoading = true;
                    break;
            }
        }

        private void HelperOnTrackChanged(object sender, EventArgs eventArgs)
        {
            var playerInstance = BackgroundMediaPlayer.Current;

            if (playerInstance == null) return;

            Duration = playerInstance.NaturalDuration;
            var state = MediaPlayerState.Closed;

            try
            {
                state = playerInstance.CurrentState;
            }
            catch
            {
                // ignored, rare occacion where the player just throws a generic Exception
            }

            if (state != MediaPlayerState.Closed &&
                 state != MediaPlayerState.Stopped)
            {
                if (CurrentQueue != null)
                {
                    var lastPlayed = DateTime.Now - CurrentQueue.Song.LastPlayed;

                    if (lastPlayed.TotalSeconds > 30)
                    {
                        CurrentQueue.Song.PlayCount++;
                        CurrentQueue.Song.LastPlayed = DateTime.Now;
                    }
                }

                var currentId = _appSettingsHelper.Read<int>(PlayerConstants.CurrentTrack);
                CurrentQueue = _service.PlaybackQueue.FirstOrDefault(p => p.Id == currentId);

                if (CurrentQueue != null
                    && CurrentQueue.Song != null
                    && CurrentQueue.Song.Duration.Ticks != Duration.Ticks)
                    CurrentQueue.Song.Duration = Duration;

                IsPlayerActive = true;
            }
            else
            {
                NowPlayingSheetUtility.CloseNowPlaying();
                IsPlayerActive = false;
                CurrentQueue = null;
            }
        }

        private void PlayPauseToggle()
        {
            _helper.PlayPauseToggle();
        }

        private void PrevSong()
        {
            _helper.PrevSong();
        }

        private void NextSong()
        {
            _helper.NextSong();
        }
    }
}