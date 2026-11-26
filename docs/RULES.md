# Rules

Every rule lives in `src/agent.sh`. The app pipes that script into each distro as root
(`wsl.exe -d NAME -u root -- sh -s scan|clean ids|preview ids`). An item only appears when it exists; the app hides items under 1 MB.

Paths starting with `~/` are checked in every home folder: `/root` and each `/home/*`.

- **safe**: rebuilds itself on demand. Checked by default.
- **review**: can be rebuilt, but costs time or bandwidth, or may still be wanted. Unchecked by default.

"deleted" means `rm -rf` of the listed paths, through a guard (`remove_selected`) that:

- deletes a path only if it was in the scan the user saw **and** was ticked **and** the rule still finds it now
  (anything new since the scan is left alone);
- refuses symbolic links, names with tabs or newlines, and anything outside `/root/*`, `/home/*/*`, `/var/cache/*`,
  `/var/lib/snapd/snaps/*`, `/var/crash/*` and `/var/lib/systemd/coredump/*`.

After every clean the agent runs `fstrim -av` (when available) so the freed blocks can be released to Windows.

## Safe

| id | what it is | how it is cleaned | how to get it back |
|---|---|---|---|
| `pkg-apt` | apt package cache (`/var/cache/apt/archives`) | `apt-get clean` | apt downloads packages again when installing |
| `pkg-dnf` / `pkg-yum` | dnf / dnf5 / yum package cache (`/var/cache/dnf`, `/var/cache/libdnf5`, `/var/cache/yum`) | `dnf clean packages` / `yum clean packages` | downloaded again when installing |
| `pkg-pacman` | pacman package cache (`/var/cache/pacman/pkg`) | `pacman -Scc --noconfirm` | pacman downloads packages again when installing |
| `pkg-zypper` | zypper package cache (`/var/cache/zypp/packages`) | `zypper clean --all` | zypper downloads packages again when installing |
| `pkg-apk` | apk package cache (`/etc/apk/cache`, only when enabled) | `apk cache clean` | apk downloads packages again when installing |
| `pip` | pip download cache (`~/.cache/pip`) | deleted | automatic on next `pip install` |
| `uv` | uv cache (`~/.cache/uv`); size counts only files not hard-linked into environments | deleted | automatic on next `uv` run; existing environments keep working |
| `npm` | npm cache, npx installs, npm logs (`~/.npm/_cacache`, `~/.npm/_npx`, `~/.npm/_logs`) | deleted | automatic on next `npm install` / `npx` |
| `yarn` | Yarn cache (`~/.cache/yarn`, `~/.yarn/berry/cache`) | deleted | automatic on next `yarn install` |
| `pnpm` | pnpm metadata cache (`~/.cache/pnpm`); the package store is not touched | deleted | automatic on next `pnpm install` |
| `bun` | Bun cache (`~/.bun/install/cache`) | deleted | automatic on next `bun install` |
| `deno` | Deno download cache (`~/.cache/deno/deps`, `gen`, `npm`, `remote`); Deno KV and localStorage (`location_data`) are kept | deleted | automatic on next `deno` run |
| `gobuild` | Go build cache (`~/.cache/go-build`) | deleted | rebuilt on the next `go build` |
| `cargo` | Cargo download cache (`~/.cargo/registry/cache`, `~/.cargo/registry/src`) | deleted | automatic on next `cargo build` |
| `composer` | Composer cache (`~/.cache/composer`) | deleted | automatic on next `composer install` |
| `nugethttp` | NuGet HTTP cache (`~/.local/share/NuGet/http-cache`, `~/.local/share/NuGet/v3-cache`) | deleted | automatic on next `dotnet restore` |
| `nodegyp` | node-gyp headers (`~/.cache/node-gyp`) | deleted | downloaded again on the next native build |
| `precommit` | pre-commit hook environments (`~/.cache/pre-commit`) | deleted | pre-commit recreates them on the next run |
| `selenium` | Selenium drivers (`~/.cache/selenium`) | deleted | Selenium Manager downloads them again |
| `tscache` | TypeScript and Prisma caches (`~/.cache/typescript`, `~/.cache/prisma`) | deleted | downloaded again when needed |
| `thumbs` | thumbnails (`~/.cache/thumbnails`) | deleted | regenerated when images are viewed |
| `vscodelogs` | VS Code / Cursor server logs (`~/.vscode-server/data/logs`, `~/.cursor-server/data/logs`) | deleted | not needed; new logs are written |
| `toolversions` | old Claude Code versions (`~/.local/share/claude/versions`, newest 2 folders kept), old VS Code / Cursor server builds (`~/.vscode-server/bin`, `~/.vscode-server/cli/servers`, `~/.cursor-server/...`, newest folder kept). Only folders count, and any build a running process uses is always kept | deleted | the newest versions are kept; an editor downloads its server again if needed |
| `journal` | systemd journal above 100 MB (`/var/log/journal`) | `journalctl --vacuum-size=100M` | not recoverable; the newest 100 MB are kept |
| `coredumps` | crash dumps (`/var/lib/systemd/coredump/*`, `/var/crash/*`) | deleted | not needed |
| `tmp` | files in `/tmp` and `/var/tmp` not modified, changed or read for 10+ days (files only; `systemd-private-*`, `tmux-*`, `.X11-unix` and names with newlines are skipped) | `find ... -exec rm -f {} +` | not needed |
| `snaps` | disabled snap revisions kept after updates | `snap remove NAME --revision=REV` for each disabled revision | `snap revert NAME` is no longer possible; the current revision stays |
| `docker-buildcache` | Docker build cache | `docker builder prune -af` | rebuilt on the next `docker build` (slower once) |
| `docker-dangling` | untagged (`<none>`) Docker images | `docker image prune -f` | rebuilt by building again |

