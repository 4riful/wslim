# wslim

wslim is a small Windows app that looks inside your WSL distros for space that can safely be given back: package caches, build caches, old tool versions, old logs, Docker leftovers. It shows what it found, cleans what you tick, and then shrinks the WSL `.vhdx` disk files with `diskpart` so the space actually returns to your C: drive. It works with any WSL 2 distro (Ubuntu, Debian, Fedora, Arch, openSUSE, Alpine and others).

## Why

A WSL 2 distro lives in a virtual disk (`ext4.vhdx`). The file grows as you use it, but it does not shrink when you delete files inside Linux. After a year of `npm install`, `pip install`, Docker builds and editor updates, the disk can be many GB larger than what is actually stored in it. wslim removes the junk and then compacts the file.

## How it decides

- **safe**: rebuilds itself on demand (download caches, build caches, old tool versions, old logs). Checked by default.
- **review**: can be rebuilt, but costs time or bandwidth, or you may still want it (Go modules, Maven, AI models, unused Docker images, old `node_modules`). Unchecked by default, with an explanation.
- **never touched**: your data. Docker named volumes, databases, project source, git data, `~/.config`, SSH/GPG keys, sessions and logins.

The full list of rules is in [docs/RULES.md](docs/RULES.md).

![wslim](assets/screenshot.png)

## Install

Download `wslim.exe` from the [Releases](../../releases) page and run it.

- Requires Windows 10 or 11 with WSL 2.
- .NET Framework 4.8 is built into Windows; nothing else to install.
- No installer. No administrator rights needed, except for the shrink step, which asks once through UAC.

## Usage

1. Click **Scan**. wslim scans each distro (starting it if needed) and lists what it can clean, with sizes.
2. Review the list. Safe items are ticked; review items are not. Each item says what it is and how it comes back.
3. Optionally click **Preview** to see the exact commands, without changing anything.
4. Click **Clean**. Only the exact folders shown in the scan and ticked are removed; anything that appeared after the scan is left alone. Hover an item to see its paths.

**Shrink disks afterwards** (on by default) runs after cleaning. It shuts WSL down (`wsl --shutdown`), so terminals, VS Code, Claude Code and containers running in WSL will close; save your work first. It then runs `diskpart` (`compact vdisk`) on each disk after one UAC prompt. Disks of Docker Desktop, Rancher Desktop and Podman are only shrunk, never cleaned, and are skipped while Docker Desktop is running. Disks that are sparse files are skipped, because Windows shrinks those itself.

## What it never touches

- Docker named volumes, running or used containers, images in use
- Databases
- Project source code and git data
- `~/.config`, SSH and GPG keys, sessions, logins and tokens (also the ones some tools keep in `~/.cache`)
- Symbolic links, and any path outside the home folders and the known system cache folders; the agent refuses to delete anything else

## Logs

Everything wslim does is written to `%LOCALAPPDATA%\wslim`:

- `logs\wslim-YYYY-MM-DD.log`: every command run in each distro and its output
- `history.tsv`: one line per clean, with the space freed
- `crash.log`: details of unexpected errors

The **Log folder** link in the app opens this folder.

## Build from source

On Windows, run `build.cmd`. It uses the C# compiler that ships with Windows (.NET Framework 4.8), so no SDK is needed. The result is `bin\wslim.exe`.

From inside WSL, run `./build.sh`. It copies the sources to a temp folder on C: and runs `build.cmd` there.

`src/App.cs` must stay C# 5 (the built-in `csc.exe` does not support newer syntax).

## Adding a rule

The cleanup rules live in `src/agent.sh`, a POSIX `sh` script that runs inside each distro. See [docs/RULES.md](docs/RULES.md#how-to-add-a-rule).

## License

MIT. See [LICENSE](LICENSE).
