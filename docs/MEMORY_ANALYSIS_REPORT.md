# Strim Memory Consumption Analysis

**Date:** 2026-07-15
**Scope:** Static review of the application, Docker image/build configuration, repository deployment files, and the supplied Grafana screenshots. No production heap dump, Coolify configuration export, production logs, or live container inspection was available.

> Implementation note (2026-07-15): the findings below describe the pre-remediation pipeline. The
> streaming/cache/container changes listed in **Implementation status** have now been added and
> verified locally. Production Grafana and cgroup metrics are still required to prove the new
> memory envelope on the actual Coolify host.

## Implementation status

| Finding | Implemented change | Expected effect |
|---|---|---|
| M-01 | `PlaylistSourceFetcher` copies HTTP bodies to a private temporary file in 128 KiB buffers while calculating SHA-256. It validates each redirect, does not forward validators across origins, honors cancellation, applies a configurable header/body timeout, and enforces a deliberately high configurable byte ceiling (2 GiB default). | Eliminates source-sized managed `byte[]`/UTF-16 allocations without imposing a 25 MiB source limit. |
| M-02 | `PlaylistProcessor` analyzes and filters files line by line with a bounded reader. It caps individual line length, group-title length, distinct-group count, and retained group metadata; generated M3U output is a file streamed to the client. | Eliminates full-file `Split`, regex, output-JSON, and output-byte-array amplification while bounding the metadata that must remain in RAM. |
| M-03/M-04 | Public share URLs use a dedicated rate policy and every fetch/analyze/generate/share/refresh job passes through one global permit by default. Raw JSON analysis is body-limited and admitted one at a time before model binding. | Prevents overlapping large jobs or raw uploads from multiplying memory and CPU. |
| M-05 | The large `IMemoryCache` source strings were replaced with TTL- and disk-budgeted source/output files. Scratch capacity is reserved before downloading or generating, and an opaque session/output key maps to metadata only. | Removes the 15-minute in-memory source retention path and makes the disk budget enforceable during writes. |
| M-06 | Source SHA-256, ETag, Last-Modified, byte length, and check time are persisted. A configurable 15-minute freshness interval avoids any upstream request during the interval; afterward validators are used when available and hash equality skips parsing/filtering. | A hash now acts as an effective version cache with an explicit bounded-staleness trade-off. |
| M-07/M-08 | The browser no longer generates immediately after analysis; newer loads abort older loads, and a late generate response is ignored if its session is no longer current. Request abort signals flow through download/parse work. | Removes the automatic second pass, reduces abandoned work, and prevents stale output from being applied to another source. |
| M-09/M-10 | The final image no longer contains the unused DbMigrator; build context is restricted; the image has a liveness health check; per-health-request Info logging was removed; M3U response compression is disabled in the app. | Runtime image verified at **110,895,656 bytes (~111 MB)** locally; lowers image, log, and application CPU overhead. |

The later CPU screenshot should not be read as proof that Strim is consuming hundreds of cores:
the sustained high series is labelled `docker`, while the Strim series is near the baseline. A
tooltip of `251` under a “cores” title is likely a unit/query mismatch (for example 251 millicores
or 251%). Verify the panel expression and inspect `docker stats`/`dockerd` before assigning that
host-level work to the application.

### New runtime configuration

All settings are safe to override in Coolify as environment variables using double underscores:

```text
PlaylistCache__Directory=/tmp/strim-playlists
PlaylistCache__MaxSourceBytes=2147483648
PlaylistCache__MaxDiskBytes=8589934592
PlaylistCache__EntryTtlMinutes=30
PlaylistCache__SourceTtlMinutes=1440
PlaylistCache__RevalidationIntervalMinutes=15
PlaylistCache__DownloadTimeoutSeconds=600
PlaylistCache__HeaderTimeoutSeconds=15
PlaylistCache__MaxLineLengthChars=65536
PlaylistCache__MaxGroupCount=10000
PlaylistCache__MaxGroupTitleLengthChars=512
PlaylistCache__MaxGroupMetadataBytes=8388608
PlaylistCache__MaxConcurrentJobs=1
PlaylistCache__QueueTimeoutSeconds=30
```

