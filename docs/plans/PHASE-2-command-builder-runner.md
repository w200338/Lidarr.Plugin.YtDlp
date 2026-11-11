# PHASE 2: yt-dlp command builder + process runner

STATUS: COMPLETED (2026-11-11)

## Goal
Pure, fully unit-tested logic that turns (settings, intent) into exact yt-dlp argument
lists, plus a thin CliWrap-based runner seam (`IYtDlpRunner`) that Phase 3/4 build on.

## In scope
- `YtDlpCommandBuilder` (stateless, static or ctor-injected settings):
  - `BuildSearchArgs(query, limit)` → `--ignore-config --no-warnings --flat-playlist
    --print "%(id)s|%(url)s|%(title)s|%(channel)s|%(duration)s|%(type)s" "ytsearchN:<query>"`
    (field order + delimiter = our parse contract; `--print` implies simulate+quiet).
  - `BuildTrackArgs(url, outputPath, settings)` → single-file download:
    `--ignore-config --no-warnings --no-color --newline` + format `-f "ba/b" -x
    --audio-format <fmt> --audio-quality 0` when reencode != Disabled; when Disabled,
    keep the source audio container as-is (`-f "ba/b"`, no `-x`) + `--embed-metadata
    --embed-thumbnail` (metadata always; harmless) + sponsorblock block when
    `useSponsorBlock`: `--sponsorblock-remove
    music_offtopic,sponsor,intro,outro,selfpromo,preview,interaction,hook` +
    `--sponsorblock-api <endpoint>` + `-o "<outputPath>/%(title)s.%(ext)s"` +
    `--sleep-requests 1 --retries 10 --retry-sleep exp=2:32` (politeness baseline;
    inter-download spacing is done by the scheduler, NOT yt-dlp sleep-interval) +
    user `additionalArgs` appended last (user wins) + `-- <url>` (yt-dlp option
    terminator so user args can't clobber ours).
  - `BuildPlaylistArgs(playlistUrl, outputDir, settings)` — same as track but
    `-o "<outputDir>/%(playlist_index&%02d - |)s%(title)s.%(ext)s"`,
    `--download-archive <stateFile>` + `--break-on-existing` (queue resume/dedupe).
  - `BuildVersionArgs()` → `--version`; `BuildSimulateProbeArgs(url)` →
    `--ignore-config --no-warnings --simulate -J`.
  - `AdditionalArgs` parsing: shell-like split (respect quotes) into tokens; reject
    empty tokens; unit-tested.
- `IYtDlpRunner` seam:
  - `Task<YtDlpRunResult> RunBufferedAsync(IReadOnlyList<string> args, CancellationToken ct)`
    → `record YtDlpRunResult(int ExitCode, string StandardOutput, string StandardError)`
    (used for search/probe/version).
  - `Task<YtDlpRunResult> RunStreamingAsync(IReadOnlyList<string> args,
    Action<string> onStdoutLine, Action<string> onStderrLine, CancellationToken ct)`
    (used for downloads; progress arrives on stderr lines).
- `CliWrapYtDlpRunner : IYtDlpRunner` — CliWrap 3.x per research doc 05:
  `Cli.Wrap(path).WithArguments(args)` (collection overload, auto-escape),
  UTF-8 encodings always, `WithValidation(CommandResultValidation.None)` (we inspect
  exit codes ourselves), single-token cancellation = forceful tree-kill; for streaming
  use `ListenAsync`-style pipes (`WithStandardOutputPipe(PipeTarget.ToDelegate(...))` +
  `WithStandardErrorPipe(...)`). Start failures (Win32Exception / not executable) map to
  `YtDlpRunResult { ExitCode = -1, StandardError = "<start error>" }`, never throw.

## Out of scope
Queue/scheduler, indexer, download client, SB HTTP probe (P3–P5).

## Tests
- Command builder (the bulk): exact argument-list assertions for every setting
  combination — reencode disabled/mp3/aac/opus/vorbis, SB on/off (+endpoint, incl.
  scheme-less `sb.internal` → passed through, yt-dlp adds https), additionalArgs
  quoting/splitting edge cases, search limit, playlist vs track templates, `--` before
  URL, user args appended after ours.
- Runner: NSubstitute-free unit tests of the arg→CliWrap mapping are not possible
  (CliWrap starts real processes) → integration test with a **fake yt-dlp shell script**
  (Linux-gated: script echoes JSON to stdout / progress to stderr, exit codes 0/1)
  running the REAL `CliWrapYtDlpRunner`: buffered + streaming + non-zero exit + start
  failure (nonexistent path). Mark `[Explicit]`/OS-gated so it never breaks Windows dev.

## Definition of done
`dotnet build` green, `dotnet test` all green (incl. Linux fake-script integration test
in CI), plan status COMPLETED, committed.
