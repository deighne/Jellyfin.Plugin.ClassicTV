using System.Text.RegularExpressions;
using Jellyfin.Plugin.ClassicTV.Schedule;

namespace Jellyfin.Plugin.ClassicTV.Api;

/// <summary>
/// Server-side validation for <see cref="ChannelConfigRoot"/> payloads submitted to
/// <c>POST /ClassicTV/manage/channels</c>.
///
/// Rules validated (all are errors; none are mere warnings at this layer):
/// <list type="bullet">
///   <item>Each channel ID is non-empty and matches <c>^[a-z0-9-]+$</c>.</item>
///   <item>Channel IDs are unique across the full channel list.</item>
///   <item>Each channel number is an integer in the range 0–9999.</item>
///   <item>Duplicate non-zero numbers among <em>enabled</em> channels are rejected.</item>
///   <item>Each non-offline block has startHour ∈ [0, 23], endHour ∈ [1, 24],
///         and endHour strictly greater than startHour.</item>
///   <item>No two blocks within the same channel have overlapping hour ranges.
///         Two ranges [s1, e1) and [s2, e2) overlap iff s1 &lt; e2 &amp;&amp; s2 &lt; e1.
///         Adjacent ranges (e.g. [0,12) and [12,24)) do NOT overlap.
///         This mirrors <see cref="ScheduleEngine.FindActiveBlock"/>, which returns the
///         <em>first</em> matching block — overlapping blocks would silently discard all
///         but the first, so they are rejected here rather than silently mis-scheduled.</item>
/// </list>
/// </summary>
public static partial class ChannelConfigValidator
{
    // Pre-compiled regex: channel ID must only contain lowercase letters, digits, and hyphens.
    [GeneratedRegex(@"^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelIdPattern();

    /// <summary>
    /// Validates <paramref name="root"/> and returns a (possibly empty) list of
    /// human-readable error strings. An empty list means the config is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(ChannelConfigRoot root)
    {
        var errors = new List<string>();

        if (root.Channels is null || root.Channels.Count == 0)
        {
            // An empty channel list is valid — the admin may be clearing everything.
            return errors;
        }

        // -----------------------------------------------------------------------
        // Channel-level checks
        // -----------------------------------------------------------------------

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var enabledNumbers = new Dictionary<int, string>(root.Channels.Count); // number → channel name

        for (int ci = 0; ci < root.Channels.Count; ci++)
        {
            var channel = root.Channels[ci];
            string channelRef = string.IsNullOrWhiteSpace(channel.Id)
                ? $"channel[{ci}]"
                : $"channel '{channel.Id}'";

            // --- ID: non-empty ---
            if (string.IsNullOrWhiteSpace(channel.Id))
            {
                errors.Add($"channel[{ci}]: Channel ID must not be empty.");
            }
            else
            {
                // --- ID: pattern ---
                if (!ChannelIdPattern().IsMatch(channel.Id))
                {
                    errors.Add($"{channelRef}: Channel ID '{channel.Id}' is invalid — " +
                               "must match ^[a-z0-9-]+$ (lowercase letters, digits, and hyphens only).");
                }

                // --- ID: uniqueness ---
                if (!seenIds.Add(channel.Id))
                {
                    errors.Add($"{channelRef}: Channel ID '{channel.Id}' is duplicated. " +
                               "Each channel must have a unique ID.");
                }
            }

            // --- Number: range ---
            if (channel.Number < 0 || channel.Number > 9999)
            {
                errors.Add($"{channelRef}: Channel number {channel.Number} is out of range — " +
                           "must be an integer between 0 and 9999 (0 = unassigned).");
            }

            // --- Number: uniqueness among enabled channels ---
            if (channel.Enabled && channel.Number > 0)
            {
                if (enabledNumbers.TryGetValue(channel.Number, out var conflictName))
                {
                    errors.Add($"{channelRef}: Channel number {channel.Number} is already assigned " +
                               $"to enabled channel '{conflictName}'. Each enabled channel must have a unique number.");
                }
                else
                {
                    enabledNumbers[channel.Number] = channel.Name ?? channel.Id;
                }
            }

            // --- MaxRating: supported ceiling ---
            if (!ContentRating.IsValidMaxRating(channel.MaxRating))
            {
                errors.Add($"{channelRef}: MaxRating '{channel.MaxRating}' is invalid — " +
                           "must be empty or one of G, PG, PG-13, R, NC-17.");
            }

            // --- Block-level checks ---
            ValidateBlocks(channel, channelRef, errors);
        }

        return errors;
    }

    // -----------------------------------------------------------------------
    // Block validation — called once per channel
    // -----------------------------------------------------------------------

    private static void ValidateBlocks(ChannelConfig channel, string channelRef, List<string> errors)
    {
        if (channel.Blocks is null || channel.Blocks.Count == 0)
            return;

        // Collect validated hour ranges for overlap detection.
        // We only track non-offline blocks; offline blocks are exempt from all hour checks.
        var nonOfflineRanges = new List<(int Start, int End, string Name)>(channel.Blocks.Count);

        for (int bi = 0; bi < channel.Blocks.Count; bi++)
        {
            var block = channel.Blocks[bi];
            string blockRef = $"{channelRef}, block '{block.Name ?? $"[{bi}]"}'";

            if (block.Offline)
            {
                // Offline blocks are exempt from hour-range validation per Requirement 11.6.
                continue;
            }

            bool blockHourValid = true;

            // --- startHour: 0–23 ---
            if (block.StartHour < 0 || block.StartHour > 23)
            {
                errors.Add($"{blockRef}: startHour {block.StartHour} is out of range — must be 0–23.");
                blockHourValid = false;
            }

            // --- endHour: 1–24 ---
            if (block.EndHour < 1 || block.EndHour > 24)
            {
                errors.Add($"{blockRef}: endHour {block.EndHour} is out of range — must be 1–24.");
                blockHourValid = false;
            }

            // --- endHour > startHour ---
            if (blockHourValid && block.EndHour <= block.StartHour)
            {
                errors.Add($"{blockRef}: endHour ({block.EndHour}) must be strictly greater than " +
                           $"startHour ({block.StartHour}).");
                blockHourValid = false;
            }

            if (blockHourValid)
            {
                // --- Block overlap: check against all previously validated non-offline blocks ---
                // [s1,e1) overlaps [s2,e2) iff s1 < e2 && s2 < e1.
                // Adjacent ranges share only a boundary and do NOT overlap.
                foreach (var (prevStart, prevEnd, prevName) in nonOfflineRanges)
                {
                    if (block.StartHour < prevEnd && prevStart < block.EndHour)
                    {
                        errors.Add(
                            $"{blockRef}: hour range [{block.StartHour}–{block.EndHour}) " +
                            $"overlaps with block '{prevName}' [{prevStart}–{prevEnd}). " +
                            $"Overlapping blocks are rejected to prevent silent mis-scheduling " +
                            $"(ScheduleEngine returns the first matching block).");
                    }
                }

                nonOfflineRanges.Add((block.StartHour, block.EndHour, block.Name ?? $"[{bi}]"));
            }
        }
    }
}