`MaxSourceBytes` is a disk-safety guard, not a 25 MiB product limit. Increase it and the disk
budget together when a legitimate provider requires a larger source; this does not reintroduce
whole-playlist RAM allocation. For a source ceiling of `S`, provision at least roughly `2S` cache
disk so an input and generated output can coexist. The cache reserves space before writing and
rejects a job with a clear error instead of overshooting the configured disk budget.

### Hash cache semantics and freshness

The cache does **not** cap downloaded source lists at 25 MiB. It works as follows:

1. For `RevalidationIntervalMinutes` (15 by default), reuse the already-spooled source, parsed
   group analysis, and matching filtered-output variant without contacting the provider.
2. After that interval, send `If-None-Match`/`If-Modified-Since` only to the original provider
   origin. A `304 Not Modified` reuses the existing file without reading its body.
3. If the provider has no usable validator, download and SHA-256 the source once per interval.
   When its hash is unchanged, reuse the existing parsed metadata and generated variant; when it
   differs, parse/filter the new version.

Therefore hashing alone cannot eliminate download CPU/network for a validator-less provider. The
freshness interval is the intentional cache knob: increase it to reduce CPU/network, or set it to
`0` to revalidate every request when freshness is more important than cost.

### Disposal and GC

The new source fetch, parser, writer, response stream, hashing objects, pooled buffers, and cache
leases use `using`/`await using` or `finally` cleanup. This closes file/socket handles and returns
buffers promptly even when a request cancels. It is important, but it does **not** force garbage
collection or make a full source string cheap; streaming is the actual reason managed memory no
longer grows with source size.

### Deployment requirements after the code fix

- Run a **single Strim replica** unless Coolify uses sticky routing or the cache is moved to shared
  storage. Analyze and generated-output capability keys are intentionally process-local.
- Give the container a measured memory limit as containment, not as the primary fix. Begin with
  one playlist job, collect RSS/GC metrics for the largest real source, then set a limit with
  headroom above the observed steady peak. Do not infer a safe limit from the old 2–3 GiB graph.
- Keep `MaxConcurrentJobs=1` initially. Raising it is a capacity decision: each permitted job
  may use source/output scratch disk and CPU concurrently even though its managed-memory footprint
  is now small.
- Mount or provision enough **ephemeral** cache disk for the configured budget; source/output
  cache files are intentionally removed at restart and must not share the persistent database
  volume unless that capacity is planned.
- Set `TRUSTED_PROXIES` to the actual Coolify proxy network so IP-based rate limiting cannot be
  spoofed by direct requests with forged forwarded headers.

### Container-image and dependency result

The production image is already compact for a .NET/EF Core/Postgres application: approximately
111 MB locally after removing the unused migrator and excluding tests, Terraform, docs, Git data,
and development artifacts from the build context. There is no npm production bundle to tree-shake:
the frontend is vanilla JavaScript. `PublishTrimmed` is not enabled because EF Core, Identity,
Swagger, and OAuth use reflection/dynamic loading; enabling it without a dedicated compatibility
test matrix can break runtime behavior for a modest image saving. A separate Tailwind production
CSS build remains a worthwhile frontend/security optimization, but is not related to the server
RAM spike.

## Executive summary

At the time of review, the most credible explanation for the observed 2–3 GiB container-memory usage was **whole-playlist allocation amplification**, not a proven classic memory leak.

Strim previously downloaded a complete source playlist into a `byte[]`, decoded it into a complete .NET `string`, repeatedly normalized and split it into all lines, cached the full source for 15 minutes, built a complete filtered output, and in some paths created a final UTF-8 `byte[]`. Large allocations go to the .NET large-object heap (LOH), which may stay committed after objects are collectible. This exactly fits the observed pattern: a rapid rise to about 3 GiB followed by stepwise declines to about 2.14 GiB rather than a strictly monotonic climb.

The most dangerous pre-remediation route was the unauthenticated share URL. Every request re-downloaded, parsed, filtered, encoded, and returned the full source playlist, with no endpoint rate or concurrency limit. IPTV clients commonly poll these URLs, so one popular share link or several clients could create overlapping multi-GiB jobs.

