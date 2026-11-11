# CliWrap API Research (3.7.x – 3.10.x)

STATUS: COMPLETE

Context: C# 12 / net8.0 plugin shelling out to an installed `yt-dlp` via CliWrap. The project
pins 3.7.1, but a transitive dependency pulls 3.10.1 → **effective compiled version is 3.10.1**
(NuGet resolves to the highest). All signatures verified against the tagged source of both
3.7.1 and 3.10.1 plus the official GitHub Release notes (no CHANGELOG.md; notes on the Releases page since 3.6.5).

## TL;DR

- 3.x API: `Cli.Wrap(path) → Command → ExecuteAsync() → CommandResult`. The older
  `ProcessWrapper`/`ProcessResult`/`ProcessException` names **do not exist in 3.x** (2.x only).
- 3.7.1 → 3.10.1 is source- and binary-compatible for normal usage: nothing removed or renamed;
  only additions (`WithResourcePolicy`, `ExecuteAsync(ProcessStartInfo, Process)` overload,
  implicit conversions + `Deconstruct` on results, `PipeSource.FromCommand(cmd, transform)`,
  `TimeoutException` on failed kill). Nags: `CommandTask.Select` `[Obsolete]`; net8.0 gets the
  net7.0 asset (TFM change).
- **No synchronous `Execute()` in 3.x** — async only; `ExecuteAsync` **throws
  `CommandExecutionException` on non-zero exit by default** (suppress with `WithValidation(CommandResultValidation.None)`).
- Default stream decoding is `Encoding.Default` (ANSI/OEM on Windows, not UTF-8) — **pass
  `Encoding.UTF8`** for yt-dlp. No retry/rate-limit utilities exist in the library.

## 1. Repo, versions, target frameworks

- Repo `github.com/Tyrrrz/CliWrap`, default branch `prime`, tags `3.7.1` … `3.10.5`; library
  source at both tags: `CliWrap/CliWrap/*.cs`. TFMs: 3.7.1 = `netstandard2.0; netstandard2.1;
  net8.0; net9.0`; 3.10.1 = `netstandard2.0; netstandard2.1; net6.0; net7.0; net10.0` — a net8.0
  plugin against 3.10.1 consumes the **net7.0** asset (nothing we need is missing).

## 2. `Cli.Wrap` overloads

Exactly one overload in both versions (`CliWrap/CliWrap/Cli.cs`):

```csharp
public static class Cli {
    public static Command Wrap(string targetFilePath) => new(targetFilePath);
}
```

`targetFilePath` may be a full path or a bare name resolved via PATH (on Windows an internal fallback
also probes PATHEXT scripts). `Command` is **immutable** — every `With*` returns a modified copy (`[Pure]`) —
safe to share across threads/tasks; each `ExecuteAsync` starts its own process (no shared mutable state).

## 3. Argument handling: parsing, quoting, `WithArguments`

`Command` configuration methods (all in 3.7.1, plus `WithResourcePolicy(ResourcePolicy)` and
`WithResourcePolicy(Action<ResourcePolicyBuilder>)` added in 3.8 — see §9):

```csharp
public Command WithTargetFile(string targetFilePath);
public Command WithArguments(string arguments);               // raw; manual escaping
public Command WithArguments(IEnumerable<string> arguments, bool escape);
public Command WithArguments(IEnumerable<string> arguments);  // escape: true
public Command WithArguments(Action<ArgumentsBuilder> configure);
public Command WithWorkingDirectory(string workingDirPath);
public Command WithCredentials(Credentials);                  // + (Action<CredentialsBuilder>)
public Command WithEnvironmentVariables(IReadOnlyDictionary<string, string?>); // + (Action<EnvVarsBuilder>)
```

Also: `WithValidation(CommandResultValidation)` (flags: `None` | `ZeroExitCode`),
`WithStandardInputPipe(PipeSource)` / `WithStandardOutputPipe(PipeTarget)` / `WithStandardErrorPipe(PipeTarget)`; a `null` env var value **removes**
the variable for the child process; `Command.ToString()` returns `"target arguments"`.

