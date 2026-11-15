# ClassicTV Plugin — Schedule Architecture

**Status:** **Implemented.** The v2 architecture described here (`DurationCache` +
`RefreshDurationsTask` as an `IScheduledTask` + simplified `ScheduleCacheWorker`) is live.
Since this was written, `RefreshDurationsTask` also gained a `StartupTrigger` (10.11.0.8) so
durations are resolved on every restart, and `ExpandBlockEpisodes` was fixed to emit the full
episode catalog per cycle (10.11.0.7). See [`../DEPLOYMENT.md`](../DEPLOYMENT.md) and
the ClassicTV3.0 `AGENTS.md`.
**Date:** September 9, 2026

---

## Problem with v1

`ScheduleCacheWorker` (an `IHostedService`) holds a reference to `IDurationResolver`
which wraps `ILibraryManager`. The worker fires on a 30-second delay after startup.
That delay is a guess — there is no contract guaranteeing `ILibraryManager` is ready
at T+30s. On a cold start with a large library it may still be indexing. On a fast
machine it may be ready in 5 seconds.

The 30-second delay also means the cache is cold for the first 30 seconds after every
Jellyfin restart. During that window the controller falls back to `ScheduleEngine`
with fallback durations (1800s), which may be wrong.

---

## Better approach: `IScheduledTask` for duration resolution

Jellyfin's `IScheduledTask` interface is designed exactly for operations that need
a fully-initialized server. Tasks run after the server is ready; Jellyfin's own library
scan triggers are wired to this system. The Trakt plugin uses `IScheduledTask` with
`ILibraryManager` extensively — it is the correct interface for this job.

`IHostedService` is correct for things that need to run continuously (timers, watchers).
It is not correct for things that need a ready library. The fix: split the two concerns
that are currently conflated in `ScheduleCacheWorker`.

---

## Target architecture

### Component 1: `DurationCache` (singleton)

A thread-safe dictionary `string jellyfinId → double durationSeconds`.
Populated by `RefreshDurationsTask`. Read by `ScheduleCacheWorker`.

```
DurationCache
  ├── Set(string id, double seconds)
  ├── Get(string id) → double?         // null = not resolved yet
  ├── GetOrFallback(string id) → double // returns FallbackDurationSeconds if unknown
  └── Count
```

`ScheduleEngine.ComputeSchedule` already accepts `IReadOnlyDictionary<string, double>`.
`DurationCache` exposes `AsReadOnlyDictionary()` for that call.

### Component 2: `RefreshDurationsTask` (IScheduledTask)

Runs after library scans (via `TaskTriggerInfo` with trigger type `DailyTrigger` or
manually). Takes `ILibraryManager` in the constructor — safe here because Jellyfin
guarantees the library is ready before scheduled tasks run.

```csharp
public class RefreshDurationsTask : IScheduledTask
{
    // IScheduledTask contract:
    string Key      // "ClassicTVRefreshDurations"
    string Name     // "Refresh ClassicTV episode durations"
    string Category // "ClassicTV"

    IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        // Daily at 3am + run after any library scan completes

    Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        // 1. Get all Jellyfin IDs referenced in channel config
        // 2. Batch-resolve durations via ILibraryManager.GetItemById
        // 3. Write into DurationCache
        // 4. Optionally: trigger ScheduleCacheWorker to rebuild immediately
}
```

`IScheduledTask` implementations are automatically discovered by Jellyfin's DI system
via `typeof(IScheduledTask)` scanning — no `AddSingleton` required.

### Component 3: `ScheduleCacheWorker` (IHostedService, simplified)

Loses `IDurationResolver` entirely. Reads durations from `DurationCache` instead.
The 30-second initial delay can be reduced to a reasonable value (5s) since no
`ILibraryManager` calls are made — only pure schedule math and cache reads.

```csharp
public class ScheduleCacheWorker : IHostedService, IDisposable
{
    ScheduleCacheWorker(ScheduleCache cache, DurationCache durations, ILogger<...> logger)
    // No ILibraryManager. No IDurationResolver.
    // PopulateCache() reads DurationCache.AsReadOnlyDictionary() for durations.
    // Falls back to FallbackDurationSeconds for any ID not yet resolved.
}
```

On first start, `DurationCache` is empty — all schedules use fallback 1800s durations.
After `RefreshDurationsTask` runs (either triggered by library scan or daily timer),
`DurationCache` is populated and the next `ScheduleCacheWorker` cycle uses real durations.

### Component 4: Registration

```csharp
// PluginServiceRegistrator.cs
public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
{
    serviceCollection.AddSingleton<DurationCache>();
    serviceCollection.AddSingleton<ScheduleCache>();
    serviceCollection.AddHostedService<ScheduleCacheWorker>(sp => new ScheduleCacheWorker(
        sp.GetRequiredService<ScheduleCache>(),
        sp.GetRequiredService<DurationCache>(),
        sp.GetRequiredService<ILogger<ScheduleCacheWorker>>()));

    // RefreshDurationsTask: NOT registered here.
    // IScheduledTask implementations are discovered automatically by Jellyfin's
    // task manager via assembly scanning. Just having the class in the DLL is enough.
}
```