There are no repository-defined CPU or memory limits for the application container. The inspected local image runs .NET Server GC, which is normal for ASP.NET Core but can retain larger heap segments after bursty large-object workloads. This is an amplifier, not the root cause.

**Conclusion:** Treat this as a production memory-safety issue. The required solution is streaming and/or bounded temporary-file processing for large sources, plus a global concurrency limiter. Do not impose a small source-file limit: the replacement design supports very large source playlists while keeping managed memory approximately bounded.

## What the screenshots indicated before remediation

| Observation | Interpretation | Confidence |
|---|---|---:|
| The `strim` series, rather than PostgreSQL or Docker, rises to roughly 3 GiB. | The application process is responsible for the material increase. | High |
| Memory rises in large steps, then falls in large steps. | More consistent with large request/cache/GC events than a simple per-request reference leak. | High |
| The container declined from about 3 GiB to about 2.14 GiB over the subsequent period. | At least some allocations became collectible; 15-minute cache expiry and later Gen-2/LOH collection are plausible contributors. | Medium-high |
| Network activity was visible near the increase in the earlier screenshot. | Correlates with the server fetching external playlists. | Medium |
| The Grafana panel is labelled only `Container memory`. | It may include filesystem page cache as well as anonymous process memory; the underlying metric/query must be checked before treating its value as managed heap. | High |

The falling series weakens the case for an indefinitely retained object graph, but **2 GiB remains too high for an idle Strim instance**. A GC heap and cgroup-memory breakdown is required to tell whether it is live managed memory, committed-but-reusable heap, native memory, or filesystem cache.

## How the original pipeline produced a multi-GiB peak

Let `B` be the downloaded source size in bytes. M3U files are generally ASCII-heavy, so a decoded .NET UTF-16 string is approximately `2B` bytes.

| Stage | Typical allocation | Evidence |
|---|---|---|
| Remote fetch | `byte[B]`, then source `string[~2B]`; both can coexist while decoding. | `FetchPlaylistText` uses `ReadAsByteArrayAsync()` then `Encoding.UTF8.GetString()`. |
| Analysis | Normalized replacement strings, an array of all lines, and strings for all lines; total line text is again roughly `2B`, plus array/object overhead. | `NormalizeLines()` performs `Replace`, `Replace`, and `Split`. |
| Cache | One retained UTF-16 source string per random cache key, for 15 minutes. | `IMemoryCache` stores `playlistText`. |
| Generation | Repeats normalization/splitting, then builds an entire output in `StringBuilder`, converts it to a string, and trims it. | `GenerateFiltered()`. |
| Response | JSON serialization writes the full output for `/generate`; share delivery additionally makes `Encoding.UTF8.GetBytes(filtered.Text)`. | Generate and share endpoints. |

The stages overlap. A large source can therefore have several full-size representations alive at once. The exact multiplier depends on line endings, number of channels/groups, how much is filtered out, and GC timing, but it is easily several times the source size. A 200–500 MiB provider playlist can plausibly produce a 2–3 GiB peak with the present design.

## Original findings and risk ranking (pre-remediation)

| ID | Finding | Likelihood | Memory impact | Why it matters |
|---|---|---:|---:|---|
| M-01 | Unbounded buffering of provider downloads | Very high | Critical | Any source size can be fully allocated in memory; no streamed byte ceiling exists. |
| M-02 | Full-file parsing and output construction | Very high | Critical | The processor creates several source/output-sized allocations per operation. |
| M-03 | Public share route is neither rate-limited nor concurrency-limited | High when share URLs are used | Critical | Every share hit repeats the entire download/filter/encode pipeline. |
| M-04 | No global concurrency cap for heavy playlist jobs | High | Critical | Fixed-window request limits do not cap qusimultaneous large allocations. |
| M-05 | 500 MiB in-memory source cache with random duplicate keys | High | High | Retains large LOH strings for 15 minutes; is not a process-memory cap. |
| M-06 | Immediate and unbounded background auto-refresh | Medium | High | Container start and six-hour cycles can sequentially fetch all enabled large sources. |
| M-07 | Browser triggers analysis and full generation back-to-back | Certain | High transient | One Load action pays for two large server phases; repeated Load actions can overlap. |
| M-08 | Request cancellation/body-read timeout is not propagated | Medium | High under abandoned/slow requests | Disconnected clients can leave large jobs running; `ResponseHeadersRead` ends the `HttpClient.Timeout` scope before body buffering. |
| M-09 | No versioned container CPU/memory limits; Server GC enabled | High in repository config; actual Coolify setting unknown | Medium-high | Allows bursty heap retention to grow against host capacity and can use many GC heaps on a multi-core host. |
| M-10 | Runtime image carries an unused migration tool | Certain | Low | Increases image/disk footprint, not a credible explanation for 2–3 GiB RAM. |