`ArgumentsBuilder` (identical in both versions): `Add(string)`, `Add(string, bool escape)`,
`Add(IEnumerable<string>, bool escape)`, `Add(IFormattable, IFormatProvider, bool escape = true)`
(+ culture overloads), `Add(IFormattable, bool escape = true)`, `Build()` — all default
`escape: true`; plus `public static string Escape(string)` (public since 3.7, PR #265).

Quoting behavior: `Add`/`Escape` implement .NET's `PasteArguments` rules (Windows
`CreateProcess`-compatible): clean strings pass through; otherwise wrapped in double quotes,
backslashes before quotes doubled, `"` → `\"`. The built string goes into
`ProcessStartInfo.Arguments`, which **.NET itself parses** (no shell — `UseShellExecute = false`),
so escaping targets .NET's parser, not `sh`/`bash`. Prefer `WithArguments(IEnumerable<string>)` /
the builder overloads to sidestep quoting pitfalls; `WithArguments(string)` needs correct pre-escaping (README warns against it; `ArgumentsBuilder.Escape`
covers the rare manual case).

## 4. Execution: `ExecuteAsync`, return types, sync behavior

Public overloads (3.10.1, `Command.Execution.cs`; 3.7.1 has only #2 and #3):

```csharp
// #1 — added in 3.9 (PR #290); manual ProcessStartInfo/Process escape hatch
public CommandTask<CommandResult> ExecuteAsync(
    Action<ProcessStartInfo>? configureStartInfo, Action<Process>? configureProcess = null,
    CancellationToken forcefulCancellationToken = default,
    CancellationToken gracefulCancellationToken = default);
```

#2 (both): `ExecuteAsync(CancellationToken forcefulCancellationToken, CancellationToken
gracefulCancellationToken)`; #3 (both): `ExecuteAsync(CancellationToken cancellationToken = default)` — the single token maps to the forceful slot.

- **No synchronous `Execute()` exists in 3.x at all.** For a blocking-style probe, `await`
  `ExecuteBufferedAsync(...)` (recommended) or `.GetAwaiter().GetResult()` (blocks a thread; avoid on UI/ASP.NET threads). Process start is **synchronous** (start errors throw at the call site, not in the task); the returned `CommandTask` tracks the run.
- `CreateStartInfo()` always sets `RedirectStandardInput/Output/Error = true`,
  `UseShellExecute = false`, `CreateNoWindow = true` (Windows), never sets
  `StandardOutputEncoding`/`StandardErrorEncoding` — decoding is done by the pipes (§7); console passthrough: overload #1 (3.9+) with `RedirectStandard* = false`.

`CommandTask<TResult>` (both versions): `Task` (`Task<TResult>`), `ProcessId` (`int`),
`GetAwaiter()`, `ConfigureAwait(bool)`, `IDisposable`, `implicit operator Task<TResult>`;
`Select<T>` is public in 3.7.1, `[Obsolete("Use async/await instead.")]` in 3.10.1. Awaiting surfaces all cancellation/validation exceptions at the `await` site.

## 5. Result types: `CommandResult` / `BufferedCommandResult`

```csharp
public class CommandResult(int exitCode, DateTimeOffset startTime, DateTimeOffset exitTime)
{
    public int ExitCode { get; }
    public bool IsSuccess { get; }        // ExitCode == 0
    public DateTimeOffset StartTime { get; }
    public DateTimeOffset ExitTime { get; }
    public TimeSpan RunTime { get; }
}
```

3.10+ (PR #307) also adds `implicit operator int`/`bool` to `CommandResult`. Buffered model
(extension member on `Command`, `CliWrap.Buffered`): `ExecuteBufferedAsync` returns
`CommandTask<BufferedCommandResult>`; overloads: `(Encoding stdOut, Encoding stdErr,
CancellationToken forceful, CancellationToken graceful)`, `(Encoding, Encoding, CancellationToken
ct = default)`, `(Encoding, CancellationToken ct = default)`, `(CancellationToken ct = default)`
(last decodes with `Encoding.Default`); on non-zero exit the message also embeds stderr.

```csharp
public partial class BufferedCommandResult : CommandResult  // adds string StandardOutput, StandardError
{
    public void Deconstruct(out int exitCode, out string standardOutput, out string standardError); // 3.10+
    public static implicit operator string(BufferedCommandResult result); // = StandardOutput, 3.10+
}
```

Event-stream model (`CliWrap.EventStream`; static extensions in 3.7.1, C#14 `extension`
members in 3.10.1 — identical call syntax). `ListenAsync` (pull, `IAsyncEnumerable<CommandEvent>`)
and `Observe` (push, `IObservable<CommandEvent>`) each have four overloads: separate
stdout/stderr encodings with forceful + graceful tokens; a single-`ct = default` variant; a
single `Encoding`; or nothing (`Encoding.Default`). Events: `StartedCommandEvent.ProcessId`,
`StandardOutputCommandEvent.Text`, `StandardErrorCommandEvent.Text`,
`ExitedCommandEvent.ExitCode` — line-by-line, stdout/stderr as separate event types.

## 6. Exceptions

```csharp
public abstract class CliWrapException : Exception;              // CliWrap.Exceptions
public class CommandExecutionException : CliWrapException
{
    public ICommandConfiguration Command { get; }   // the command that failed
    public int ExitCode { get; }
}
```

- Non-zero exit (default validation): `CommandExecutionException` — "Command execution failed
  because the underlying process (name#id) returned a non-zero exit code (N). Command:
  `<target> <args>`. You can suppress this validation by calling `WithValidation(CommandResultValidation.None)` on the command."
- Failed start: wrapped `Win32Exception` ("Failed to start a process with file path '...' — see
  inner exception") or `InvalidOperationException` on Unix when the file is not executable.
- Unsupported platform features (credentials/resource policy): `NotSupportedException`.
- Cancellation: `OperationCanceledException` — "Command execution canceled. Underlying process (name#id) was forcefully/gracefully terminated." (carries the matching token).
- **3.10.1 only** (PR #308): if a forceful kill doesn't finish the process within the 3 s
  fallback, `TimeoutException` ("Failed to terminate the underlying process (name#id) within the allotted timeout."); 3.7.1 only reports cancellation.
- **No retry / rate-limit / backoff utilities exist in the library** (source-verified).

## 7. Encoding and cancellation

- Decoding uses the encoding passed to `PipeTarget.ToStringBuilder/ToDelegate`, `ListenAsync`,
  or `ExecuteBufferedAsync`; the `Process`-level encodings are never set. Default overloads use
  `Encoding.Default` (system ANSI/OEM on Windows — **not necessarily UTF-8**) with
  `detectEncodingFromByteOrderMarks: false` (applied verbatim; `PipeSource.FromString(s)` defaults to `Console.InputEncoding`).
- **No ANSI color filtering/stripping** — decoded lines pass through verbatim; for yt-dlp pass
  `--no-color` (or `-N`) **and** `Encoding.UTF8`.
- Forceful token → `Register(process.Kill)` → `Process.Kill(true)` = kill the **entire
  process tree** (SIGKILL on Unix, tree termination on Windows).
- Graceful token → `Register(process.Interrupt)` → SIGINT on Unix; on Windows a bundled helper
  executable (CliWrap.Signaler, deployed to a temp dir) sends a console Ctrl+C event — functionally
  equivalent to pressing Ctrl+C; cooperative, the process may ignore it.
- The single-token overloads (`ExecuteAsync(ct)`, `ListenAsync(ct)`, `Observe(ct)`,
  `ExecuteBufferedAsync(ct)`) are **forceful** (tree kill), not SIGINT; after forceful cancellation CliWrap waits for exit with a hard 3 s timeout (3.10.1: `TimeoutException`).

## 8. Recommended patterns for long-running yt-dlp downloads

1. Quick probe — binary presence/version:

```csharp
string version = "";
try { version = (await Cli.Wrap(ytdlpPath).WithArguments(["--version"])
    .ExecuteBufferedAsync(Encoding.UTF8, ct)).StandardOutput.Trim(); }
