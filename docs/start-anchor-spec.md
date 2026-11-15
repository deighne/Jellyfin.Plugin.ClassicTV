# Start Anchor — Schedule Engine Fix

**Date:** September 15, 2026
**Status:** **Implemented** (commit `e608f9c`). The fixed `startAnchor` replaced the
daily-reset `BlockStartEpoch`, and `ComputeSchedule` uses `elapsed = now - startAnchor`.
**Follow-up (2026-09-19):** the anchor was necessary but not sufficient. `ExpandBlockEpisodes`
still emitted only `sum(weight)` slots, so with `weight: 1` only the first episode of each
show ever aired. It now emits the full episode catalog once per cycle. See
[`../DEPLOYMENT.md`](../DEPLOYMENT.md#schedule-engine-semantics-important).

---

## The bug

`ScheduleEngine.BlockStartEpoch` currently returns "when did this block window open
today." For a 24/7 block that means midnight Chicago time today. For a block covering
hours 15–17 it means 3pm today.

This means `elapsed = now - blockStartEpoch` resets to zero every time the block window
opens. The rotation restarts from episode 1 each day.

Correct behaviour: the channel has been "broadcasting" continuously since a fixed point
in the past. `elapsed = now - startAnchor`. The rotation never resets — it just keeps
advancing through episodes and wrapping around.

---

## The fix

### 1. Add `startAnchor` to `ChannelConfig`

```json
{
  "id": "sitcoms",
  "name": "Classic Sitcoms",
  "startAnchor": "2024-01-01T00:00:00Z",
  "blocks": [ ... ]
}
```

`startAnchor` is an ISO 8601 UTC datetime string. It represents the moment the channel
"started broadcasting." All schedule calculations measure elapsed time from this point.

It does not need to be in the past relative to any block window — it is simply a fixed
origin for the modulo arithmetic.

**Default if omitted:** Unix epoch (1970-01-01T00:00:00Z). This is deterministic and
avoids a null-check on every call while producing reasonable behaviour for channels that
don't specify one.

### 2. Remove `BlockStartEpoch` from `ScheduleEngine`

`BlockStartEpoch` is the source of the bug. It computes a daily-resetting origin.
It should be deleted entirely.

### 3. Rewrite `ComputeSchedule` to use `startAnchor`

Current step 4/5:
```csharp
double blockStartEpoch = BlockStartEpoch(block, local);
double elapsed = unixSeconds - blockStartEpoch;
```

Replacement:
```csharp
long anchorEpoch = channel.StartAnchorUnix; // parsed from startAnchor at config load
double elapsed = unixSeconds - anchorEpoch;
```

`elapsed` is now the total number of seconds the channel has been running since its
anchor. `elapsed % cycleDuration` gives the position within the current rotation cycle.

The block time-window check (`FindActiveBlock`) still uses local Chicago time — that
part is correct and unchanged. The only change is what `elapsed` is measured from.

### 4. Block-aware elapsed (multi-block channels)

A channel with multiple blocks needs one additional consideration: elapsed time should
only count time spent in the same block. Otherwise episodes in the 15–17 DBZ block would
advance based on 24 hours of elapsed time, not 2 hours.

The correct calculation for a channel with blocks:

```
total_block_seconds_elapsed = how many seconds have passed since startAnchor
                              that were spent inside this specific block
```

For a block covering hours H1–H2 (2 hours/day), after N days:
```
block_elapsed = (N complete days × block_duration_per_day)
              + (seconds in today's window, if currently in block)
```

This is the same logic `scheduler.py` used with `block_start_epoch` — it measured
elapsed time within the block's window, not absolute wall-clock time. The anchor just
moves the origin from "today's window opening" to a fixed past point.

Concretely for a block covering hours 15–17:
- `block_duration_per_day` = 2 * 3600 = 7200 seconds
- Days since anchor = `floor((now - anchor) / 86400)`
- Seconds into today's block window = `now - today_block_start` (if currently in block)
- `block_elapsed` = `days_since_anchor * 7200 + seconds_into_todays_window`

For a 24/7 block this simplifies to `elapsed = now - anchor` (same as step 3 above).

### 5. Handling the anchor in config

`ChannelConfig` gets a new property:

```csharp
[JsonPropertyName("startAnchor")]
public string StartAnchor { get; set; } = "1970-01-01T00:00:00Z";
```

`ScheduleEngine` parses it once at call time (or `ChannelConfig` can expose a
`StartAnchorUnix` computed property):

```csharp
public long StartAnchorUnix =>
    string.IsNullOrEmpty(StartAnchor)
        ? 0L
        : new DateTimeOffset(DateTime.Parse(StartAnchor,
            null, System.Globalization.DateTimeStyles.RoundtripKind))
            .ToUnixTimeSeconds();
```

---

## What does NOT change

- `FindActiveBlock` — unchanged, still uses local Chicago hour
- `BlockIsActive` — unchanged
- `ExpandBlockEpisodes` — unchanged, WRR rotation logic is correct
- The `elapsed % cycleDuration` modulo arithmetic — unchanged
- The slot-walking and seek-offset calculation — unchanged
- All existing tests for `BlockIsActive` and `ExpandBlockEpisodes` — unchanged

---

## Tests required

All existing `ComputeSchedule` tests need to be updated to either:
a) pass a `startAnchor` that makes the test deterministic, or
b) rely on `elapsed = now - anchor` math explicitly

New tests:

```csharp
// Fixed anchor produces identical result regardless of what day it is
[Fact] ComputeSchedule_FixedAnchor_SameInputAlwaysSameOutput()

// Anchor in distant past — rotation has wrapped many times
[Fact] ComputeSchedule_AnchorFarInPast_PositionWithinCycle()

// Missing anchor defaults to epoch (deterministic, not a crash)
[Fact] ComputeSchedule_MissingAnchor_DefaultsToEpoch()

// Multi-block: DBZ block only counts 2h/day of elapsed time
[Fact] ComputeSchedule_MultiBlock_ElapsedCountsOnlyBlockTime()

// Verify BlockStartEpoch is gone (structural: method should not exist)
// (enforced by the compiler once deleted)
```

---

## Migration for existing channel data

Existing channels have no `startAnchor` field. The default of Unix epoch
(`1970-01-01T00:00:00Z`) means they've been "running" for ~56 years of elapsed seconds.
`elapsed % cycleDuration` still produces a valid position within the cycle — it just
starts at a seemingly arbitrary episode. That's fine. The channel will be consistent
from that point forward.

To set a specific starting episode for a channel, pick a `startAnchor` that puts the
desired episode at position 0:
- Measure the full cycle duration in seconds
- Choose `startAnchor = now - (desired_episode_index * average_episode_duration)`
- Or simply set `startAnchor` to now and accept that the channel starts from episode 1

---

## Implementation order

1. Add `startAnchor` to `ChannelConfig` with default `"1970-01-01T00:00:00Z"`
2. Add `StartAnchorUnix` computed property
3. Rewrite `ComputeSchedule` to use anchor-based elapsed
4. Implement block-aware elapsed for multi-block channels
5. Delete `BlockStartEpoch`
6. Update existing tests, add new tests
7. Verify `ComputeSchedule_SameInputAlwaysSameOutput` passes (the core regression test)
