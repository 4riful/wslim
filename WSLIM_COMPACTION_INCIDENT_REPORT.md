# WSLIM compaction incident report

Status: read-only investigation complete. No implementation file was changed. Nothing was run against
`UbuntuRestored` or its VHDX. Evidence read: the repository at `bb2c828` (v1.1.3), the app's own log
(`%LOCALAPPDATA%\wslim\logs\wslim-2026-11-27.log`), `history.tsv`, and the generated DiskPart files in
`%TEMP%\wslim\`.

Each finding is marked **Confirmed** (proved by source, log or documentation) or **Hypothesis**.

---

## 1. Executive incident summary

On 2026-11-27 at 01:45:10, wslim's shrink step attached `C:\WSL\UbuntuRestored\ext4.vhdx` to Windows, failed on the
next command, and exited without detaching it. Windows kept the disk attached (`\\.\PhysicalDrive1`), so WSL could
not open it (`ERROR_SHARING_VIOLATION`) until it was dismounted by hand with `Dismount-DiskImage`.

- **Cause:** the generated DiskPart script contains `compact vdisk noerr`. `compact vdisk` takes no arguments, so
  DiskPart rejected the line and stopped the script before `detach vdisk`.
- **Scope:** every published version, v1.0.0 through v1.1.3, GUI and CLI. The shrink step can never have succeeded
  in any release; on every machine it attaches the disk and leaves it attached.
- **Data:** no evidence of data loss. The disk was attached read-only, and the distro started normally after the
  manual dismount.
- **How it shipped:** the line was added during a review fix before v1.0.0 and the shrink path was never executed
  end to end before release. The release notes said so ("I haven't run a real clean and shrink"), and it was
  released anyway. That was the wrong call for code that touches a virtual disk.

**Exposure that needs a decision from you (nothing done yet):** the broken builds are public: five GitHub releases,
the Scoop bucket, an open winget PR (#490404), an Awesome-WSL PR (#134), comments on microsoft/WSL #4699 and #12103,
and the dev.to article. Other users who run "Shrink" will hit the same lock-out. Containment options are in
section 9.

---

## 2. Exact source-level root cause

**Confirmed.** `src/Core.cs:342` writes this script for each disk:

```
select vdisk file="C:\WSL\UBUNTU~1\EXT4~1.VHD"
attach vdisk readonly
compact vdisk noerr
detach vdisk noerr
exit
```

(The file `%TEMP%\wslim\shrink-0.txt` on the affected machine matches byte for byte.)

1. `compact vdisk` has no parameters. Microsoft's syntax block is just `compact vdisk`; `noerr` is documented for
   `attach vdisk` and `detach vdisk` only.
2. DiskPart treats `compact vdisk noerr` as a syntax error. The log shows: "The arguments specified for this command
   are not valid. For more information on the command type: HELP COMPACT VDISK". Exit code `-2147024809` is
   `0x80070057` (`E_INVALIDARG`).
3. Microsoft documents that in script mode DiskPart "stops processing the script" on an error, and that it "always
   returns errors when it encounters syntax errors, regardless of whether you used the noerr parameter". So the
   script ended after `attach vdisk readonly` succeeded and `detach vdisk noerr` never ran.
4. The disk stayed attached to the Windows host. WSL then failed with `HCS/ERROR_SHARING_VIOLATION`.

**Origin (confirmed by `git log -S`):** the `noerr` on `compact vdisk` first appears in commit `d1db4a7` (v1.0.0).
It was added while fixing a review finding ("if compact fails after attach, detach never runs"). The fix intended to
make detach always run and instead guaranteed it never runs. The earlier PowerShell prototype used plain
`compact vdisk` and worked (69.8 GB → 42.6 GB), which is why the first manual run succeeded and the released app did
not.

---

## 3. Existing execution flow

All compaction goes through one function, `Core.Shrink` (`src/Core.cs:324-373`). Three call sites:

| Caller | Location |
|---|---|
| GUI "Clean" with "Shrink disks afterwards" | `src/App.cs:373` |
| CLI `clean --shrink` | `src/Cli.cs:306` |
| CLI `shrink` | `src/Cli.cs:345` |

Flow inside `Core.Shrink`:

| Step | Lines | What it does |
|---|---|---|
| 1 | 327 | Runs `wsl.exe --shutdown` (60 s timeout); the exit code is ignored. |
| 2 | 328-329 | Polls `wsl --list --running` up to 30 times, then sleeps 3 s. Continues even if distros are still running. |
| 3 | 331-344 | Writes one script per disk to `%TEMP%\wslim\shrink-N.txt` (OEM code page, 8.3 path via `ShortPath`). |
| 4 | 345, 350-352 | Starts one elevated `cmd.exe /c diskpart /s a >> out 2>&1 & diskpart /s b >> out 2>&1` (`Verb = "runas"`, one UAC prompt). |
| 5 | 354 | `p.WaitForExit()` with no timeout. |
| 6 | 355-358 | Copies DiskPart's output file into the log. |
| 7 | 359 | Non-zero exit code → logs "diskpart reported an error" and returns `false`. |
| 8 | 362-366 | UAC declined (error 1223) or start failure → returns `false`. |
| 9 | 367-372 | On success, logs old → new file size and returns `true`. |

After `Shrink` returns, the callers do the same thing regardless of the result:

- GUI: `App.cs:381-384` logs `"Done: " + summary`, writes history, and sets the banner to "Finished with errors · …"
  or "Done · …".
- CLI: `Cli.cs:353-358` (`Finish`) logs `"Done: " + summary` and prints "Finished with errors: …" or "Done: …".

There is no code anywhere that checks whether a VHDX is attached, detaches one, or records that an operation is in
progress.

---

## 4. Failure and safety gap analysis

### 4.1 Direct causes of this incident (all confirmed)

| # | Gap | Location |
|---|---|---|
| G1 | Invalid `noerr` on `compact vdisk` makes the script stop after attach. | `Core.cs:342` |
| G2 | Detach depends on a later line of the same script. Any failure between attach and detach leaves the disk attached. | `Core.cs:342` |
| G3 | No recovery after a non-zero exit: the function logs and returns. No independent detach, no state check. | `Core.cs:359` |
| G4 | No postcondition check that the disk is detached and openable. | `Core.cs:367-372` |
| G5 | The attach is unnecessary. Microsoft: "You can only use compact dynamically expanding VHDs that are detached or attached as read-only." | `Core.cs:342` |

### 4.2 Why the app said "Done" after failing (confirmed)

- `App.cs:382` and `Cli.cs:354` always write `Done: disks 44.6 GB → 44.6 GB, freed 0 on C:` to the log. The `failed`
  flag only changes the banner text and adds "(with errors)" to `history.tsv`.
- `Core.Summary` (`Core.cs:382-389`) computes sizes from file lengths only, so a failed compaction and a successful
  one that reclaimed nothing produce the same sentence.
- The state model has two values (done / finished with errors). It has no "failed", "recovery required" or "no
  space reclaimed".

### 4.3 Why preview said "finished" after failing (confirmed)

Log, 01:45:28-01:45:30: the preview's `wsl.exe` call failed with the sharing violation, `CleanDistro` returned `-1`
(`Core.cs:289-290`), and then:

- `App.cs:369` logs "Preview finished. Nothing was changed." unconditionally.
- `App.cs:388` sets the status bar to the same text unconditionally. `failed` is not consulted for previews.
- `App.cs:368` still logs "Would then shut WSL down and shrink: UbuntuRestored".
- CLI: `Cli.cs:300` logs the same sentence, though it does return exit code 1 (`Cli.cs:302`).

Preview itself is non-mutating (the agent runs with `DRY=1`), but note that a preview **starts the distro** if it is
stopped, because it runs `wsl.exe -d NAME`.

### 4.4 Why repeated attempts made things worse (confirmed from the log)

After the first failure the user clicked Clean three more times (01:46:00, 01:46:21, 01:46:57). Each run:

- ran `wsl --shutdown` again,
- asked for elevation again and ran DiskPart again, which failed at `attach` ("being used by another process"),
- logged "Done" again.

Nothing detected that the target disk was already attached, and nothing stopped further operations. The repeats did
not add damage in this case, but each one re-ran an elevated disk operation against a disk in an unknown state.

One unexplained detail: runs 2-4 log the same exit code `-2147024809` although the failure was a sharing violation
(expected `0x80070020`). **Hypothesis:** DiskPart maps script failures to one generic code. Not verified; it does
not change the analysis.

### 4.5 Other failure paths found in the same code (confirmed by reading; not yet triggered)

| # | Gap | Location | Consequence |
|---|---|---|---|
| G6 | Shutdown is not verified. The loop gives up after 30 s and proceeds. | `Core.cs:327-329` | DiskPart may run while WSL still holds the disk. |
| G7 | `RunningNames` returns an empty set when `wsl.exe` fails or times out, which reads as "nothing running". | `Core.cs:100-111` | A broken `wsl.exe` is treated as a clean shutdown. |
| G8 | `p.WaitForExit()` has no timeout and there is no cancellation. | `Core.cs:354` | A hung DiskPart hangs the app; closing the app leaves an elevated DiskPart running with the disk attached. |
| G9 | The exit code of `cmd /c a & b` is the last command's only. | `Core.cs:345` | With several disks, an early failure is reported as success. |
| G10 | Multiple DiskPart runs are chained back to back. Microsoft: "allow at least 15 seconds between each script". | `Core.cs:343-345` | Possible failures on multi-disk machines. |
| G11 | No lock across processes. `busy` (`App.cs:321`) only guards one window. | `App.cs`, `Cli.cs` | GUI + CLI, or two windows, can compact the same disk at once. |
| G12 | No persistent record of an operation in progress. | — | After a crash, kill or power loss nothing knows a disk may be attached. |
| G13 | The app is not elevated, so it cannot detach or inspect the disk itself after the elevated child fails. | `Core.cs:350-351` | Recovery needs a second elevation that the code never requests. |
| G14 | `ShortPath` creates an empty file if the path does not exist. | `Core.cs:315` | A wrong VHDX path would create a 0-byte file at that path instead of failing. |
| G15 | The disk identity is not validated: no check that the path is a `.vhdx`, is the path registered for that distro at execution time, or is not a reparse point. | `Core.cs:338-342` | Path changes between scan and shrink go unnoticed. |
| G16 | `Wsl.Refresh` after the shrink (`App.cs:375`) calls `wsl --list --running`. **Hypothesis:** harmless (it does not start a distro), but it runs `wsl.exe` while the disk state is unknown. | `App.cs:375` | To be confirmed. |

### 4.6 Not a cause

- The cleanup inside Linux worked as designed (log 01:44:58-01:45:01: three safe items removed, 698 MB freed).
- `fstrim` ran correctly.
- No Linux-side rule touched anything outside the selected paths.

---

## 5. Official documentation verification

All four requested pages were fetched on 2026-11-27, plus DiskPart's scripting page.

| Source | What it says | Effect on the design |
|---|---|---|
| [compact vdisk](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/compact-vdisk) | Syntax is exactly `compact vdisk`, no parameters. "You can only use compact dynamically expanding VHDs that are detached or attached as read-only." A VHD must be selected first. | `noerr` is invalid here. Attaching is optional. |
| [attach vdisk](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/attach-vdisk) | `attach vdisk [readonly] { [sd=<SDDL>] \| [usefilesd] } [noerr]`. Without `noerr`, "an error causes DiskPart to exit with an error code". | Confirms script abort behaviour. |
| [detach vdisk](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/detach-vdisk) | `detach vdisk [noerr]`. A VHD must be selected first. | An independent detach script needs its own `select vdisk`. |
| [diskpart scripts](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/diskpart-scripts-and-examples) | On error DiskPart "stops processing the script". Syntax errors are returned "regardless of whether you used the noerr parameter". Allow 15 s between successive scripts. | Explains G1, G2, G10. |
| [WSL disk space](https://learn.microsoft.com/en-us/windows/wsl/disk-space) | Covers expanding only (`wsl --manage --resize`, `expand vdisk`). Says reducing "is much more complicated". Does not document compacting, sparse VHD or `Optimize-VHD`. Warns against touching WSL files in `AppData` with Windows tools. | Microsoft gives no official WSL shrink procedure; wslim relies on the generic VHD documentation. |

**Not documented anywhere I found:** numeric DiskPart exit codes; whether `compact vdisk` on a *detached* VHDX
reclaims as much as on a read-only attached one. The second point matters and is listed under unresolved questions.

---

## 6. Proposed corrected architecture

Principle: do not attach the disk at all. If nothing is attached, nothing can be left attached.

### 6.1 Compaction without attachment (primary design)

Per disk, the script becomes:

```
select vdisk file="<path>"
compact vdisk
```

No `attach`, no `detach`, no `noerr`. This is the documented "detached" mode. A failure of either line leaves the
host exactly as it was.

This must be proved on a disposable VHDX before it is adopted (see Q1 in section 9). If detached compaction turns
out not to reclaim space, the fallback is 6.5, never the current script.

### 6.2 One elevated worker with its own verification

Today the unelevated app launches `cmd.exe` elevated and can do nothing afterwards (G13). Proposed: the app launches
**itself** elevated in a hidden worker mode (`wslim-cli.exe --compact-worker <job-file>`), still one UAC prompt. The
worker does everything that needs rights and writes a result file the app reads:

1. **Preflight, per disk**
   - Re-read the distro's registry entry; confirm the path is unchanged since the scan, exists, ends in `.vhdx`, is
     a regular file (not a reparse point), and is not sparse.
   - Confirm WSL is stopped: `wsl --shutdown` exit code 0, then `wsl --list --running` succeeds *and* is empty. A
     failed query counts as "unknown", which stops the operation (fixes G6, G7).
   - Query attachment state (`Get-DiskImage -ImagePath` → `Attached`, or `GetVirtualDiskInformation`).
     If the disk is already attached, wslim did not attach it: stop with **Recovery required**, show who to ask
     (the two commands from the incident), and do not detach it.
   - Try to open the file exclusively (`FileShare.None`) and close it again. If that fails, something else holds
     it: stop with **Failed**, change nothing.
2. **Compact** with a timeout scaled to disk size. One DiskPart process per disk, each exit code checked (fixes G9),
   15 s apart (fixes G10).
3. **Postcondition, per disk**: attachment state is "not attached", the exclusive-open probe succeeds, and the file
   size before/after is recorded.
4. On timeout: do not start recovery while DiskPart is still running. Terminate DiskPart (the worker's own child,
   same elevation), wait for exit, then run the postcondition check. If the state cannot be verified, the result is
   **Recovery required**.

No automatic killing of other processes and no detaching of disks wslim did not attach.

### 6.3 Operation journal and locking

- **Lock:** a named mutex (`Global\wslim-compact`) plus a per-disk lock file; a second request for the same disk is
  refused with a clear message (fixes G11).
- **Journal:** before the worker starts, write `%LOCALAPPDATA%\wslim\operation.json` (disk paths, sizes, phase,
  PID, time). Update the phase as it proceeds; delete only after the postcondition passes.
- **On every start** (GUI and CLI): if a journal exists, the previous run did not finish. Do not scan, clean or
  shrink. Show **Recovery required** with the recorded disks and run the read-only state check. Offer the fix only
  if the journal shows wslim created the attachment (possible only in fallback mode 6.5).
- A `finally` block does not cover process kill, power loss or a crash of the elevated worker. The journal is what
  covers those.

### 6.4 Outcome model and reporting

Replace the boolean `failed` with one outcome per disk and one overall:

| Outcome | Meaning |
|---|---|
| Success | Compaction ran, postcondition passed, file got smaller. |
| No space reclaimed | Compaction ran, postcondition passed, size unchanged. |
| Partial success | Some disks succeeded, others did not; or Linux cleanup succeeded and compaction did not. |
| Failed | Nothing was changed on the host; disk state verified safe. |
| Cancelled | User declined UAC or cancelled before compaction started. |
| Recovery required | Disk state is unsafe or could not be verified. All further operations are blocked. |

- The log line and banner use the outcome word. "Done" is never written for a failure.
- Two separate numbers: "freed inside Linux" (from the agent) and "returned to Windows" (file size delta, only
  reported when compaction succeeded).
- **Recovery required** shows the exact commands, copyable:
  `wsl --shutdown`, `Get-DiskImage -ImagePath "<path>" | fl Attached,DevicePath`,
  `Dismount-DiskImage -ImagePath "<path>"`.
- **Bring WSL back afterwards (requested by the owner on 2026-11-27).** Today the app shuts WSL down and leaves it
  off, and nothing in the app works again until a distro is started. New behaviour: after the postcondition check
  passes, wslim starts the distros that were running before the shutdown (`wsl -d NAME -- true`) and reports
  "WSL is running again" or the exact error. The consent dialog says this up front. If the disk state is not
  verified safe, wslim does **not** start anything and shows Recovery required instead, because starting WSL
  against a locked disk is what produced the error in this incident.
- After a failed start of a distro (scan, preview or clean), the app says "could not start <distro>" with the WSL
  error, and offers the read-only disk-state check, instead of continuing as if the distro were empty.

### 6.5 Fallback only if 6.1 does not reclaim space

Attach read-only, compact, detach, as three **separate** DiskPart invocations controlled by the worker:

- record "attached by wslim" in the journal before attaching;
- run detach as its own script (`select vdisk` + `detach vdisk`) whether compact succeeded or not;
- verify detached state afterwards; if detach fails, retry once with `Dismount-DiskImage`, then
  **Recovery required**.

### 6.6 Preview and scan behaviour

- Preview never launches the worker, never shuts WSL down, never requests elevation.
- If any distro's scan or preview call fails, the result is "Preview failed for <distro>", not "Preview finished".
  No "would then shrink" line is shown for a distro that could not be reached.
- The UI states that scan and preview start a stopped distro.
- The stop-WSL consent names every running distro and says that `wsl --shutdown` stops **all** of them, including
  ones wslim is not shrinking.

---

## 7. Files and functions requiring modification

| File | Change |
|---|---|
| `src/Core.cs` | Replace `Shrink` (324-373) with the worker protocol: preflight, compact, postcondition, outcome. Remove the file-creating side effect in `ShortPath` (315). Make `RunningNames` (100-111) return success/failure separately from the list. Replace `Summary` (382-389) with the outcome model. Add journal, lock, and disk-state query. |
| `src/Cli.cs` | Add hidden `--compact-worker` mode. Replace `Finish` (351-360) and the preview ending (297-303) with outcome reporting. Startup journal check. |
| `src/App.cs` | `Clean` (319-390): outcome banner, preview failure handling, block on Recovery required. `Start`: journal check. Consent text (336-345). |
| `src/Compact.cs` (new) | Worker logic behind a small process-runner interface, so it can be tested with a fake runner. |
| `tests/` (new) | Test executable built by the same `csc` (no new dependencies). |
| `build.cmd`, `.github/workflows/build.yml` | Build and run tests; integration job on `windows-latest` with a disposable VHDX. |
| `README.md`, `docs/RULES.md` | Correct the description of the shrink step; add a recovery section. |

No UI redesign and no change to the Linux rule catalog (`src/agent.sh`).

---

## 8. Regression test matrix

Unit tests use a fake process runner that returns scripted exit codes, output and delays. Integration tests create
a throw-away expandable VHDX in a temp folder on the CI runner (`create vdisk file=… maximum=64 type=expandable`);
they never touch a WSL distro.

| # | Scenario | Type | Expected |
|---|---|---|---|
| 1 | Successful compaction | Integration | Success; file smaller; not attached; journal removed. |
| 2 | Invalid compaction arguments (the incident script) | Unit + integration | Failed; disk not attached afterwards. The generated script is asserted to contain exactly `select vdisk` and `compact vdisk`, and no `noerr`. |
| 3 | Non-zero subprocess exit code | Unit | Failed; postcondition check runs; no "Done"/"Success" text. |
| 4 | Attach succeeds, compact fails (fallback mode) | Unit + integration | Independent detach runs; verified detached; Failed. |
| 5 | Detach/cleanup fails | Unit | Recovery required; further operations blocked; commands shown. |
| 6 | Subprocess timeout | Unit | Child terminated first, then state checked; Failed or Recovery required; no hang. |
| 7 | User cancels UAC | Unit | Cancelled; WSL shutdown reported as already done; nothing else changed. |
| 8 | App killed mid-operation, then restarted | Unit (journal file planted) | Start shows Recovery required; scan/clean/shrink disabled until resolved. |
| 9 | Disk already attached by something else | Integration (pre-attach the test VHDX) | Stop before compaction; disk still attached afterwards; never detached by wslim. |
| 10 | `wsl --shutdown` fails or distros still running | Unit | Failed before any disk command. |
| 11 | Two concurrent compaction requests | Unit + integration (two processes) | Second is refused; first unaffected. |
| 12 | Several disks, one fails | Unit | Partial success; per-disk outcomes; each exit code checked. |
| 13 | Compaction succeeds, zero bytes reclaimed | Unit | "No space reclaimed", distinct from Failed. |
| 14 | Preview when the distro cannot start | Unit | "Preview failed"; no shrink line; no worker, no shutdown, no elevation requested. |
| 15 | Paths with spaces and non-ASCII characters | Integration (`…\Jösé test\disk ü.vhdx`) | Success; correct file compacted. |
| 16 | Status and log text for every outcome | Unit | Banner, log line, `history.tsv` and CLI exit code agree for all six outcomes. |
| 17 | VHDX path changed between scan and shrink | Unit | Failed at preflight. |
| 18 | Sparse disk | Unit | Skipped with explanation; no disk command issued. |

Release gate: the integration job must pass on CI, and one manual run on a disposable WSL distro (created for the
purpose, never `UbuntuRestored`) must pass before any release.

---

## 9. Implementation risks and unresolved questions

**Decisions needed from you now (containment of the published builds).** Nothing below has been done.

| Option | Effect |
|---|---|
| A. Mark all five GitHub releases as broken and remove their binaries (recommended) | Stops new downloads of the faulty shrink. Reversible. |
| B. Add a warning to the README, the dev.to article and the two WSL issue comments | Tells people who already downloaded it not to use Shrink, with the `Dismount-DiskImage` recovery. |
| C. Close or pause the winget PR (#490404) and Awesome-WSL PR (#134); empty the Scoop bucket | Prevents distribution through package managers until fixed. |
| D. Leave everything until the fix ships | Other users can be locked out of WSL the same way. |

**Unresolved technical questions**

| # | Question | How it gets answered |
|---|---|---|
| Q1 | Does `compact vdisk` on a *detached* VHDX reclaim the same space as read-only attached? | **Answered 2026-11-27 (GitHub Actions run on `windows-latest`, disposable 1 GB VHDX files, branch `experiment/compact-modes`).** A disk with no Windows-readable filesystem (as ext4 appears to Windows), 400 MB written then 300 MB zeroed: 420 MB → 132 MB detached, 420 MB → 132 MB attached read-only. Identical, so the detached design (6.1) is adopted. The same run reproduced the incident: the released script exited `-2147024809` and left the test disk attached (`Attached = True`). Not yet measured on a real ext4 disk trimmed by WSL. |
| Q2 | Can attachment state be read without elevation on Windows Home (`Get-DiskImage`, or `OpenVirtualDisk` + `GetVirtualDiskInformation`)? | Test on Home. If not, the startup check needs elevation only when a journal exists. |
| Q3 | Is the exclusive-open probe a reliable "nothing holds this disk" signal for a VHDX the VM service has released? | Integration test. |
| Q4 | Why did runs 2-4 return `E_INVALIDARG` for a sharing violation? | Capture DiskPart exit codes in tests; cosmetic. |
| Q5 | Should the direct API (`CompactVirtualDisk` in `virtdisk.dll`) replace DiskPart later? It removes script parsing entirely but adds P/Invoke code. | Out of scope for this fix; revisit after. |

**Risks**

- The fix adds an elevated worker mode to `wslim-cli.exe`; its argument handling must reject anything but a job
  file wslim wrote itself.
- Compaction time on large disks is unknown, so the timeout must be generous and visible.
- Antivirus or backup software can hold a VHDX briefly; the preflight must report that as "in use", not retry in a
  loop.

---

## 10. Acceptance criteria

1. The generated disk script contains no `noerr` on `compact vdisk`, asserted by a test.
2. In the primary design no `attach vdisk` is ever issued. In fallback mode, detach is a separate invocation and
   its result is verified.
3. After any outcome (success, failure, timeout, cancel) the target VHDX is verified not attached and openable, or
   the outcome is **Recovery required**.
4. A disk attached by something else is never detached by wslim.
5. The words "Done", "Success" or "finished" never appear for a failed operation, in the log, banner, history or
   CLI output. All six outcomes are distinguishable.
6. "Freed inside Linux" and "returned to Windows" are reported separately.
7. A failed scan or preview is reported as failed; preview never shuts WSL down, elevates or runs a disk command.
8. A second concurrent compaction of the same disk is refused.
9. After a simulated kill mid-operation, the next start blocks all operations and shows recovery steps.
10. All tests in section 8 pass on CI, including the disposable-VHDX integration job.
11. One full manual run on a disposable WSL distro passes before release; `UbuntuRestored` is never used for testing.
12. No commit, push, release or post without your explicit approval.