### M-01 — Provider downloads are fully buffered with no source-size enforcement

`FetchPlaylistText` calls `ReadAsByteArrayAsync()` and then decodes the entire response ([api/Program.cs](../api/Program.cs#L1087)). `HttpCompletionOption.ResponseHeadersRead` does **not** make the subsequent `ReadAsByteArrayAsync()` streamed; it only returns from `GetAsync` after headers. The body is then explicitly buffered in full ([api/Program.cs](../api/Program.cs#L1149)).

This helper is used by analysis, generation fallback, and public shares. Both `/api/fetch` endpoints separately use the same whole-body pattern ([api/Program.cs](../api/Program.cs#L1744) and [api/Program.cs](../api/Program.cs#L1795)). The auto-refresh service uses `ReadAsStringAsync()` with the same result ([api/Services/PlaylistRefreshService.cs](../api/Services/PlaylistRefreshService.cs#L53)).

`MaxPlaylistTextSize` only validates an already model-bound `RawText` request body ([api/Program.cs](../api/Program.cs#L71) and [api/Program.cs](../api/Program.cs#L1443)). It does not limit remote source downloads, and it is measured in UTF-16 characters rather than downloaded bytes.

### M-02 — The parser and generator intentionally materialize whole inputs and outputs

`PlaylistProcessor.NormalizeLines()` replaces line endings twice and then splits the entire string into a `string[]` ([api/Services/PlaylistProcessor.cs](../api/Services/PlaylistProcessor.cs#L126)). Every line is allocated before either counting or filtering starts. `ExtractGroupTitle()` creates regex match objects for attributes on each `#EXTINF` line ([api/Services/PlaylistProcessor.cs](../api/Services/PlaylistProcessor.cs#L132)).

Generation repeats the same normalization, builds the complete output in a `StringBuilder`, calls `ToString()`, then trims the result ([api/Services/PlaylistProcessor.cs](../api/Services/PlaylistProcessor.cs#L36)). The public share route makes a further complete UTF-8 byte array before returning it ([api/Program.cs](../api/Program.cs#L1715)).

This is a high-allocation architecture even when every object is eventually released. Large strings and arrays are allocated on the LOH, where segment retention and fragmentation can keep RSS/cgroup memory elevated after a burst.

### M-03 — Public share requests are unbounded work multipliers

`GET /api/playlists/{id}/share/{code}` is deliberately unauthenticated, but it has no `.RequireRateLimiting(...)` call ([api/Program.cs](../api/Program.cs#L1693)). There is also no global limiter. Each request fetches the source anew, filters it, creates an output byte array, updates the database, and sends the response.

This is especially important for IPTV use: a user may have multiple clients, and clients may periodically refresh a share URL without any browser session. If a source is large, a small number of concurrent share downloads is sufficient to reproduce the observed peak.

### M-04 — Current rate limits do not control concurrent memory

The `fetch` policy permits 30 requests per IP per minute ([api/Program.cs](../api/Program.cs#L197)). A fixed-window limiter controls admissions over time, not the number of in-flight jobs; many accepted requests can buffer large playlists simultaneously. The public share endpoint does not even use that policy.

In addition, the default forwarded-header configuration trusts all forwarded headers and allows unlimited hops when `TRUSTED_PROXIES` is unset ([api/Program.cs](../api/Program.cs#L25)). This makes IP-based protection less trustworthy unless Coolify strips client-supplied forwarded headers and the app is reachable only through the proxy.

### M-05 — The cache is bounded logically, but not safely for process memory

The application configures a 500 MiB `IMemoryCache` size limit ([api/Program.cs](../api/Program.cs#L172)). Analysis creates a new random cache key every time and retains the complete source for 15 minutes ([api/Program.cs](../api/Program.cs#L1431)). Re-loading the same URL therefore creates another cache entry instead of reusing it.

The cache limit tracks caller-supplied entry sizes; it does not account for transient byte arrays, parser allocations, output strings, JSON buffers, regex allocations, or GC heap fragmentation. It is not a 500 MiB process limit. Expiry makes an entry removable, but does not guarantee immediate collection or return of committed memory to the OS.

### M-06 — Auto-refresh downloads every enabled source immediately at startup

The background service runs `RefreshPlaylists()` before its first six-hour delay ([api/Services/PlaylistRefreshService.cs](../api/Services/PlaylistRefreshService.cs#L17)). It loads all enabled playlist metadata and processes each source sequentially. Sequential execution limits concurrent downloads from this one service, but a single large source is still fully buffered and split ([api/Services/PlaylistRefreshService.cs](../api/Services/PlaylistRefreshService.cs#L42)).

If the memory step occurs directly after deployment/restart or every six hours, this path becomes a leading suspect.

### M-07 — Normal browser flow adds avoidable server work

Loading a source calls `/playlist/analyze` and immediately calls `updateOutput()`, which posts `/playlist/generate` ([main.js](../main.js#L863) and [main.js](../main.js#L1824)). This differs from the README statement that generation is deferred until an explicit action ([README.md](../README.md#L201)).

Refresh and Copy generate again, and the source Load control is not disabled, deduplicated, or cancelable ([main.js](../main.js#L121) and [main.js](../main.js#L1896)). Each duplicate analysis has a new server cache key. The frontend has no polling loop, retry loop, WebSocket, or EventSource that would independently leak server memory; it is a request multiplier rather than an autonomous leak.

### M-08 — Cancellation and body-read timeout gaps

The configured `HttpClient.Timeout` is 15 seconds ([api/Program.cs](../api/Program.cs#L250)), but the normal fetch helper accepts no cancellation token and passes none to `GetAsync` or `ReadAsByteArrayAsync` ([api/Program.cs](../api/Program.cs#L1087)). With `ResponseHeadersRead`, the request task completes at headers, so content-body reads need their own timeout/cancellation.

The HTTP handlers do not propagate `HttpContext.RequestAborted`. A canceled browser request or disconnected share client can therefore leave upstream download/parsing work alive until it completes or otherwise fails.

### M-09 — Container and runtime configuration amplify bursty workloads

The Dockerfile uses the ASP.NET Core .NET 8 runtime image and starts `dotnet api.dll` ([Dockerfile](../Dockerfile#L1)). Neither compose file declares memory or CPU constraints ([docker-compose.yml](../docker-compose.yml#L1), [docker-compose.sqlite.yml](../docker-compose.sqlite.yml#L1)). The uncommitted Hetzner template also defines no resource limits ([infra/hetzner/templates/cloud-init.yml.tftpl](../infra/hetzner/templates/cloud-init.yml.tftpl#L14)). Actual Coolify limits are not present in this repository and must be inspected on the server.

Read-only inspection of the local `strim:root-route-test` image found `.NET 8.0.28`, `DOTNET_RUNNING_IN_CONTAINER=true`, and `System.GC.Server=true` in `api.runtimeconfig.json`. Server GC is the expected Web SDK setting and is not a leak. However, without a cgroup CPU/memory limit it can use the host's perceived processor capacity and retain larger heap segments after LOH-heavy bursts. If Coolify supplies a limit, .NET can use that cgroup information; it needs to be verified with `docker inspect` and cgroup files.

The same image contains a 44 MiB migration-tool copy under `/app/migrator`. This is worth removing from the final image for smaller pulls and filesystem footprint, but it is a **low-impact memory finding**.

There is also no Docker `HEALTHCHECK`, resource telemetry exporter, or repository-held Coolify application definition. The application exposes liveness/readiness endpoints and the image installs `curl`, so a health check is straightforward to add; it will not lower memory directly, but it will make OOM/restart gaps diagnosable. The untracked Hetzner template defaults to an older image tag, so it must not be assumed to describe the live Coolify workload.

## Recommended target architecture

The design should support large source playlists without using RAM proportional to the entire source or output:

1. **Bounded, streamed source intake.** Use `SendAsync(..., ResponseHeadersRead, cancellationToken)` and copy the response stream in fixed-size buffers. Enforce a configurable *high* byte and disk quota while copying, including for chunked responses. A small 20–25 MiB ceiling is not appropriate for this product.
2. **Ephemeral spool file, not an in-memory source cache.** Stream an accepted source to a randomly named file in a private temporary directory, with owner-only permissions, TTL cleanup, disk-free-space checks, and cancellation cleanup. Cache only job metadata/path and expiration. On a future multi-instance deployment, use a shared object store instead.
3. **Streaming parser.** Count groups and generate output from a `StreamReader`/span-based line reader. Do not create a `string[]` containing every line; replace the regex with targeted group-title extraction.
4. **Streaming output.** For shares and downloads, write the filtered M3U straight to the HTTP response. Do not build a full `StringBuilder`, JSON `filteredText`, or final UTF-8 byte array. If the UI needs counts, return them from analysis or use a separate metadata request.
5. **One global playlist-job limiter.** Apply a `ConcurrencyLimiter` or a semaphore around all source fetch/parse/generate/share/refresh jobs. Start with one or two permits, a short bounded queue, and a clear `429`/`503` response when busy.
6. **End-to-end cancellation.** Pass `RequestAborted` from every handler into source reads and parsing; make temporary-file cleanup reliable in `finally` blocks. Add an explicit body-read timeout in addition to connection/header timeout.
7. **Use cache purposefully.** Do not retain giant source strings in `IMemoryCache`. If caching is needed for share traffic, cache a bounded temporary/generated file plus metadata and invalidate it by TTL, ETag, or Last-Modified.

Illustrative configuration names—not prescribed values—could be:

```text
PlaylistCache__MaxSourceBytes=<largest legitimate source plus safety margin>
PlaylistCache__MaxDiskBytes=<instance disk budget>
PlaylistCache__MaxConcurrentJobs=1 or 2
PlaylistCache__EntryTtlMinutes=15
PlaylistCache__DownloadTimeoutSeconds=<provider-appropriate value>
```

For example, a 1 GiB maximum source is compatible with the streaming/spool design if the instance has adequate temporary disk. It is **not** compatible with the current all-in-memory design.

## Prioritized remediation plan

| Priority | Action | Expected memory effect | Notes |
|---|---|---|---|
| P0 | Add a global concurrency limiter and rate-limit the share route. | Stops multiplicative peaks immediately. | This can ship before parser refactoring. |
| P0 | Configure Coolify resource limits and restart policy after measuring the largest workload. | Limits blast radius on the host. | A cap is containment, not a fix; choose it above measured safe peak while refactoring. |
| P0 | Disable auto-refresh temporarily if timestamps correlate with startup/six-hour refresh. | Removes a known unbounded background trigger. | Re-enable after streaming refresh is implemented. |
| P1 | Replace all full-body source reads with streamed, bounded spool-file intake. | Removes source-size-proportional managed allocations. | Apply uniformly to analyze, generate fallback, share, `/api/fetch`, and refresh. |
| P1 | Stream parsing and share/download output. | Removes line-array/output-string/UTF-8-copy peaks. | The principal long-term fix. |
| P1 | Stop automatic generation immediately after analysis; prevent duplicate Loads and add abort support. | Removes avoidable second pass and overlapping jobs. | Aligns UI with README behavior. |
| P2 | Replace giant in-memory cache entries with metadata/file leases. | Reduces idle baseline and LOH retention. | Add cleanup and quota observability. |
| P2 | Pin base-image digests and remove `/app/migrator` from final image. | Small RAM benefit; better reproducibility and smaller image. | Hygiene, not a root-cause fix. |
| P2 | Set `TRUSTED_PROXIES` for the actual Coolify proxy network. | Makes IP limits meaningful. | Also avoids accepting arbitrary forwarded identities. |
| P3 | Add runtime/cgroup metrics, request-size logs, and stress tests. | Proves the fix and detects regressions. | Do not log full provider URLs, which may contain credentials. |

## Verification plan

### 1. Establish what the Grafana series represents

Inspect the dashboard query. Prefer to graph all of the following separately:

- Container RSS/anonymous memory.
- Container working set.
- Cgroup `anon`, `file`, and `inactive_file` memory.
- .NET GC heap size, LOH size, fragmentation, allocation rate, and Gen-2 collections.
- Active playlist jobs, source bytes, output bytes, cache hit/miss, and job duration.

If the graph is `container_memory_usage_bytes`, a large file-cache component can make the value look worse than non-reclaimable process memory. `anon`/RSS near 2 GiB would instead confirm the process itself is retaining the memory.

### 2. Gather production state safely

Run these on the Coolify/Hetzner host against the actual Strim container:

```bash
docker inspect <container> \
  --format 'restarts={{.RestartCount}} oom={{.State.OOMKilled}} limit={{.HostConfig.Memory}} cpus={{.HostConfig.NanoCpus}}'

docker stats --no-stream <container>

docker exec <container> sh -c \
  'grep -E "VmRSS|RssAnon|RssFile|RssShmem" /proc/1/status; \
   grep -E "^(anon|file|inactive_file|slab) " /sys/fs/cgroup/memory.stat; \
   cat /sys/fs/cgroup/memory.events'
```

Interpretation:

- High `RssAnon`/`anon`: managed heap or other anonymous process allocations are the main issue.
- High `file` with high `inactive_file`: much of the panel may be reclaimable filesystem cache.
- `oom_kill`/nonzero restarts: gaps in the graph are likely OOM/restart events.
- Zero memory/CPU limits: the container is relying on the host to absorb peak allocations.

### 3. Reproduce with representative sources

After adding safe instrumentation, run this sequence with the largest real playlist size and a sanitized source URL/fixture:

1. Restart the container and record warm idle baseline.
2. Load the source once; record maximum RSS, GC heap/LOH, source bytes, and active jobs.
3. Wait longer than cache TTL plus a GC cycle; record idle values again.
4. Repeat Load quickly and from two browser sessions; verify that only configured concurrent jobs run.
5. Hit a share URL from one, then several, clients; verify rate/concurrency behavior.
6. Cancel a browser request midway; verify the upstream read ends and its temporary file disappears.
7. Enable auto-refresh for several sources; verify every iteration stays within the same memory envelope.

Success is not merely a lower Grafana number. The target is a bounded anonymous-memory profile that does not scale by several full copies of source size, no OOM events, and predictable rejection/queueing under overload.

## Lower-priority observations

- The database model stores metadata and disabled groups, not raw playlists, so EF Core/Npgsql/SQLite are not likely explanations for the multi-GiB spike. `GET /api/playlists` does materialize and sort all of a user's metadata in memory, but this is a separate scale concern.
- Response compression can add buffers and CPU work for large M3U/JSON responses, but it is secondary to the full source/output allocations already identified.
- The frontend Web Worker also splits the full local playlist and builds/structured-clones a full output. That can affect browser memory but does not explain the Hetzner container series; the worker is terminated after completion.
- The Dockerfile uses floating `sdk:8.0` and `aspnet:8.0` tags. Pinning a patch/digest and adding an OCI revision label will make it possible to correlate a future memory regression with the exact image that Coolify runs.
- Existing automated tests cover `SecurityHelpers`; there are no large-playlist, cancellation, concurrency, cache-expiry, or memory-regression tests. This report is therefore a code/telemetry review, not a production heap-dump diagnosis.

## Confidence and limitations

The buffering, parser, cache, public-share, cancellation, and configuration findings are directly confirmed from source and image metadata. The conclusion that they caused the particular Grafana spike is highly plausible but not mathematically proven without a request timeline and managed-heap/cgroup breakdown.

The local image inspected was `strim:root-route-test` on ARM64 and reported .NET 8.0.28; it validates the Dockerfile-derived runtime behavior but is not proof that the live Coolify deployment runs the exact same image, architecture, environment variables, or limits. The repository's untracked Hetzner template defaults to an older image tag, while the stated deployment is Coolify. Production inspection remains necessary before making a precise capacity decision.
