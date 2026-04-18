// Copyright © 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using MicaWPF.Core.Enums;
using MicaWPF.Core.Helpers;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Media.Control;

namespace FluentFlyout.Controls;

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

    private readonly DispatcherTimer _scrollTimer;
    private double _textWidth;
    private double _controlWidth;
    private bool _needsScroll;

    private bool _isTextAActive = true;

    private class RibbonItemData
    {
        public double Width { get; set; }
        public double CenterX { get; set; }
    }
    private List<RibbonItemData>? _ribbonItems;
    private double _ribbonTotalWidth;

    // Dirty flag: set whenever ribbon content or layout-affecting settings change.
    // Prevents calling Measure() inside the 16ms scroll tick unnecessarily.
    private bool _ribbonLayoutDirty = true;

    // Stored handler reference so we can correctly unsubscribe on Unloaded.
    private readonly PropertyChangedEventHandler _settingsChangedHandler;

    public bool HasLyrics => _hasContent;

    public LyricsMarqueeControl()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;

        _settingsChangedHandler = OnSettingsPropertyChanged;
        SettingsManager.Current.PropertyChanged += _settingsChangedHandler;

        // Unsubscribe when the control is unloaded to prevent handler accumulation
        // if the control is created and destroyed multiple times.
        Unloaded += (_, _) => SettingsManager.Current.PropertyChanged -= _settingsChangedHandler;

        ApplyWindowsTheme();

        _syncTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _syncTimer.Tick += SyncTimer_Tick;

        _scrollTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += ScrollTimer_Tick;

        SizeChanged += (s, e) => { _controlWidth = ActualWidth; };
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsManager.Current.LyricsTextColor))
        {
            ApplyWindowsTheme();
        }
        else if (e.PropertyName == nameof(SettingsManager.Current.LyricsFontSize) ||
                 e.PropertyName == nameof(SettingsManager.Current.LyricsFontFamily) ||
                 e.PropertyName == nameof(SettingsManager.Current.LyricsContinuousScrollDirection))
        {
            // Mark the ribbon layout as stale so EnsureRibbonLayout rebuilds on next tick.
            _ribbonLayoutDirty = true;

            if (_syncedLyrics != null && SettingsManager.Current.LyricsAnimationMode == 2)
            {
                BuildRibbon();
            }
        }
    }

    public void ApplyWindowsTheme()
    {
        string hex = SettingsManager.Current.LyricsTextColor;
        Brush brush;
        if (string.IsNullOrWhiteSpace(hex) || hex.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            bool isDark = WindowsThemeHelper.GetCurrentWindowsTheme() == WindowsTheme.Dark;
            brush = new SolidColorBrush(isDark ? Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
        }
        else
        {
            try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch
            {
                bool isDark = WindowsThemeHelper.GetCurrentWindowsTheme() == WindowsTheme.Dark;
                brush = new SolidColorBrush(isDark ? Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
            }
        }

        MarqueeTextA.Foreground = brush;
        MarqueeTextB.Foreground = brush;

        if (RibbonPanel != null)
        {
            foreach (UIElement child in RibbonPanel.Children)
            {
                if (child is TextBlock tb) tb.Foreground = brush;
            }
        }
    }

    private void BuildRibbon()
    {
        RibbonPanel.Children.Clear();
        _ribbonItems = new List<RibbonItemData>();
        _ribbonTotalWidth = 0;
        _ribbonLayoutDirty = true;

        if (_syncedLyrics == null) return;

        bool isRtl = SettingsManager.Current.LyricsContinuousScrollDirection == 1;
        double marginValue = 75.0;
        double cumulativeX = 0;

        var textBlocks = new List<TextBlock>();
        foreach (var line in _syncedLyrics)
        {
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                Margin = new Thickness(0, 0, marginValue, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding("LyricsFontSize") { Source = SettingsManager.Current });
            tb.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding("LyricsFontFamily") { Source = SettingsManager.Current });
            textBlocks.Add(tb);
        }

        if (isRtl)
        {
            for (int i = textBlocks.Count - 1; i >= 0; i--) RibbonPanel.Children.Add(textBlocks[i]);
        }
        else
        {
            foreach (var tb in textBlocks) RibbonPanel.Children.Add(tb);
        }

        ApplyWindowsTheme();
        RibbonPanel.UpdateLayout();

        var tempItems = new List<RibbonItemData>();
        foreach (FrameworkElement child in RibbonPanel.Children)
        {
            if (child.DesiredSize.Width == 0 && child.ActualWidth == 0)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            double width = child.ActualWidth > 0 ? child.ActualWidth : child.DesiredSize.Width;

            tempItems.Add(new RibbonItemData
            {
                Width = width,
                CenterX = cumulativeX + (width / 2.0)
            });
            cumulativeX += width + marginValue;
        }

        if (isRtl)
        {
            for (int i = tempItems.Count - 1; i >= 0; i--) _ribbonItems.Add(tempItems[i]);
        }
        else
        {
            _ribbonItems.AddRange(tempItems);
        }

        _ribbonTotalWidth = cumulativeX - marginValue;
        _ribbonLayoutDirty = false;

        if (SettingsManager.Current.LyricsAnimationMode == 2)
        {
            UpdateContinuousScroll(true);
        }
    }

    public void SetSyncedLyrics(List<LyricLine>? lyrics, GlobalSystemMediaTransportControlsSession? session)
    {
        Dispatcher.Invoke(() =>
        {
            _syncedLyrics = lyrics;
            _plainLyrics = null;
            _session = session;
            _currentLineIndex = -1;
            _ribbonLayoutDirty = true;

            RibbonCanvas.Visibility = Visibility.Collapsed;
            SingleLineGrid.Visibility = Visibility.Collapsed;
            RibbonTranslate.BeginAnimation(TranslateTransform.XProperty, null);

            if (lyrics == null || lyrics.Count == 0 || session == null)
            {
                _hasContent = false;
                _syncTimer.Stop();
                _scrollTimer.Stop();
                MarqueeTextA.Text = string.Empty;
                MarqueeTextB.Text = string.Empty;
                return;
            }

            _hasContent = true;
            BuildRibbon();

            if (!_isPaused)
            {
                _syncTimer.Start();
                _scrollTimer.Start();
            }
            UpdateCurrentLine();
        });
    }

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
                MarqueeTextB.Text = string.Empty;
                RibbonCanvas.Visibility = Visibility.Collapsed;
                SingleLineGrid.Visibility = Visibility.Collapsed;
                return;
            }

            SingleLineGrid.Visibility = Visibility.Visible;
            RibbonCanvas.Visibility = Visibility.Collapsed;

            _plainLyrics = lyrics.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n\n", " ★ ").Replace("\n", " ◆ ");
            string displayText = _plainLyrics + "          ★          " + _plainLyrics + "          ★          ";

            TextBlock activeBlock = _isTextAActive ? MarqueeTextA : MarqueeTextB;
            activeBlock.Opacity = 0.85;
            activeBlock.Text = displayText;

            HandleLineMeasurement(activeBlock);

            TranslateTransform activeTrans = _isTextAActive ? MarqueeTranslateA : MarqueeTranslateB;
            activeTrans.BeginAnimation(TranslateTransform.XProperty, null);
            activeTrans.X = _controlWidth;

            _hasContent = true;
            if (!_isPaused) _scrollTimer.Start();
        });
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        Dispatcher.Invoke(() =>
        {
            if (paused)
            {
                _syncTimer.Stop();
                _scrollTimer.Stop();
                double op = SettingsManager.Current.LyricsHideWhenPaused ? 0.0 : 0.45;

                MarqueeTextA.BeginAnimation(OpacityProperty, null);
                MarqueeTextB.BeginAnimation(OpacityProperty, null);

                MarqueeTextA.Opacity = _isTextAActive ? op : 0;
                MarqueeTextB.Opacity = !_isTextAActive ? op : 0;

                if (SettingsManager.Current.LyricsAnimationMode == 2 && _syncedLyrics != null)
                {
                    RibbonPanel.Opacity = 1.0;
                    UpdateContinuousScroll(true);
                }
                else
                {
                    RibbonPanel.Opacity = op;
                }
            }
            else if (_hasContent)
            {
                if (_syncedLyrics != null)
                {
                    _syncTimer.Start();
                    if (SettingsManager.Current.LyricsAnimationMode == 2) _scrollTimer.Start();
                }
                else if (_plainLyrics != null)
                {
                    _scrollTimer.Start();
                }

                MarqueeTextA.BeginAnimation(OpacityProperty, null);
                MarqueeTextB.BeginAnimation(OpacityProperty, null);

                MarqueeTextA.Opacity = _isTextAActive ? 0.85 : 0;
                MarqueeTextB.Opacity = !_isTextAActive ? 0.85 : 0;
                RibbonPanel.Opacity = SettingsManager.Current.LyricsAnimationMode == 2 ? 1.0 : 0.85;
            }
        });
    }

    public void Clear()
    {
        SetSyncedLyrics(null, null);
    }

    private void SyncTimer_Tick(object? sender, EventArgs e)
    {
        if (!_hasContent || _session == null || _syncedLyrics == null) return;

        if (!SettingsManager.Current.LyricsMarqueeEnabled)
        {
            _syncTimer.Stop();
            SingleLineGrid.Visibility = Visibility.Collapsed;
            RibbonCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateCurrentLine();
    }

    private TimeSpan GetExtrapolatedPosition()
    {
        if (_session == null) return TimeSpan.Zero;
        try
        {
            var timeline = _session.GetTimelineProperties();
            if (timeline == null) return TimeSpan.Zero;

            var playbackInfo = _session.GetPlaybackInfo();
            TimeSpan currentPosition = timeline.Position;

            if (playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                TimeSpan elapsedSinceUpdate = DateTimeOffset.Now - timeline.LastUpdatedTime;
                // Guard against unreasonably large elapsed times (e.g. after system sleep).
                if (elapsedSinceUpdate.TotalHours < 1)
                    currentPosition += elapsedSinceUpdate;
            }

            return currentPosition.Add(TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error extrapolating position: {ex.Message}");
            return TimeSpan.Zero;
        }
    }

    private void UpdateCurrentLine()
    {
        if (_syncedLyrics == null || _session == null) return;
        try
        {
            TimeSpan currentPosition = GetExtrapolatedPosition();

            int newIndex = -1;
            for (int i = 0; i < _syncedLyrics.Count; i++)
            {
                if (currentPosition >= _syncedLyrics[i].Timestamp) newIndex = i;
                else break;
            }

            if (newIndex != -1 && newIndex != _currentLineIndex)
            {
                _currentLineIndex = newIndex;
                UpdateCurrentLine(newIndex, currentPosition);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error syncing lyrics: {ex.Message}");
        }
    }

    private void UpdateCurrentLine(int newIndex, TimeSpan currentPosition)
    {
        try
        {
            if (_syncedLyrics == null || newIndex < 0 || newIndex >= _syncedLyrics.Count)
            {
                _scrollTimer.Stop();
                _needsScroll = false;
                return;
            }
            string lineText = _syncedLyrics[newIndex].Text;
            AnimateLineChange(lineText, currentPosition, newIndex);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error updating timeline position: {ex.Message}");
        }
    }

    private void HandleLineMeasurement(TextBlock targetBlock)
    {
        targetBlock.TextWrapping = TextWrapping.NoWrap;
        targetBlock.Width = double.NaN;
        targetBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = targetBlock.DesiredSize.Width;
        _controlWidth = ActualWidth > 0 ? ActualWidth : 300;

        if (_syncedLyrics == null)
        {
            targetBlock.Width = _textWidth;
            if (_textWidth > _controlWidth)
            {
                _needsScroll = true;
                if (!_isPaused) _scrollTimer.Start();
            }
            else
            {
                _needsScroll = false;
                _scrollTimer.Stop();
            }
        }
        else
        {
            targetBlock.TextWrapping = TextWrapping.Wrap;
            targetBlock.Width = double.NaN;
            _needsScroll = false;
            _scrollTimer.Stop();
        }
    }

    private void AnimateLineChange(string newText, TimeSpan currentPosition, int? index = null)
    {
        try
        {
            int mode = SettingsManager.Current.LyricsAnimationMode;
            double targetOpacity = _isPaused ? (SettingsManager.Current.LyricsHideWhenPaused ? 0.0 : 0.45) : 0.85;

            TextBlock activeBlock = _isTextAActive ? MarqueeTextA : MarqueeTextB;
            TextBlock inactiveBlock = _isTextAActive ? MarqueeTextB : MarqueeTextA;
            TranslateTransform activeTrans = _isTextAActive ? MarqueeTranslateA : MarqueeTranslateB;
            TranslateTransform inactiveTrans = _isTextAActive ? MarqueeTranslateB : MarqueeTranslateA;

            if (mode == 2 && RibbonPanel.Children.Count > 0 && index.HasValue && _syncedLyrics != null)
            {
                SingleLineGrid.Visibility = Visibility.Collapsed;
                RibbonCanvas.Visibility = Visibility.Visible;
                RibbonPanel.Opacity = 1.0;
                RibbonTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                return;
            }
            else
            {
                RibbonCanvas.Visibility = Visibility.Collapsed;
                SingleLineGrid.Visibility = Visibility.Visible;

                inactiveBlock.Text = newText;
                HandleLineMeasurement(inactiveBlock);

                if (mode == 1)
                {
                    int dir = SettingsManager.Current.LyricsSlideDirection;
                    double outX = 0, inX = 0, outY = 0, inY = 0;

                    if (dir == 0) { outX = -_controlWidth - 50; inX = _controlWidth + 50; }
                    else if (dir == 1) { outX = _controlWidth + 50; inX = -_controlWidth - 50; }
                    else if (dir == 2) { outY = -ActualHeight - 20; inY = ActualHeight + 20; }
                    else if (dir == 3) { outY = ActualHeight + 20; inY = -ActualHeight - 20; }

                    var slideOutX = new DoubleAnimation { To = outX, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                    var slideOutY = new DoubleAnimation { To = outY, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                    var fadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(150), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

                    if (dir < 2) activeTrans.BeginAnimation(TranslateTransform.XProperty, slideOutX);
                    else activeTrans.BeginAnimation(TranslateTransform.YProperty, slideOutY);
                    activeBlock.BeginAnimation(OpacityProperty, fadeOut);

                    inactiveTrans.BeginAnimation(TranslateTransform.XProperty, null);
                    inactiveTrans.BeginAnimation(TranslateTransform.YProperty, null);
                    inactiveTrans.X = dir < 2 ? inX : 0;
                    inactiveTrans.Y = dir < 2 ? 0 : inY;

                    var slideInX = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    var slideInY = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(400), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    var fadeIn = new DoubleAnimation { To = targetOpacity, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

                    if (dir < 2) inactiveTrans.BeginAnimation(TranslateTransform.XProperty, slideInX);
                    else inactiveTrans.BeginAnimation(TranslateTransform.YProperty, slideInY);
                    inactiveBlock.BeginAnimation(OpacityProperty, fadeIn);
                }
                else
                {
                    var fadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(150), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    activeBlock.BeginAnimation(OpacityProperty, fadeOut);

                    inactiveTrans.BeginAnimation(TranslateTransform.XProperty, null);
                    inactiveTrans.BeginAnimation(TranslateTransform.YProperty, null);
                    inactiveTrans.X = 0; inactiveTrans.Y = 0;

                    var fadeIn = new DoubleAnimation { To = targetOpacity, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                    inactiveBlock.BeginAnimation(OpacityProperty, fadeIn);
                }

                _isTextAActive = !_isTextAActive;
            }
        }
        catch
        {
            TextBlock inactiveBlock = _isTextAActive ? MarqueeTextB : MarqueeTextA;
            inactiveBlock.Text = newText;
            HandleLineMeasurement(inactiveBlock);
            inactiveBlock.Opacity = 0.85;
            _isTextAActive = !_isTextAActive;
        }
    }

    /// <summary>
    /// Ensures ribbon layout data is valid before using it in the scroll tick.
    /// Uses a dirty flag to avoid expensive Measure() calls every 16ms frame.
    /// </summary>
    private void EnsureRibbonLayout()
    {
        if (_syncedLyrics == null || RibbonPanel.Children.Count == 0) return;

        bool needsRebuild = _ribbonLayoutDirty
            || _ribbonItems == null
            || _ribbonItems.Count != _syncedLyrics.Count;

        // Guard: if widths are still zero after an earlier build, rebuild once.
        if (!needsRebuild && _ribbonItems!.Count > 0 && _ribbonItems[0].Width <= 0.1)
            needsRebuild = true;

        if (needsRebuild)
            RebuildLayoutCache();
    }

    private void RebuildLayoutCache()
    {
        bool isRtl = SettingsManager.Current.LyricsContinuousScrollDirection == 1;
        double marginValue = 75.0;
        double cumulativeX = 0;

        var tempItems = new List<RibbonItemData>();
        foreach (FrameworkElement child in RibbonPanel.Children)
        {
            if (child.ActualWidth == 0 && child.DesiredSize.Width == 0)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            double width = child.ActualWidth > 0 ? child.ActualWidth : child.DesiredSize.Width;

            tempItems.Add(new RibbonItemData
            {
                Width = width,
                CenterX = cumulativeX + (width / 2.0)
            });
            cumulativeX += width + marginValue;
        }

        _ribbonItems = new List<RibbonItemData>();
        if (isRtl)
        {
            for (int i = tempItems.Count - 1; i >= 0; i--) _ribbonItems.Add(tempItems[i]);
        }
        else
        {
            _ribbonItems.AddRange(tempItems);
        }

        _ribbonTotalWidth = cumulativeX - marginValue;
        _ribbonLayoutDirty = false;
    }

    private void UpdateContinuousScroll(bool snapToCurrent = false)
    {
        if (_syncedLyrics == null || _session == null) return;
        EnsureRibbonLayout();
        if (_ribbonItems == null || _ribbonItems.Count == 0 || RibbonPanel.Children.Count == 0) return;

        TimeSpan pos = GetExtrapolatedPosition();

        int idx = 0;
        for (int i = 0; i < _syncedLyrics.Count; i++)
        {
            if (pos >= _syncedLyrics[i].Timestamp) idx = i; else break;
        }

        int nextIdx = idx + 1 < _syncedLyrics.Count ? idx + 1 : idx;

        TimeSpan lineStart = _syncedLyrics[idx].Timestamp;
        TimeSpan nextLineStart = _syncedLyrics[nextIdx].Timestamp;
        TimeSpan dur = nextLineStart - lineStart;

        double currCenter = _ribbonItems[idx].CenterX;
        double nextCenter = _ribbonItems[nextIdx].CenterX;

        double canvasWidth = ActualWidth > 0 ? ActualWidth : 300;
        double currX = (canvasWidth / 2.0) - currCenter;
        double nextX = idx == nextIdx ? currX : ((canvasWidth / 2.0) - nextCenter);

        double progress = 0;

        if (dur.TotalMilliseconds > 0 && idx != nextIdx && !snapToCurrent && !_isPaused)
        {
            double elapsedMs = (pos - lineStart).TotalMilliseconds;
            double totalMs = dur.TotalMilliseconds;

            progress = elapsedMs / totalMs;
        }

        progress = Math.Clamp(progress, 0, 1);

        RibbonTranslate.X = currX + (nextX - currX) * progress;

        UpdateRibbonOpacities(idx, nextIdx, progress);
    }

    private void UpdateRibbonOpacities(int currentIdx, int nextIdx, double progress)
    {
        double activeOpacity = _isPaused ? (SettingsManager.Current.LyricsHideWhenPaused ? 0.0 : 0.45) : 0.85;
        double inactiveOpacity = activeOpacity * 0.4;
        bool isRtl = SettingsManager.Current.LyricsContinuousScrollDirection == 1;

        for (int i = 0; i < _ribbonItems!.Count; i++)
        {
            int childIndex = isRtl ? (_ribbonItems.Count - 1 - i) : i;

            if (childIndex >= 0 && childIndex < RibbonPanel.Children.Count)
            {
                if (RibbonPanel.Children[childIndex] is UIElement element)
                {
                    double targetOpacity = inactiveOpacity;
                    if (i == currentIdx) targetOpacity = inactiveOpacity + (activeOpacity - inactiveOpacity) * (1 - progress);
                    else if (i == nextIdx) targetOpacity = inactiveOpacity + (activeOpacity - inactiveOpacity) * progress;

                    if (Math.Abs(element.Opacity - targetOpacity) > 0.01)
                    {
                        element.Opacity = targetOpacity;
                    }
                }
            }
        }
    }

    private void ScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_hasContent || !SettingsManager.Current.LyricsMarqueeEnabled)
        {
            _scrollTimer.Stop();
            return;
        }

        if (SettingsManager.Current.LyricsAnimationMode == 2 && _syncedLyrics != null)
        {
            UpdateContinuousScroll();
            return;
        }

        if (_isPaused) return;

        int speed = Math.Clamp(SettingsManager.Current.LyricsMarqueeSpeed, 1, 5);
        if (_syncedLyrics == null && _plainLyrics != null && _needsScroll)
        {
            double moveSpeed = speed * (16.0 / 33.0);
            TranslateTransform activeTrans = _isTextAActive ? MarqueeTranslateA : MarqueeTranslateB;
            activeTrans.X -= moveSpeed;
            if (activeTrans.X < -(_controlWidth + _textWidth) / 2)
            {
                activeTrans.X = (_controlWidth + _textWidth) / 2;
            }
        }
    }
}