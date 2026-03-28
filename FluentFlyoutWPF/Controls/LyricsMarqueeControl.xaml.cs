// Copyright © 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using MicaWPF.Core.Enums;
using MicaWPF.Core.Helpers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Media.Control;

namespace FluentFlyout.Controls;

/// <summary>
/// A control that displays time-synced lyrics, showing the current line
/// based on the song's playback position.
/// </summary>
public partial class LyricsMarqueeControl : UserControl
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly DispatcherTimer _syncTimer;
    private List<LyricLine>? _syncedLyrics;
    private string? _plainLyrics;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _currentLineIndex = -1;
    private bool _isPaused;
    private bool _hasContent;

    // For scrolling long lines
    private readonly DispatcherTimer _scrollTimer;
    private double _textWidth;
    private double _controlWidth;
    private bool _needsScroll;

    public LyricsMarqueeControl()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        SettingsManager.Current.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SettingsManager.Current.LyricsTextColor))
            {
                ApplyWindowsTheme();
            }
        };
        ApplyWindowsTheme();

        // Timer to sync lyrics with playback position (~10 checks per second for better precision)
        _syncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _syncTimer.Tick += SyncTimer_Tick;

        // Timer for scrolling long lines
        _scrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30fps
        };
        _scrollTimer.Tick += ScrollTimer_Tick;

        SizeChanged += (s, e) =>
        {
            _controlWidth = ActualWidth;
        };
    }

    public void ApplyWindowsTheme()
    {
        string hex = SettingsManager.Current.LyricsTextColor;
        if (string.IsNullOrWhiteSpace(hex) || hex.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            bool isDark = WindowsThemeHelper.GetCurrentWindowsTheme() == WindowsTheme.Dark;
            MarqueeText.Foreground = new SolidColorBrush(isDark
                ? Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
        }
        else
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                MarqueeText.Foreground = new SolidColorBrush(color);
            }
            catch
            {
                bool isDark = WindowsThemeHelper.GetCurrentWindowsTheme() == WindowsTheme.Dark;
                MarqueeText.Foreground = new SolidColorBrush(isDark
                    ? Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
            }
        }
    }

    /// <summary>
    /// Sets time-synced lyrics and the media session for position tracking.
    /// </summary>
    public void SetSyncedLyrics(List<LyricLine>? lyrics, GlobalSystemMediaTransportControlsSession? session)
    {
        Dispatcher.Invoke(() =>
        {
            _syncedLyrics = lyrics;
            _plainLyrics = null;
            _session = session;
            _currentLineIndex = -1;

            if (lyrics == null || lyrics.Count == 0 || session == null)
            {
                _hasContent = false;
                _syncTimer.Stop();
                _scrollTimer.Stop();
                MarqueeText.Text = string.Empty;
                Visibility = Visibility.Collapsed;
                return;
            }

            _hasContent = true;
            Visibility = Visibility.Visible;

            if (!_isPaused)
                _syncTimer.Start();

            // Show first line immediately
            UpdateCurrentLine();
        });
    }

    /// <summary>
    /// Sets plain (non-synced) lyrics as a fallback scrolling marquee.
    /// </summary>
    public void SetPlainLyrics(string? lyrics)
    {
        Dispatcher.Invoke(() =>
        {
            _syncedLyrics = null;
            _session = null;
            _syncTimer.Stop();
            _currentLineIndex = -1;

            if (string.IsNullOrWhiteSpace(lyrics))
            {
                _hasContent = false;
                _scrollTimer.Stop();
                MarqueeText.Text = string.Empty;
                Visibility = Visibility.Collapsed;
                return;
            }

            // Combine into single scrolling line
            _plainLyrics = lyrics
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n\n", " ★ ")
                .Replace("\n", " ◆ ");

            string displayText = _plainLyrics + "          ★          " + _plainLyrics + "          ★          ";
            MarqueeText.Text = displayText;

            MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _textWidth = MarqueeText.DesiredSize.Width;
            _controlWidth = ActualWidth > 0 ? ActualWidth : 300;

            _hasContent = true;
            _needsScroll = true;
            Visibility = Visibility.Visible;
            MarqueeTranslate.X = _controlWidth;

            if (!_isPaused)
                _scrollTimer.Start();
        });
    }

    /// <summary>
    /// Pauses or resumes lyrics display.
    /// </summary>
    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        Dispatcher.Invoke(() =>
        {
            if (paused)
            {
                _syncTimer.Stop();
                _scrollTimer.Stop();
                if (SettingsManager.Current.LyricsHideWhenPaused)
                {
                    Visibility = Visibility.Collapsed;
                }
                else
                {
                    MarqueeText.Opacity = 0.45;
                }
            }
            else if (_hasContent)
            {
                Visibility = Visibility.Visible;
                if (_syncedLyrics != null)
                    _syncTimer.Start();
                else if (_plainLyrics != null)
                    _scrollTimer.Start();
                MarqueeText.Opacity = 0.85;
            }
        });
    }

    public void Clear()
    {
        SetSyncedLyrics(null, null);
    }

    private void SyncTimer_Tick(object? sender, EventArgs e)
    {
        if (!_hasContent || _session == null || _syncedLyrics == null)
            return;

        if (!SettingsManager.Current.LyricsMarqueeEnabled)
        {
            _syncTimer.Stop();
            Visibility = Visibility.Collapsed;
            return;
        }

        UpdateCurrentLine();
    }

    private void UpdateCurrentLine()
    {
        if (_syncedLyrics == null || _session == null)
            return;

        try
        {
            var timeline = _session.GetTimelineProperties();
            var playbackInfo = _session.GetPlaybackInfo();
            
            TimeSpan position = timeline.Position;

            // If playing, we must add the exact elapsed time since Windows last updated the session snapshot.
            // Otherwise the position will freeze and lag behind by multiple seconds until Windows updates it again.
            if (playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                TimeSpan elapsedSinceUpdate = DateTimeOffset.Now - timeline.LastUpdatedTime;
                position += elapsedSinceUpdate;
            }

            // Add a small 100ms offset to compensate for our fade-in animation duration
            position = position.Add(TimeSpan.FromMilliseconds(100));

            // Find the current lyric line based on position
            int newIndex = -1;
            for (int i = _syncedLyrics.Count - 1; i >= 0; i--)
            {
                if (position >= _syncedLyrics[i].Timestamp)
                {
                    newIndex = i;
                    break;
                }
            }

            if (newIndex == _currentLineIndex)
                return; // Same line, no update needed

            _currentLineIndex = newIndex;

            if (newIndex < 0)
            {
                // Before first lyric line
                MarqueeText.Text = "♪ ♪ ♪";
                _scrollTimer.Stop();
                _needsScroll = false;
                MarqueeTranslate.X = 0;
                return;
            }

            string lineText = _syncedLyrics[newIndex].Text;

            // Animate the line change (which natively checks layout requirements when completed)
            AnimateLineChange(lineText);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error getting timeline position: {ex.Message}");
        }
    }

    private void HandleLineMeasurement()
    {
        MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = MarqueeText.DesiredSize.Width;
        _controlWidth = ActualWidth > 0 ? ActualWidth : 300;

        if (_textWidth > _controlWidth)
        {
            _needsScroll = true;
            if (!_isPaused)
                _scrollTimer.Start();
        }
        else
        {
            _needsScroll = false;
            _scrollTimer.Stop();
        }
    }

    private void AnimateLineChange(string newText)
    {
        try
        {
            if (SettingsManager.Current.LyricsAnimationMode == 1) // Slide
            {
                var slideOut = new DoubleAnimation
                {
                    To = -_controlWidth - 50,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                slideOut.Completed += (s, e) =>
                {
                    MarqueeText.Text = newText;
                    HandleLineMeasurement();
                    
                    MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    MarqueeTranslate.X = _controlWidth + 50;
                    double targetX = _controlWidth > _textWidth ? (_controlWidth - _textWidth) / 2 : 0;

                    var slideIn = new DoubleAnimation
                    {
                        To = targetX,
                        Duration = TimeSpan.FromMilliseconds(300),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
                };

                MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
            }
            else // Fade
            {
                var fadeOut = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(60),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                fadeOut.Completed += (s, e) =>
                {
                    MarqueeText.Text = newText;
                    HandleLineMeasurement();
                    
                    MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    MarqueeTranslate.X = _controlWidth > _textWidth ? (_controlWidth - _textWidth) / 2 : 0;

                    var fadeIn = new DoubleAnimation
                    {
                        To = _isPaused ? (SettingsManager.Current.LyricsHideWhenPaused ? 0.0 : 0.45) : 0.85,
                        Duration = TimeSpan.FromMilliseconds(120),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };
                    MarqueeText.BeginAnimation(OpacityProperty, fadeIn);
                };

                MarqueeText.BeginAnimation(OpacityProperty, fadeOut);
            }
        }
        catch
        {
            // Fallback: just set text directly
            MarqueeText.Text = newText;
            HandleLineMeasurement();
            MarqueeTranslate.X = _controlWidth > _textWidth ? (_controlWidth - _textWidth) / 2 : 0;
        }
    }

    private void ScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_hasContent || !SettingsManager.Current.LyricsMarqueeEnabled)
        {
            _scrollTimer.Stop();
            return;
        }

        int speed = Math.Clamp(SettingsManager.Current.LyricsMarqueeSpeed, 1, 5);

        if (_syncedLyrics != null && _needsScroll)
        {
            // For synced lyrics: scroll the long line back and forth
            MarqueeTranslate.X -= speed;
            if (MarqueeTranslate.X < -(_textWidth - _controlWidth) - 20)
            {
                // Reached end, pause briefly then reset
                MarqueeTranslate.X = 10;
            }
        }
        else if (_plainLyrics != null)
        {
            // For plain lyrics: continuous scroll
            MarqueeTranslate.X -= speed;
            double halfTextWidth = _textWidth / 2.0;
            if (MarqueeTranslate.X < -halfTextWidth)
            {
                MarqueeTranslate.X += halfTextWidth;
            }
        }
    }
}
