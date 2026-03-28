// Copyright © 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace FluentFlyout.Classes.Utils;

public static class MediaPlayerData
{
    private static readonly HashSet<string> NonInformativeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "io", "app", "apps", "github", "exe", "microsoft", "windows"
    };


    private class CachedMediaPlayerInfo
    {
        public string Title { get; set; }
        public ImageSource? Icon { get; set; }
    }
    // cache for media player info to avoid redundant process lookups
    private static readonly Dictionary<string, CachedMediaPlayerInfo> mediaPlayerCache = new();

    private static Process[] cachedProcesses = null;
    private static DateTime lastCacheTime = DateTime.MinValue;
    private const int CACHE_DURATION_SECONDS = 5;

    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string CreateFriendlyTitle(string mediaPlayerId)
    {
        var parts = mediaPlayerId
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Replace('-', ' ').Replace('_', ' ').Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part) && !NonInformativeWords.Contains(part))
            .ToList();

        if (parts.Count == 0)
            return mediaPlayerId;

        string selected = parts.OrderByDescending(p => p.Length).First();
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(selected.ToLowerInvariant());
    }

    public static (string, ImageSource) getMediaPlayerData(string mediaPlayerId)
    {
        if (mediaPlayerCache.TryGetValue(mediaPlayerId, out var cachedInfo))
        {
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        string mediaTitle = mediaPlayerId;
        ImageSource? mediaIcon = null;
        
        // get sanitized media title name
        string[] mediaSessionIdVariants = mediaPlayerId.Split('.');

        // remove common non-informative substrings
        var variants = mediaSessionIdVariants.Select(variant =>
            variant.Replace("com", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("github", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("exe", "", StringComparison.OrdinalIgnoreCase)
                   .Trim()
        ).Where(variant => !string.IsNullOrWhiteSpace(variant)).ToList();

        // add original id to the end of the array to ensure at least one variant
        variants.Add(mediaPlayerId);

        Process[] processes;

        // use cache to avoid frequent process enumeration
        if (cachedProcesses == null || (DateTime.Now - lastCacheTime).TotalSeconds > CACHE_DURATION_SECONDS)
        {
            cachedProcesses = Process.GetProcesses();
            lastCacheTime = DateTime.Now;
        }

        processes = cachedProcesses;

        string friendlyFallbackTitle = CreateFriendlyTitle(mediaPlayerId);
        var normalizedVariants = variants.Select(NormalizeForMatch)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        var processData = processes.Select(p =>
            {
                try
                {
                    // pre-filter processes without a main window handle
                    if (p.MainWindowHandle == IntPtr.Zero)
                    {
                        return null;
                    }

                    var mainModule = p.MainModule;
                    if (mainModule == null) return null;

                    string path = mainModule.FileName;

                    string normalizedPath = NormalizeForMatch(path);
                    string normalizedProcessName = NormalizeForMatch(p.ProcessName);
                    string normalizedWindowTitle = NormalizeForMatch(p.MainWindowTitle);
                    int score = 0;
                    bool windowTitleMatch = false;

                    foreach (var normalizedVariant in normalizedVariants)
                    {
                        if (normalizedWindowTitle.Contains(normalizedVariant))
                            windowTitleMatch = true;

                        if (normalizedProcessName.Contains(normalizedVariant))
                            score += 2;
                        if (normalizedPath.Contains(normalizedVariant))
                            score += 1;
                    }

                    if (score == 0 && !windowTitleMatch)
                        return null;

                    string fileDescription = mainModule.FileVersionInfo.FileDescription;
                    string title = !string.IsNullOrWhiteSpace(fileDescription)
                        ? fileDescription
                        : p.MainWindowTitle;

                    // Browser-hosted/PWA players often only match by window title.
                    // In that case keep friendly player name, but still use host process icon.
                    if (score == 0 && windowTitleMatch)
                        title = friendlyFallbackTitle;

                    if (string.IsNullOrWhiteSpace(title))
                        title = friendlyFallbackTitle;

                    return new { Title = title, Path = path, Score = score };
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // silently ignore the exception for inaccessible processes
                }
                return null;
            })
            .Where(data => data != null)
            .OrderByDescending(data => data!.Score)
            .FirstOrDefault(); // use best scored result

        if (processData != null)
        {
            mediaTitle = !string.IsNullOrWhiteSpace(processData.Title) ? processData.Title : mediaPlayerId;

            try
            {
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(processData.Path))
                {
                    if (icon != null)
                    {
                        mediaIcon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());

                        mediaIcon.Freeze();
                    }
                }
            }
            catch
            {
                mediaIcon = null;
            }
        }
        else
        {
            mediaTitle = friendlyFallbackTitle;
        }

        mediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
        {
            Title = mediaTitle,
            Icon = mediaIcon
        };

        return (mediaTitle, mediaIcon);
    }
}