## Review

| id | what it is | how it is cleaned | how to get it back |
|---|---|---|---|
| `gomod` | Go module cache (`~/go/pkg/mod`) | deleted | downloaded again on the next `go build` |
| `gradle` | Gradle caches (`~/.gradle/caches`) | deleted | downloaded and rebuilt on the next Gradle build |
| `maven` | Maven repository (`~/.m2/repository`) | deleted | downloaded again on the next Maven build |
| `nuget` | NuGet packages (`~/.nuget/packages`) | deleted | downloaded again on the next `dotnet restore` |
| `playwright` | Playwright browsers (`~/.cache/ms-playwright`) | deleted | `npx playwright install` |
| `puppeteer` | Puppeteer browsers (`~/.cache/puppeteer`) | deleted | reinstalled on the next `npm install` |
| `huggingface` | Hugging Face models and datasets (`~/.cache/huggingface/hub`, `datasets`, `xet`, `assets`); the login token is kept | deleted | downloaded again when a model is loaded (can be many GB) |
| `torch` | PyTorch hub cache (`~/.cache/torch`) | deleted | downloaded again when a model is loaded |
| `docker-unused` | Docker images no container (running or stopped) uses | `docker image prune -af` | `docker pull` or `docker build` again |
| `docker-stopped` | stopped and created Docker containers | `docker container prune -f` | recreate with `docker run` / `docker compose up`; their named volumes stay |
| `docker-anonvolumes` | unnamed Docker volumes no container (running or stopped) uses; they can hold database data from deleted containers | `docker volume rm ID`, only for the volumes listed in the scan, and only if still unused; done before any container is removed | not recoverable; named volumes are never touched |
| `stale-node` | `node_modules` of projects with a `package.json` where nothing in the project changed for 60+ days (up to 6 levels deep in each home, hidden folders skipped) | deleted | `npm install` in the project |
| `stale-venv` | `.venv` / `venv` (with `pyvenv.cfg`) of projects with `requirements.txt`, `pyproject.toml`, `setup.py` or `Pipfile` where nothing changed for 60+ days | deleted | recreate from the project's requirements |
| `cache:PATH` | each other folder in `~/.cache` of 50 MB or more, one item per folder. Never offered: logins, shell history, keyrings, tokens, `gh`, `claude`, `pypoetry` (Poetry environments), `lm-studio` (models), `JetBrains`, and any name containing history, token, secret, cred, ssh, gnupg or session | deleted | that program recreates its cache; close it first |

Docker rules are only offered for a Docker engine running inside the distro, when `docker info` answers within 8 seconds.
If Docker is not running, or the engine is Docker Desktop's shared engine, the scan prints a note instead; Docker Desktop's
disk is only shrunk.

## Never touched

- Docker named volumes, running containers, images used by any container
- Databases (`/var/lib/postgresql`, `/var/lib/mysql`, and so on)
- Project source and git data
- `~/.config`, `~/.ssh`, `~/.gnupg`, sessions and logins
- Hidden folders in a home, except the exact cache paths listed above
- Logins and tokens, even when stored under `~/.cache` (Hugging Face token, `gh`, Claude, SSH agent files)
- The Deno KV / localStorage data in `~/.cache/deno/location_data`
- Anything outside the allowed roots of the delete guard

## How to add a rule

### A plain cache folder

Add one line to the `DIRS` table in `src/agent.sh`:

```
id|tier|title|why|relative paths (space separated)
```

- `id`: short, unique, lowercase, no spaces. It is what the app passes back to `clean`.
- `tier`: `safe` only if the tool rebuilds it with no user action. Otherwise `review`.
- `title`: shown in the list.
- `why`: one sentence that says what it is and how it comes back. No `|` characters.
- `relative paths`: paths relative to each home folder, for example `.cache/foo .foo/cache`.

If the folder is under `~/.cache`, also add its folder name to `CACHE_SKIP` so it is not offered a second time as a `cache:` item.
List only the subfolders that hold downloads; never a folder that also holds logins or databases.

### Anything else

Rules that need a command, an age filter or a tool check go in two places:

1. `scan()`: compute the size and call `item id tier bytes title why paths`. Paths are newline separated.
2. `clean()`: add `want id && { log "== Title"; ...; }`. Use `run CMD` for commands and `remove_selected id "$paths"` for deletion,
   so preview mode, the selection check and the delete guard apply. Never call `rm` directly.

Then add the rule to the tables above, and check:

```
sh -n src/agent.sh
shellcheck -s sh -S warning src/agent.sh
```
