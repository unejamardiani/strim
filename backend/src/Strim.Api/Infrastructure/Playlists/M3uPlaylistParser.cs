using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Strim.Api.Application.Playlists;
using Strim.Api.Domain;

namespace Strim.Api.Infrastructure.Playlists;

public class M3uPlaylistParser(ILogger<M3uPlaylistParser> logger) : IPlaylistParser
{
    private static readonly Regex AttributeRegex = new("(?<key>[\\w-]+)=\"(?<value>.*?)\"", RegexOptions.Compiled);

    public async Task ParseAsync(Stream stream, Playlist playlist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(playlist);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        logger.LogDebug("Starting playlist parse for source {Source}", playlist.Source);

        string? pendingExtinf = null;
        Dictionary<string, string>? pendingAttributes = null;
        string? pendingDisplayName = null;
        var lineNumber = 0;
        var seenHeader = false;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawLine = await reader.ReadLineAsync().ConfigureAwait(false);
            lineNumber++;

            if (rawLine is null)
            {
                break;
            }

            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!seenHeader)
            {
                if (!line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Playlist is missing the #EXTM3U header");
                    throw new PlaylistParseException("Playlist is missing #EXTM3U header");
                }

                seenHeader = true;
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pendingExtinf = rawLine;
                (pendingAttributes, pendingDisplayName) = ParseExtinf(line);
                continue;
            }

            if (line.StartsWith('#'))
            {
                // Ignore other directives for now (e.g., #EXTGRP, #EXTVLCOPT).
                continue;
            }

            if (pendingExtinf is null || pendingAttributes is null)
            {
                logger.LogWarning("Encountered URL without EXTINF at line {Line}", lineNumber);
                throw new PlaylistParseException($"Stream URL encountered without preceding EXTINF at line {lineNumber}");
            }

            var channelName = ResolveChannelName(pendingAttributes, pendingDisplayName, line);
            var tvgId = GetOptional(pendingAttributes, "tvg-id");
            var tvgName = GetOptional(pendingAttributes, "tvg-name");
            var groupTitle = GetOptional(pendingAttributes, "group-title");
            var tvgLogo = GetOptional(pendingAttributes, "tvg-logo");

            var channel = Channel.Create(channelName, line, pendingExtinf, groupTitle, tvgId, tvgName, tvgLogo);
            playlist.AddChannel(channel);

            pendingExtinf = null;
            pendingAttributes = null;
            pendingDisplayName = null;
        }

        if (pendingExtinf is not null)
        {
            logger.LogWarning("Playlist ended before URL for final EXTINF entry");
            throw new PlaylistParseException("Playlist ended before providing a stream URL for the final EXTINF entry");
        }

        if (!seenHeader)
        {
            logger.LogWarning("Playlist did not contain an #EXTM3U header");
            throw new PlaylistParseException("Playlist did not contain an #EXTM3U header");
        }

        logger.LogInformation("Parsed {ChannelCount} channels from playlist {PlaylistId}", playlist.Channels.Count, playlist.Id);
    }

    private static (Dictionary<string, string> Attributes, string? DisplayName) ParseExtinf(string line)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex < 0)
        {
            throw new PlaylistParseException("Invalid EXTINF line: missing colon");
        }

        var metadata = line[(colonIndex + 1)..];
        string? displayName = null;
        string attributeSegment = metadata;

        var commaIndex = metadata.IndexOf(',');
        if (commaIndex >= 0)
        {
            attributeSegment = metadata[..commaIndex];
            displayName = metadata[(commaIndex + 1)..].Trim();
        }

        var attributes = AttributeRegex
            .Matches(attributeSegment)
            .Cast<Match>()
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value, StringComparer.OrdinalIgnoreCase);

        return (attributes, string.IsNullOrWhiteSpace(displayName) ? null : displayName);
    }

    private static string ResolveChannelName(Dictionary<string, string> attributes, string? displayName, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        if (attributes.TryGetValue("tvg-name", out var tvgName) && !string.IsNullOrWhiteSpace(tvgName))
        {
            return tvgName.Trim();
        }

        if (attributes.TryGetValue("tvg-id", out var tvgId) && !string.IsNullOrWhiteSpace(tvgId))
        {
            return tvgId.Trim();
        }

        return fallback.Trim();
    }

    private static string? GetOptional(IDictionary<string, string> attributes, string key)
    {
        return attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }
}