catch (CommandExecutionException ex) { /* ex.ExitCode, ex.Command */ } catch (Win32Exception) { /* not found */ }
```

2. Streaming download with live progress (yt-dlp writes progress to **stderr**):

```csharp
var cmd = Cli.Wrap(ytdlpPath)
    .WithArguments(args => { args.Add("-f"); args.Add("best"); args.Add(outPath); args.Add(url); })
    .WithStandardOutputPipe(PipeTarget.ToDelegate(l => StdOutHook(l), Encoding.UTF8))
    .WithStandardErrorPipe(PipeTarget.ToDelegate(l => ProgressHook(l), Encoding.UTF8));
await cmd.ExecuteAsync(forcefulCts.Token, gracefulCts.Token).ConfigureAwait(false);
```

3. Kill on cancellation (graceful SIGINT first, forceful tree-kill fallback — the official Readme pattern):

```csharp
using var gracefulCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
using var forcefulCts = new CancellationTokenSource();
await using var _ = ct.Register(() => forcefulCts.CancelAfter(TimeSpan.FromSeconds(3)));
try { await cmd.ExecuteAsync(forcefulCts.Token, gracefulCts.Token); }
catch (OperationCanceledException) { /* cancelled: already interrupted/killed */ } catch (CommandExecutionException ex) { /* ex.ExitCode */ }
```

## 9. Changelog delta 3.7.1 → 3.10.1 (official Release notes)

- **3.8** (2025-02-13): `PipeSource.FromCommand(command, transform)` overload (PR #275);
  resource policies: `ResourcePolicy` (Priority/Affinity/Min/MaxWorkingSet, all nullable) +
  `ResourcePolicyBuilder`/`WithResourcePolicy(...)`/`Command.ResourcePolicy` (PR #276).
- **3.8.2** (2025-03-04): process priority no longer set unless explicitly requested (PR #282).
- **3.9** (2025-06-08): `ExecuteAsync(Action<ProcessStartInfo>?, Action<Process>?, ...)` overload (PR #290); .NET Framework support dropped (PR #296); TFM set changed.
- **3.10** (2025-11-19): implicit conversions (`int`/`bool` on `CommandResult`, `string` on `BufferedCommandResult`) + `BufferedCommandResult.Deconstruct` (PR #307).
- **3.10.1** (2026-03-21): `TimeoutException` when the process can't be killed during
  cancellation (PR #308); `LibraryImport` for AOT (PR #323); CPM build changes. Internals (no
  public API change): C#14 `extension` members, stdin pipe cancellation rework (issue #74), output pipes no longer cancelled on process exit.
- **Breaking-change assessment: none for standard usage.** Only source-level nags:
  `CommandTask.Select` obsolete (warning); the full `Command` primary constructor gained a `resourcePolicy` parameter (positional callers would break — not realistic).

## Gotchas

- Old 2.x names (`ProcessWrapper`/`ProcessResult`/`ProcessException`) **do not exist in 3.x**; use `Command`/`CommandResult`/`CommandExecutionException`.
- Non-zero exit **throws** by default (`CommandResultValidation.ZeroExitCode`); for probes or
  "run and inspect" flows use `WithValidation(CommandResultValidation.None)` or catch.
- Default decoding is `Encoding.Default`, **not** UTF-8 on Windows — pass `Encoding.UTF8` for
  yt-dlp; add `--no-color` (no ANSI stripping is done).
- `WithArguments(string)` requires manual escaping; prefer the collection/builder overloads.
- All three streams are redirected by default — output does **not** appear in the parent
  console; pipe it yourself (or opt out via the 3.9+ `ExecuteAsync(configureStartInfo:)`).
- One token = forceful kill of the **process tree**; SIGINT requires the second (graceful)
  token. Windows graceful cancellation depends on the bundled Signaler helper executable.
- No timeout API — use `CancellationTokenSource.CancelAfter`; the library adds a 3 s kill grace
  period (then `TimeoutException` on 3.10.1). No retry/rate-limit helpers; yt-dlp has `--retries`, `--fragment-retries`, `--limit-rate`.
- `CommandTask.Select` is `[Obsolete]` in 3.10.1 (compiles with warning; prefer await); no sync
  `Execute()` (use `.GetAwaiter().GetResult()` on sync paths — deadlock risk); start failures throw **synchronously** at the call site, not in the task.

## Sources

- Tagged source (fetched + diffed locally): `github.com/Tyrrrz/CliWrap` tags `3.7.1`
  (commit 006be6c) and `3.10.1` — `CliWrap/CliWrap/`: `Cli.cs`, `Command.cs`, `Command.Execution.cs`,
  `CommandResult.cs`, `CommandTask.cs`, `CommandResultValidation.cs`, `PipeSource.cs`, `PipeTarget.cs`,
  `Command.PipeOperators.cs`, `ResourcePolicy.cs`, `Builders/`, `Exceptions/`, `Buffered/`,
  `EventStream/`, `Utils/ProcessEx.cs`, `CliWrap.csproj`.
- GitHub Release notes (de facto changelog): `github.com/Tyrrrz/CliWrap/releases` — 3.7.1, 3.8, 3.8.2, 3.9, 3.10, 3.10.1 (through 3.10.5 for context).
- Official Readme at tag 3.10.1 (usage, encoding, cancellation patterns, `CommandTask` process
  info); pipe operators (`Command | PipeTarget|Stream|StringBuilder|delegate`, stdout+stderr tuple
  variants, `PipeSource|...|Command | Command`) in `CliWrap/Command.PipeOperators.cs` — identical in both tags.
