// Copyright © 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Media.Control;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyout.Windows;

/// <summary>
/// A transparent window embedded in the taskbar that shows scrolling lyrics.
/// Positioned to the right side of the taskbar, left of the system tray.
/// </summary>
public partial class LyricsWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly DispatcherTimer _positionTimer;
    private readonly int _lyricsWidth = 300;
    private IntPtr _trayHandle;
    private bool _positionUpdateInProgress;

    public LyricsWindow()
    {
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        // Use a named method so OnClosed can correctly unsubscribe the exact same delegate.
        _positionTimer.Tick += OnPositionTimerTick;
        _positionTimer.Start();

        Show();
    }

    /// <summary>Named tick handler stored to allow correct unsubscription in OnClosed.</summary>
    private void OnPositionTimerTick(object? sender, EventArgs e) => UpdatePosition();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource source = (HwndSource)PresentationSource.FromDependencyObject(this);
        source.AddHook(WindowProc);
        SetupWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Stop the timer before the window handle becomes invalid,
        // preventing spurious ticks after Close() is called.
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        base.OnClosed(e);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x003D: // WM_GETOBJECT
            case 0x0018: // WM_SHOWWINDOW
            case 0x0046: // WM_WINDOWPOSCHANGING
            case 0x0083: // WM_NCCALCSIZE
            case 0x0281: // WM_IME_SETCONTEXT
            case 0x0282: // WM_IME_NOTIFY
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Updates with time-synced lyrics bound to a media session.
    /// </summary>
    public void UpdateSyncedLyrics(List<LyricLine>? lyrics, GlobalSystemMediaTransportControlsSession? session)
    {
        Dispatcher.BeginInvoke(() =>
        {
            EnsurePositionTimerState();
            if (!SettingsManager.Current.LyricsMarqueeEnabled)
            {
                Visibility = Visibility.Collapsed;
                MarqueeWidget.Clear();
                return;
            }

            MarqueeWidget.SetSyncedLyrics(lyrics, session);
            if (lyrics != null && lyrics.Count > 0)
                Visibility = Visibility.Visible;
            else
                Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Updates with plain (non-synced) lyrics as a scrolling fallback.
    /// </summary>
    public void UpdatePlainLyrics(string? lyrics)
    {
        Dispatcher.BeginInvoke(() =>
        {
            EnsurePositionTimerState();
            if (!SettingsManager.Current.LyricsMarqueeEnabled)
            {
                Visibility = Visibility.Collapsed;
                MarqueeWidget.Clear();
                return;
            }

            MarqueeWidget.SetPlainLyrics(lyrics);
            if (!string.IsNullOrWhiteSpace(lyrics))
                Visibility = Visibility.Visible;
            else
                Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Hides the lyrics window and clears content.
    /// </summary>
    public void ClearLyrics()
    {
        Dispatcher.BeginInvoke(() =>
        {
            EnsurePositionTimerState();
            MarqueeWidget.Clear();
            Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Pauses or resumes the lyrics marquee based on playback state.
    /// </summary>
    public void SetPaused(bool paused)
    {
        Dispatcher.BeginInvoke(() =>
        {
            EnsurePositionTimerState();
            MarqueeWidget.SetPaused(paused);
        });
    }

    private IntPtr GetMainTaskbarHandle()
    {
        return FindWindow("Shell_TrayWnd", null);
    }

    private void SetupWindow()
    {
        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr windowHandle = interop.Handle;

            IntPtr taskbarHandle = GetMainTaskbarHandle();

            // Make this a child of the taskbar
            int style = GetWindowLong(windowHandle, GWL_STYLE);
            style = (style & ~WS_POPUP) | WS_CHILD;
            SetWindowLong(windowHandle, GWL_STYLE, style);

            SetParent(windowHandle, taskbarHandle);

            CalculateAndSetPosition(taskbarHandle, windowHandle);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Lyrics Window error during setup");
        }
    }

    private void UpdatePosition()
    {
        EnsurePositionTimerState();
        if (!SettingsManager.Current.LyricsMarqueeEnabled || SettingsManager.Current.LyricsDisplayMode == 1)
        {
            Dispatcher.Invoke(() => Visibility = Visibility.Collapsed);
            return;
        }

        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarHandle = GetMainTaskbarHandle();

            if (interop.Handle == IntPtr.Zero)
                return;

            if (GetParent(interop.Handle) != taskbarHandle)
                SetParent(interop.Handle, taskbarHandle);

            if (taskbarHandle != IntPtr.Zero && interop.Handle != IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    CalculateAndSetPosition(taskbarHandle, interop.Handle);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Lyrics Window error during position update");
        }
    }

    private void EnsurePositionTimerState()
    {
        bool shouldRun = SettingsManager.Current.LyricsMarqueeEnabled && SettingsManager.Current.LyricsDisplayMode != 1;
        if (shouldRun)
        {
            if (!_positionTimer.IsEnabled)
                _positionTimer.Start();
        }
        else
        {
            if (_positionTimer.IsEnabled)
                _positionTimer.Stop();
        }
    }

    private void CalculateAndSetPosition(IntPtr taskbarHandle, IntPtr windowHandle)
    {
        if (_positionUpdateInProgress)
            return;
        _positionUpdateInProgress = true;

        try
        {
            double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;
            if (dpiScale <= 0)
                return;

            GetWindowRect(taskbarHandle, out RECT taskbarRect);

            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

            int lyricsPhysicalWidth = (int)(_lyricsWidth * dpiScale);
            int lyricsLeft;

            if (_trayHandle == IntPtr.Zero)
                _trayHandle = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);

            if (_trayHandle != IntPtr.Zero)
            {
                GetWindowRect(_trayHandle, out RECT trayRect);
                lyricsLeft = trayRect.Left - taskbarRect.Left - lyricsPhysicalWidth - 8;
            }
            else
            {
                lyricsLeft = taskbarWidth - lyricsPhysicalWidth - 250;
            }

            lyricsLeft = Math.Max(lyricsLeft, taskbarWidth / 3);

            POINT containerPos = new() { X = taskbarRect.Left, Y = taskbarRect.Top };
            ScreenToClient(taskbarHandle, ref containerPos);

            int containerWidth = taskbarWidth;
            int containerHeight = taskbarHeight;

            SetWindowPos(windowHandle, 0,
                containerPos.X, containerPos.Y,
                containerWidth, containerHeight,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);

            int widgetTop = (taskbarHeight - (int)(40 * dpiScale)) / 2;

            Canvas.SetLeft(MarqueeWidget, lyricsLeft / dpiScale);
            Canvas.SetTop(MarqueeWidget, widgetTop / dpiScale);
            MarqueeWidget.Width = lyricsPhysicalWidth / dpiScale;
            MarqueeWidget.Height = (int)(40 * dpiScale) / dpiScale;

            Rect widgetRect = new Rect(
                Canvas.GetLeft(MarqueeWidget) * dpiScale,
                Canvas.GetTop(MarqueeWidget) * dpiScale,
                MarqueeWidget.Width * dpiScale,
                MarqueeWidget.Height * dpiScale);

            UpdateWindowRegion(windowHandle, widgetRect);
        }
        finally
        {
            _positionUpdateInProgress = false;
        }
    }

    private void UpdateWindowRegion(IntPtr windowHandle, Rect rect)
    {
        if (rect == Rect.Empty)
            return;

        IntPtr rgn = CreateRectRgn((int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom);
        if (rgn == IntPtr.Zero)
        {
            Logger.Error("Lyrics Window error during CreateRectRgn");
            return;
        }

        if (SetWindowRgn(windowHandle, rgn, true) == 0)
        {
            Logger.Error("Lyrics Window error during SetWindowRgn");
            DeleteObject(rgn);
        }
    }
}