---

## Data flow

```
Jellyfin starts
    └── PluginServiceRegistrator.RegisterServices()
          ├── DurationCache registered (empty)
          ├── ScheduleCache registered (empty)
          └── ScheduleCacheWorker started (IHostedService)
                └── Timer fires at T+5s
                      └── PopulateCache() → uses DurationCache (empty → fallback 1800s)
                            → cache filled with fallback-duration schedules

Library scan completes  (or daily at 3am)
    └── Jellyfin triggers RefreshDurationsTask
          └── ExecuteAsync()
                ├── Reads channel config from Plugin.Instance
                ├── Collects all Jellyfin IDs
                ├── ILibraryManager.GetItemById() for each → real durations
                └── Writes into DurationCache

Next ScheduleCacheWorker cycle (hourly)
    └── PopulateCache() → uses DurationCache (now populated → real durations)
          → cache rebuilt with accurate schedules

Controller request (any time)
    └── ScheduleCache.Get(channelId, now)
          → hit: return cached ScheduleResult (real or fallback durations)
          → miss: ComputeSchedule with DurationCache.AsReadOnlyDictionary()
```

---

## What changes from v1

| Component | v1 | v2 |
|---|---|---|
| `ScheduleCacheWorker` constructor | Takes `IDurationResolver` (wraps `ILibraryManager`) | Takes `DurationCache` (no Jellyfin dependency) |
| Initial delay | 30s (guess) | 5s (no library calls needed) |
| Duration resolution | In `IHostedService` timer callback | In `IScheduledTask.ExecuteAsync` |
| Library readiness guarantee | None (30s guess) | Jellyfin's task scheduler contract |
| `IDurationResolver` | Exists | Removed |
| `JellyfinDurationResolver` | Exists | Removed |
| `DurationCache` | Doesn't exist | New singleton |
| `RefreshDurationsTask` | Doesn't exist | New IScheduledTask |

---

## Implementation order

1. Add `DurationCache` — trivially similar to `ScheduleCache`
2. Add `RefreshDurationsTask` implementing `IScheduledTask`
3. Simplify `ScheduleCacheWorker` to take `DurationCache` instead of `IDurationResolver`
4. Update `PluginServiceRegistrator` — remove `IDurationResolver` and `JellyfinDurationResolver` registrations
5. Delete `IDurationResolver.cs` and `JellyfinDurationResolver.cs`
6. Update tests: `ScheduleCacheWorkerTests` no longer needs to handle `ILibraryManager` concerns;
   add `RefreshDurationsTaskTests` covering the `IScheduledTask` contract and `DurationCache` population

---

## Tests required

```csharp
// DurationCacheTests
[Fact] GetOrFallback_UnknownId_ReturnsFallback()
[Fact] GetOrFallback_KnownId_ReturnsStoredValue()
[Fact] Set_OverwritesExistingValue()
[Fact] AsReadOnlyDictionary_ReflectsCurrentState()

// RefreshDurationsTaskTests
[Fact] Key_IsStable()             // "ClassicTVRefreshDurations"
[Fact] Category_IsClassicTV()
[Fact] GetDefaultTriggers_ReturnsAtLeastOneTrigger()
[Fact] ExecuteAsync_WithNullPluginInstance_DoesNotThrow()
[Fact] ExecuteAsync_WithEmptyChannels_DoesNotCallLibraryManager()
[Fact] ExecuteAsync_PopulatesDurationCache_WithResolvedDurations()
[Fact] ExecuteAsync_SkipsUnparsableGuids()

// ScheduleCacheWorkerTests (updated)
[Fact] StartAsync_DoesNotPopulateCacheImmediately()   // keep
[Fact] StartAsync_ThenStopAsync_DoesNotThrow()         // keep
[Fact] PopulateCache_UsesDurationCacheFallback_WhenEmpty()  // replaces old ILibraryManager test
[Fact] PopulateCache_UsesRealDurations_WhenDurationCachePopulated()  // new
```

---

## Notes

- `IScheduledTask` classes in the plugin assembly are discovered automatically by
  Jellyfin's `PluginManager` via reflection — no DI registration needed. This is
  confirmed by the Trakt plugin pattern (Trakt's `PluginServiceRegistrator` only
  registers hosted services, not scheduled tasks).
- `GetDefaultTriggers()` returning a daily trigger is sufficient. A `LibraryScanCompletedTrigger`
  would be ideal but `TaskTriggerInfo` doesn't expose it as a named constant — use
  a daily 3am trigger and trust that admins can manually trigger it after a library
  change.
- On first install with empty `DurationCache`, the system degrades gracefully to
  1800s fallback durations. The Kodi addon's `timestamp` parameter will be slightly
  wrong but the error is bounded at ±1800s (one episode), which is no worse than
  the current 3.0 system.
