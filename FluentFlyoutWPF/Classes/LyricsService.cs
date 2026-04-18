// Copyright © 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Represents a single timed lyric line from LRC format.
/// </summary>
public sealed class LyricLine
{
    public TimeSpan Timestamp { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Fetches and parses song lyrics from the LRCLIB API (https://lrclib.net).
/// Free, open-source, no API key required.
/// </summary>
public sealed class LyricsService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        BaseAddress = new Uri("https://lrclib.net/")
    };

    // LRC timestamp pattern: [mm:ss.xx]
    private static readonly Regex LrcTimestampRegex = new(@"\[(\d{2}):(\d{2})\.(\d{2})\]\s?(.*)", RegexOptions.Compiled);

    // Cache to avoid redundant requests for the same song
    private string _cachedKey = string.Empty;
    private List<LyricLine>? _cachedSyncedLyrics;
    private string? _cachedPlainLyrics;
    private bool _cacheIsValid;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    // Cancellation support: replaced atomically on each new fetch request
    private CancellationTokenSource _fetchCts = new();

    private bool _disposed;

    static LyricsService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout/1.0 (https://github.com/FluentFlyout)");
    }

    /// <summary>
    /// Gets synced (timed) lyrics for the given track. Returns null if not available.
    /// </summary>
    public async Task<List<LyricLine>?> GetSyncedLyricsAsync(string trackName, string artistName, int durationSeconds = 0)
    {
        await FetchIfNeeded(trackName, artistName, durationSeconds);
        return _cachedSyncedLyrics;
    }

    /// <summary>
    /// Gets plain lyrics as fallback. Returns null if not available.
    /// </summary>
    public async Task<string?> GetPlainLyricsAsync(string trackName, string artistName, int durationSeconds = 0)
    {
        await FetchIfNeeded(trackName, artistName, durationSeconds);
        return _cachedPlainLyrics;
    }

    private async Task FetchIfNeeded(string trackName, string artistName, int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(trackName) || trackName == "-")
            return;

        string key = $"{trackName}||{artistName}".ToLowerInvariant();

        // Cancel only when requesting a different track key.
        // This avoids cross-cancel behavior between the sequential
        // GetSyncedLyricsAsync/GetPlainLyricsAsync calls for the same track.
        if (!string.IsNullOrEmpty(_cachedKey) && !string.Equals(_cachedKey, key, StringComparison.Ordinal))
        {
            var oldCts = Interlocked.Exchange(ref _fetchCts, new CancellationTokenSource());
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var token = _fetchCts.Token;

        await _cacheLock.WaitAsync();
        try
        {
            if (key == _cachedKey && _cacheIsValid)
                return;

            _cachedKey = key;
            _cacheIsValid = false;
            _cachedSyncedLyrics = null;
            _cachedPlainLyrics = null;

            try
            {
                string encodedTrack = Uri.EscapeDataString(trackName);
                string encodedArtist = Uri.EscapeDataString(artistName ?? string.Empty);

                string url = $"api/get?track_name={encodedTrack}&artist_name={encodedArtist}";
                if (durationSeconds > 0)
                    url += $"&duration={durationSeconds}";

                using var response = await _httpClient.GetAsync(url, token);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Debug($"LRCLIB returned {response.StatusCode} for '{trackName}' by '{artistName}'");
                    _cacheIsValid = true;
                    return;
                }

                string json = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Try synced lyrics first (preferred for time-sync)
                if (root.TryGetProperty("syncedLyrics", out var syncedProp) && syncedProp.ValueKind == JsonValueKind.String)
                {
                    string? synced = syncedProp.GetString();
                    if (!string.IsNullOrWhiteSpace(synced))
                    {
                        _cachedSyncedLyrics = ParseLrc(synced);
                    }
                }

                // Also get plain lyrics as fallback
                if (root.TryGetProperty("plainLyrics", out var plainProp) && plainProp.ValueKind == JsonValueKind.String)
                {
                    _cachedPlainLyrics = plainProp.GetString();
                }

                _cacheIsValid = true;
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == token)
            {
                // A newer track was requested while this fetch was in-flight — expected, not an error.
                Logger.Debug($"Lyrics fetch cancelled for '{trackName}' by '{artistName}' (newer track requested)");
                // Leave _cacheIsValid = false so the next caller (new track) proceeds with its own fetch.
            }
            catch (TaskCanceledException)
            {
                // HttpClient.Timeout fired — not our explicit cancellation.
                Logger.Warn($"LRCLIB request timed out for '{trackName}' by '{artistName}'");
                throw;
            }
            catch (HttpRequestException ex)
            {
                Logger.Warn(ex, $"LRCLIB request failed for '{trackName}' by '{artistName}'");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Unexpected error fetching lyrics for '{trackName}' by '{artistName}'");
                throw;
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Parses LRC formatted text into a list of timed lyric lines.
    /// </summary>
    private static List<LyricLine> ParseLrc(string lrcText)
    {
        var lines = new List<LyricLine>();

        foreach (string rawLine in lrcText.Split('\n'))
        {
            var match = LrcTimestampRegex.Match(rawLine.Trim());
            if (!match.Success)
                continue;

            int minutes = int.Parse(match.Groups[1].Value);
            int seconds = int.Parse(match.Groups[2].Value);
            int centiseconds = int.Parse(match.Groups[3].Value);
            string text = match.Groups[4].Value.Trim();

            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add(new LyricLine
            {
                Timestamp = new TimeSpan(0, 0, minutes, seconds, centiseconds * 10),
                Text = text
            });
        }

        // Sort by timestamp
        lines.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return lines;
    }

    public void ClearCache()
    {
        // Cancel any in-flight request before clearing
        _fetchCts.Cancel();

        _cacheLock.Wait();
        try
        {
            _cachedKey = string.Empty;
            _cachedSyncedLyrics = null;
            _cachedPlainLyrics = null;
            _cacheIsValid = false;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fetchCts.Cancel();
        _fetchCts.Dispose();
        _cacheLock.Dispose();
    }
}
