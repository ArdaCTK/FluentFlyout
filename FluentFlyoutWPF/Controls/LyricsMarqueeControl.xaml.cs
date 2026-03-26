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

    public bool HasLyrics => _hasContent;

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
            if (timeline == null) return;
            
            var playbackInfo = _session.GetPlaybackInfo();
            TimeSpan currentPosition = timeline.Position;

            if (playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                TimeSpan elapsedSinceUpdate = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsedSinceUpdate.TotalHours < 1)
                    currentPosition += elapsedSinceUpdate;
            }

            currentPosition = currentPosition.Add(TimeSpan.FromMilliseconds(100));

            int newIndex = -1;
            for (int i = 0; i < _syncedLyrics.Count; i++)
            {
                if (currentPosition >= _syncedLyrics[i].Timestamp)
                    newIndex = i;
                else
                    break;
            }

            if (newIndex != -1 && newIndex != _currentLineIndex)
            {
                _currentLineIndex = newIndex;
                UpdateCurrentLine(newIndex);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error syncing lyrics: {ex.Message}");
        }
    }

    private void UpdateCurrentLine(int newIndex)
    {
        try
        {
            if (_syncedLyrics == null || newIndex < 0 || newIndex >= _syncedLyrics.Count)
            {
                // Before first lyric line or invalid index
                MarqueeText.Text = "♪ ♪ ♪";
                _scrollTimer.Stop();
                _needsScroll = false;
                MarqueeTranslate.X = 0;
                return;
            }

            string lineText = _syncedLyrics[newIndex].Text;

            TimeSpan duration = TimeSpan.FromSeconds(3);
            if (newIndex + 1 < _syncedLyrics.Count)
            {
                 duration = _syncedLyrics[newIndex + 1].Timestamp - _syncedLyrics[newIndex].Timestamp;
                 if (duration.TotalSeconds > 15) duration = TimeSpan.FromSeconds(15);
            }

            AnimateLineChange(lineText, duration);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error updating timeline position: {ex.Message}");
        }
    }

    private void HandleLineMeasurement()
    {
        MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = MarqueeText.DesiredSize.Width;
        _controlWidth = ActualWidth > 0 ? ActualWidth : 300;

        if (_textWidth > _controlWidth && SettingsManager.Current.LyricsAnimationMode != 2)
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

    private void AnimateLineChange(string newText, TimeSpan duration)
    {
        try
        {
            int mode = SettingsManager.Current.LyricsAnimationMode;

            if (mode == 2) // Marquee Sync
            {
                MarqueeText.BeginAnimation(OpacityProperty, null);
                MarqueeText.Opacity = _isPaused ? (SettingsManager.Current.LyricsHideWhenPaused ? 0.0 : 0.45) : 0.85;

                MarqueeText.Text = newText;
                HandleLineMeasurement();
                
                double startX = _controlWidth;
                double endX = -_textWidth;

                var syncScroll = new DoubleAnimation
                {
                    From = startX,
                    To = endX,
                    Duration = duration
                };

                MarqueeTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                MarqueeTranslate.Y = 0;
                MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, syncScroll);
            }
            else if (mode == 1) // Slide
            {
                int dir = SettingsManager.Current.LyricsSlideDirection; // 0:Left, 1:Right, 2:Up, 3:Down
                double outX = 0, inX = 0, outY = 0, inY = 0;
                
                if (dir == 0)      { outX = -_controlWidth - 50; inX = _controlWidth + 50; }
                else if (dir == 1) { outX = _controlWidth + 50; inX = -_controlWidth - 50; }
                else if (dir == 2) { outY = -ActualHeight - 20; inY = ActualHeight + 20; }
                else if (dir == 3) { outY = ActualHeight + 20; inY = -ActualHeight - 20; }

                var slideOut = new DoubleAnimation
                {
                    To = (dir < 2) ? outX : outY,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                slideOut.Completed += (s, e) =>
                {
                    MarqueeText.Text = newText;
                    HandleLineMeasurement();
                    
                    double targetX = _controlWidth > _textWidth ? (_controlWidth - _textWidth) / 2 : 0;
                    double targetY = 0;

                    MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                    MarqueeTranslate.BeginAnimation(TranslateTransform.YProperty, null);

                    if (dir < 2) 
                    {
                        MarqueeTranslate.X = inX;
                        MarqueeTranslate.Y = targetY;
                        var slideIn = new DoubleAnimation { To = targetX, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                        MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
                    }
                    else
                    {
                        MarqueeTranslate.X = targetX;
                        MarqueeTranslate.Y = inY;
                        var slideIn = new DoubleAnimation { To = targetY, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                        MarqueeTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
                    }
                };

                if (dir < 2) MarqueeTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
                else MarqueeTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
            }
            else // 0: Fade
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
                    MarqueeTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    MarqueeTranslate.X = _controlWidth > _textWidth ? (_controlWidth - _textWidth) / 2 : 0;
                    MarqueeTranslate.Y = 0;

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
            MarqueeTranslate.Y = 0;
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
