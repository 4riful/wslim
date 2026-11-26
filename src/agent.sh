#!/bin/sh
# wslim agent: runs inside a WSL distro as root (piped in by wslim.exe).
#   sh -s scan              list what can be cleaned, with sizes and exact paths
#   sh -s clean <ids...>    clean those items, then release free space to Windows (fstrim)
#   sh -s preview <ids...>  print what clean would do, change nothing
#
# Selection: wslim.exe puts a line  WSLIM_SEL='<base64>'  in front of this script. Decoded, it holds
# "id<TAB>path" lines: the exact paths (or volume names) the user saw in the scan and ticked.
# Clean deletes a path only if it is BOTH in that selection AND still found by the rule now.
# Anything new since the scan, or anything the rule no longer matches, is left alone.
#
# Tiers:
#   safe    rebuilds itself on demand (download caches, build caches, old tool versions)
#   review  can be rebuilt, but costs time or bandwidth, or may be wanted; off by default
# Never touched: Docker named volumes, databases, ~/.config, keys, project source, git data,
#                logins and tokens.
#
# Output, tab separated:  ITEM id tier bytes title why paths(;)  |  INFO key value  |  NOTE text  |  LOG text
# POSIX sh with coreutils/findutils or busybox; each tool's own CLI only when it is installed.

MODE=${1:-scan}; [ $# -gt 0 ] && shift
IDS=" $* "
DRY=0; [ "$MODE" = preview ] && DRY=1
NL='
'
TABC=$(printf '\t')
SEL=""
[ -n "${WSLIM_SEL:-}" ] && SEL=$(printf '%s' "$WSLIM_SEL" | base64 -d 2>/dev/null)

HOMES=""
for d in /root /home/*; do [ -d "$d" ] && [ ! -L "$d" ] && HOMES="$HOMES$d$NL"; done

log()  { printf 'LOG\t%s\n' "$*"; }
note() { printf 'NOTE\t%s\n' "$*"; }
info() { printf 'INFO\t%s\t%s\n' "$1" "$2"; }
has()  { command -v "$1" >/dev/null 2>&1; }
want() { case "$IDS" in *" $1 "*) return 0;; esac; return 1; }
selected() { printf '%s\n' "$SEL" | grep -qxF -- "$1$TABC$2"; }   # id path

# total bytes of the paths on stdin (one per line); hard links counted once per du call
size_of() {
  t=0
  while IFS= read -r p; do
    [ -n "$p" ] && [ -e "$p" ] || continue
    k=$(du -sk "$p" 2>/dev/null | cut -f1)
    t=$((t + ${k:-0}))
  done
  echo $((t * 1024))
}
# like size_of, but skips files that also live elsewhere through a hard link (uv links into venvs)
size_unshared() {
  t=0
  while IFS= read -r p; do
    [ -n "$p" ] && [ -e "$p" ] || continue
    k=$(find "$p" -xdev -type f -links 1 -exec du -k {} + 2>/dev/null | awk '{s+=$1}END{print s+0}')
    t=$((t + ${k:-0}))
  done
  echo $((t * 1024))
}
# existing directories <home>/<rel> for every home (never symlinks, never names with newlines)
home_paths() {
  printf '%s' "$HOMES" | while IFS= read -r h; do
    [ -n "$h" ] || continue
    for rel in "$@"; do [ -d "$h/$rel" ] && [ ! -L "$h/$rel" ] && printf '%s\n' "$h/$rel"; done
  done
}
item() {  # id tier bytes title why paths(newline separated)
  [ "${3:-0}" -gt 0 ] 2>/dev/null || return 0
  ps=$(printf '%s' "$6" | tr '\n' ';' | sed 's/;$//')
  printf 'ITEM\t%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$5" "$ps"
}
run() {  # log and run a command (skipped in preview)
  log "+ $*"
  [ $DRY -eq 1 ] && return 0
  "$@" 2>&1 | while IFS= read -r l; do printf 'LOG\t    %s\n' "$l"; done
  return 0
}
# delete the paths the rule finds NOW (arg 2, newline separated) that the user also selected (id = arg 1)
remove_selected() {
  id=$1
  printf '%s\n' "$2" | while IFS= read -r p; do
    [ -n "$p" ] && [ -e "$p" ] || continue
    case "$p" in *"$TABC"*) continue;; esac
    case "$p" in /root/?*|/home/?*/?*|/var/cache/?*|/var/lib/snapd/snaps/?*|/var/crash/?*|/var/lib/systemd/coredump/?*) ;;
      *) log "refused (outside allowed folders): $p"; continue;; esac
    if [ -n "$SEL" ] && ! selected "$id" "$p"; then log "kept (not in your selection): $p"; continue; fi
    if [ -L "$p" ]; then log "kept (symbolic link): $p"; continue; fi
    run rm -rf -- "$p"
  done
}
human_to_bytes() { awk '{ n=$1+0; u=$1; gsub(/[0-9.]/,"",u); m=1;
  if(u~/^[kK]/)m=1e3; else if(u~/^M/)m=1e6; else if(u~/^G/)m=1e9; else if(u~/^T/)m=1e12; s+=n*m } END{ printf "%.0f\n", s+0 }'; }

# ------------------------------------------------------------------ catalog
# Cache folders inside each home:  id|tier|title|why|relative folders (space separated)
# Only folders that hold downloaded or generated data. Never folders that hold logins or databases.
DIRS='pip|safe|pip cache|Downloaded Python packages. pip fetches them again when needed.|.cache/pip
uv|safe|uv cache|Downloaded and built Python packages. uv fetches them again; existing environments keep working. Size counts only files not shared with environments.|.cache/uv
npm|safe|npm cache|Downloaded npm packages, npx installs and npm logs.|.npm/_cacache .npm/_npx .npm/_logs
yarn|safe|Yarn cache|Downloaded Yarn packages.|.cache/yarn .yarn/berry/cache
pnpm|safe|pnpm cache|pnpm metadata cache (the package store itself is not touched).|.cache/pnpm
bun|safe|Bun cache|Downloaded Bun packages.|.bun/install/cache
deno|safe|Deno download cache|Downloaded and compiled Deno modules. Deno KV data and localStorage are kept.|.cache/deno/deps .cache/deno/gen .cache/deno/npm .cache/deno/remote
gobuild|safe|Go build cache|Compiled Go objects; rebuilt on the next build.|.cache/go-build
cargo|safe|Cargo download cache|Downloaded crate archives and their unpacked sources.|.cargo/registry/cache .cargo/registry/src
composer|safe|Composer cache|Downloaded PHP packages.|.cache/composer
nugethttp|safe|NuGet HTTP cache|Downloaded NuGet package metadata.|.local/share/NuGet/http-cache .local/share/NuGet/v3-cache
nodegyp|safe|node-gyp headers|Node headers for native builds; downloaded again when needed.|.cache/node-gyp
precommit|safe|pre-commit environments|Hook environments; pre-commit recreates them.|.cache/pre-commit
selenium|safe|Selenium drivers|Browser drivers; Selenium Manager downloads them again.|.cache/selenium
tscache|safe|TypeScript and Prisma caches|Type definitions and engine downloads.|.cache/typescript .cache/prisma
thumbs|safe|Thumbnails|Image previews.|.cache/thumbnails
vscodelogs|safe|VS Code server logs|Log files of the VS Code / Cursor server.|.vscode-server/data/logs .cursor-server/data/logs
gomod|review|Go module cache|Downloaded Go modules. Large re-download on the next build.|go/pkg/mod
gradle|review|Gradle caches|Downloaded dependencies and build caches. The next build is slow.|.gradle/caches
maven|review|Maven repository|Downloaded Java dependencies. The next build downloads them again.|.m2/repository
nuget|review|NuGet packages|Downloaded .NET packages. The next restore downloads them again.|.nuget/packages
playwright|review|Playwright browsers|Test browsers. Run "npx playwright install" to get them back.|.cache/ms-playwright
puppeteer|review|Puppeteer browsers|Chrome builds for Puppeteer; reinstalled on the next npm install.|.cache/puppeteer
huggingface|review|Hugging Face models|Downloaded AI models and datasets; can be many GB to download again. Your Hugging Face login is kept.|.cache/huggingface/hub .cache/huggingface/datasets .cache/huggingface/xet .cache/huggingface/assets
torch|review|PyTorch hub cache|Downloaded model weights.|.cache/torch'

# ~/.cache entries handled above, or that must never be offered (logins, history, envs, keys)
CACHE_SKIP=" pip uv yarn pnpm deno go-build composer node-gyp pre-commit selenium typescript prisma thumbnails ms-playwright puppeteer huggingface torch gh claude claude-cli-nodejs pypoetry zsh bash fish lm-studio JetBrains keyring gnupg ssh p10k-dump gitstatus mesa_shader_cache "

dirs_scan() {
  printf '%s\n' "$DIRS" | while IFS='|' read -r id tier title why rels; do
    [ -n "$id" ] || continue
    # shellcheck disable=SC2086
    ps=$(home_paths $rels)
    [ -n "$ps" ] || continue
    if [ "$id" = uv ]; then b=$(printf '%s\n' "$ps" | size_unshared); else b=$(printf '%s\n' "$ps" | size_of); fi
    item "$id" "$tier" "$b" "$title" "$why" "$ps"
  done
}
dirs_clean() {
  printf '%s\n' "$DIRS" | while IFS='|' read -r id tier title why rels; do
    [ -n "$id" ] && want "$id" || continue
    log "== $title"
    # shellcheck disable=SC2086
    remove_selected "$id" "$(home_paths $rels)"
  done
}

# Each larger folder in ~/.cache that no rule above knows becomes its own review item.
other_cache_dirs() {
  printf '%s' "$HOMES" | while IFS= read -r h; do
    [ -d "$h/.cache" ] || continue
    for p in "$h"/.cache/*/ "$h"/.cache/.[!.]*/; do
      p=${p%/}
      [ -d "$p" ] && [ ! -L "$p" ] || continue
      n=${p##*/}
      case "$CACHE_SKIP" in *" $n "*) continue;; esac
      case "$n" in *" "*|*history*|*token*|*secret*|*cred*|*keyring*|*ssh*|*gnupg*|*.env|*session*) continue;; esac
      printf '%s\n' "$p"
    done
  done
}
other_cache_scan() {
  other_cache_dirs | while IFS= read -r p; do
    b=$(printf '%s\n' "$p" | size_of)
    [ "$b" -ge 52428800 ] || continue          # 50 MB and up, so the list stays short
    n=${p##*/}; h=${p%/.cache/*}
    # shellcheck disable=SC2088  # "~" is display text here
    item "cache:$p" review "$b" "~/.cache/$n$( [ "$h" != /root ] && printf ' (%s)' "${h##*/}")" \
      "Cache folder of another program. Usually re-creatable; close that program first." "$p"
  done
}
other_cache_clean() {
  for id in $IDS; do
    case $id in cache:*) ;; *) continue;; esac
    p=${id#cache:}
    log "== ~/.cache/${p##*/}"
    remove_selected "$id" "$(other_cache_dirs | grep -xF -- "$p")"
  done
}

# Old builds of tools that update themselves. Keeps the newest N folders and any build in use.
old_versions() {
  [ -d "$1" ] || return 0
  ls -1t "$1" 2>/dev/null | while IFS= read -r v; do [ -d "$1/$v" ] && [ ! -L "$1/$v" ] && printf '%s\n' "$v"; done \
    | tail -n +$(( $2 + 1 )) | while IFS= read -r v; do
      in_use "$1/$v" && continue
      printf '%s\n' "$1/$v"
    done
}
in_use() {  # true if a running process runs from, or has open, something under the folder
  for p in /proc/[0-9]*; do
    e=$(readlink "$p/exe" 2>/dev/null); case "$e" in "$1"/*) return 0;; esac
    tr '\0' ' ' < "$p/cmdline" 2>/dev/null | grep -qF -- "$1/" && return 0
  done
  return 1
}
tool_versions_paths() {
  printf '%s' "$HOMES" | while IFS= read -r h; do
    [ -n "$h" ] || continue
    old_versions "$h/.local/share/claude/versions" 2
    old_versions "$h/.vscode-server/bin" 1
    old_versions "$h/.vscode-server/cli/servers" 1
    old_versions "$h/.cursor-server/bin" 1
    old_versions "$h/.cursor-server/cli/servers" 1
  done
}

# Dependency folders of projects nobody worked on for 60+ days, that can be rebuilt from a manifest.
stale_paths() {
  printf '%s' "$HOMES" | while IFS= read -r h; do
    [ -n "$h" ] || continue
    find "$h" -xdev -maxdepth 6 \( -path "*$NL*" -o -path "$h/.*" \) -prune -o \
      -type d \( -name node_modules -o -name .venv -o -name venv \) -prune -print 2>/dev/null
  done | while IFS= read -r v; do
    proj=${v%/*}
    case "${v##*/}" in
      node_modules) [ -f "$proj/package.json" ] || continue ;;
      *) [ -f "$v/pyvenv.cfg" ] || continue
         [ -f "$proj/requirements.txt" ] || [ -f "$proj/pyproject.toml" ] || [ -f "$proj/setup.py" ] || [ -f "$proj/Pipfile" ] || continue ;;
    esac
    # skip when anything in the project or the folder itself changed in the last 60 days
    [ -n "$(find "$proj" -maxdepth 2 -newer "$STAMP" ! -path "$v" ! -path "$v/*" 2>/dev/null | head -n 1)" ] && continue
    [ -n "$(find "$v" -maxdepth 3 -newer "$STAMP" 2>/dev/null | head -n 1)" ] && continue
    printf '%s\n' "$v"
  done
}
STAMP=$(mktemp 2>/dev/null || echo /tmp/wslim-stamp.$$)
touch -d '60 days ago' "$STAMP" 2>/dev/null || touch -t "$(date -d '60 days ago' +%Y%m%d%H%M 2>/dev/null || echo 197001010000)" "$STAMP"
trap 'rm -f "$STAMP"' EXIT

# Temporary files untouched (modified, changed and read) for 10+ days. Names with newlines are skipped.
tmp_find() {  # extra find args...
  # shellcheck disable=SC2046
  find /tmp /var/tmp -xdev \( -path "*$NL*" -o -path '/var/tmp/systemd-private-*' -o -path '/tmp/systemd-private-*' \
    -o -path /tmp/.X11-unix -o -path '/tmp/tmux-*' \) -prune -o -type f -mtime +10 -ctime +10 -atime +10 "$@" 2>/dev/null
}

journal_excess() {
  has journalctl || { echo 0; return; }
  u=$(journalctl --disk-usage 2>/dev/null | grep -oE '[0-9.]+[KMGT]' | head -n 1)
  [ -n "$u" ] || { echo 0; return; }
  b=$(printf '%sB\n' "$u" | human_to_bytes)
  [ "$b" -gt 104857600 ] && echo $((b - 104857600)) || echo 0
}

pkg_cache() {  # id bytes folder command...
  if has apt-get; then echo "apt $(printf '/var/cache/apt/archives\n' | size_of) /var/cache/apt/archives apt-get clean"; fi
  if has dnf; then echo "dnf $(printf '/var/cache/dnf\n/var/cache/libdnf5\n' | size_of) /var/cache/dnf dnf clean packages"
  elif has yum; then echo "yum $(printf '/var/cache/yum\n' | size_of) /var/cache/yum yum clean packages"; fi
  if has pacman; then echo "pacman $(printf '/var/cache/pacman/pkg\n' | size_of) /var/cache/pacman/pkg pacman -Scc --noconfirm"; fi
  if has zypper; then echo "zypper $(printf '/var/cache/zypp/packages\n' | size_of) /var/cache/zypp/packages zypper clean --all"; fi
  if has apk && [ -d /etc/apk/cache ]; then echo "apk $(printf '/etc/apk/cache\n' | size_of) /etc/apk/cache apk cache clean"; fi
}

coredump_paths() { for p in /var/lib/systemd/coredump/* /var/crash/*; do [ -f "$p" ] && printf '%s\n' "$p"; done; }

# Docker: only for an engine that runs inside this distro. Docker Desktop's shared engine is left
# to Docker Desktop (its data lives in its own disk, which wslim only shrinks).
DOCKER=0
docker_check() {
  has docker || return 0
  if ! timeout 8 docker info >/dev/null 2>&1; then
    note "Docker is installed but not running, so Docker leftovers were not measured. Start Docker and scan again to include them."
    return 0
  fi
  os=$(docker info --format '{{.OperatingSystem}}' 2>/dev/null)
  case "$os" in *"Docker Desktop"*)
    note "Docker here is Docker Desktop. Clean its images and build cache in Docker Desktop (Troubleshoot > Clean / Purge data); wslim only shrinks its disk."
    return 0;; esac
  DOCKER=1
}
anon_volumes() { docker volume ls -qf dangling=true 2>/dev/null | grep -E '^[0-9a-f]{64}$'; }
unused_image_ids() {
  used=$(docker ps -aq 2>/dev/null | while IFS= read -r c; do docker inspect --format '{{.Image}}' "$c" 2>/dev/null; done | sort -u)
  docker images -q --no-trunc 2>/dev/null | sort -u | while IFS= read -r i; do
    printf '%s\n' "$used" | grep -qxF -- "$i" || printf '%s\n' "$i"
  done
}
sum_image_sizes() { while IFS= read -r i; do docker image inspect --format '{{.Size}}' "$i" 2>/dev/null; done | awk '{s+=$1}END{print s+0}'; }

# ------------------------------------------------------------------ scan
scan() {
  info used "$(df -Pk / | awk 'NR==2{print $3*1024}')"
  info os "$( (. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME") || uname -sr)"
  pkg_cache | while read -r id b dir cmd; do
    item "pkg-$id" safe "$b" "System package cache ($id)" "Downloaded system packages. Cleared with: $cmd" "$dir"
  done
  dirs_scan
  tv=$(tool_versions_paths)
  [ -n "$tv" ] && item toolversions safe "$(printf '%s\n' "$tv" | size_of)" "Old tool versions" \
    "Older Claude Code and VS Code / Cursor server builds. The newest ones and any build in use are kept." "$tv"
  item journal safe "$(journal_excess)" "System journal" "System logs above 100 MB. The newest 100 MB are kept." "/var/log/journal"
  cd_=$(coredump_paths)
  [ -n "$cd_" ] && item coredumps safe "$(printf '%s\n' "$cd_" | size_of)" "Crash dumps" "Memory dumps from programs that crashed." "$cd_"
  tb=$(tmp_find -exec du -k {} + | awk '{s+=$1}END{print s*1024}')
  item tmp safe "${tb:-0}" "Old temporary files" "Files in /tmp and /var/tmp not modified or read for 10+ days." "/tmp;/var/tmp"
  if has snap; then
    sn=$(snap list --all 2>/dev/null | awk '/disabled/{print "/var/lib/snapd/snaps/"$1"_"$3".snap"}')
    [ -n "$sn" ] && item snaps safe "$(printf '%s\n' "$sn" | size_of)" "Old snap revisions" "Disabled snap versions kept after updates." "$sn"
  fi
  docker_check
  if [ $DOCKER -eq 1 ]; then
    bc=$(docker system df --format '{{.Type}}|{{.Reclaimable}}' 2>/dev/null | awk -F'|' '$1=="Build Cache"{split($2,a," ");print a[1]}' | human_to_bytes)
    item docker-buildcache safe "$bc" "Docker build cache" "Layers left over from image builds. The next build takes longer once." "docker builder"
    dg=$(docker images -qf dangling=true --no-trunc 2>/dev/null | sum_image_sizes)
    item docker-dangling safe "$dg" "Untagged Docker images" "Old image versions replaced by newer builds (shown as <none>)." "docker images"
    ui=$(unused_image_ids | sum_image_sizes)
    item docker-unused review "$ui" "Unused Docker images" "Images no container uses, running or stopped. Pulled or built again when needed. Shared layers make this an upper bound." "docker images"
    sc=$(docker ps -aqf status=exited -f status=created 2>/dev/null | wc -l)
    [ "$sc" -gt 0 ] && item docker-stopped review 1 "Stopped Docker containers ($sc)" \
      "Containers that are not running. Their volumes are kept, named or not." "docker ps -a"
    av=$(anon_volumes)
    if [ -n "$av" ]; then
      vb=$(printf '%s\n' "$av" | while IFS= read -r v; do printf '/var/lib/docker/volumes/%s/_data\n' "$v"; done | size_of)
      item docker-anonvolumes review "$([ "$vb" -gt 0 ] && echo "$vb" || echo 1)" "Orphaned unnamed Docker volumes ($(printf '%s\n' "$av" | wc -l))" \
        "Unnamed volumes that no container, running or stopped, uses. They can hold database data from containers you already deleted. Named volumes are never touched." "$av"
    fi
  fi
  st=$(stale_paths)
  sn=$(printf '%s\n' "$st" | grep '/node_modules$')
  [ -n "$sn" ] && item stale-node review "$(printf '%s\n' "$sn" | size_of)" "node_modules of inactive projects" \
    "Projects with a package.json and no changes for 60+ days. Run npm install in the project to restore." "$sn"
  sv=$(printf '%s\n' "$st" | grep -v '/node_modules$' | grep .)
  [ -n "$sv" ] && item stale-venv review "$(printf '%s\n' "$sv" | size_of)" "Python environments of inactive projects" \
    "Projects with requirements or pyproject and no changes for 60+ days. Recreate the environment to restore." "$sv"
  other_cache_scan
  return 0
}

# ------------------------------------------------------------------ clean
clean() {
  before=$(df -Pk / | awk 'NR==2{print $3*1024}')
  pkg_cache | while read -r id b dir cmd; do
    want "pkg-$id" || continue
    log "== System package cache ($id)"
    # shellcheck disable=SC2086
    run $cmd
  done
  dirs_clean
  want toolversions && { log "== Old tool versions"; remove_selected toolversions "$(tool_versions_paths)"; }
  want journal && { log "== System journal"; run journalctl --vacuum-size=100M; }
  want coredumps && { log "== Crash dumps"; remove_selected coredumps "$(coredump_paths)"; }
  if want tmp; then
    log "== Old temporary files"
    n=$(tmp_find -print | wc -l)
    if [ $DRY -eq 1 ]; then log "+ delete $n files in /tmp and /var/tmp untouched for 10+ days"
    else tmp_find -exec rm -f -- {} +; log "    removed $n files"; fi
  fi
  if want snaps && has snap; then
    log "== Old snap revisions"
    snap list --all 2>/dev/null | awk '/disabled/{print $1, $3}' | while read -r n r; do
      [ -z "$SEL" ] || selected snaps "/var/lib/snapd/snaps/${n}_${r}.snap" || continue
      run snap remove "$n" --revision="$r"
    done
  fi
  docker_check >/dev/null
  if [ $DOCKER -eq 1 ]; then
    if want docker-anonvolumes; then     # before any container is removed, so only volumes seen in the scan go
      log "== Orphaned unnamed Docker volumes"
      now=$(anon_volumes)
      printf '%s\n' "$SEL" | awk -F'\t' '$1=="docker-anonvolumes"{print $2}' | while IFS= read -r v; do
        [ -n "$v" ] || continue
        printf '%s\n' "$now" | grep -qxF -- "$v" || { log "kept (in use again or gone): $v"; continue; }
        run docker volume rm "$v"
      done
    fi
    want docker-buildcache && { log "== Docker build cache"; run docker builder prune -af; }
    want docker-dangling   && { log "== Untagged Docker images"; run docker image prune -f; }
    want docker-stopped    && { log "== Stopped Docker containers"; run docker container prune -f; }
    want docker-unused     && { log "== Unused Docker images"; run docker image prune -af; }
  fi
  if want stale-node || want stale-venv; then
    st=$(stale_paths)
    want stale-node && { log "== node_modules of inactive projects"; remove_selected stale-node "$(printf '%s\n' "$st" | grep '/node_modules$')"; }
    want stale-venv && { log "== Python environments of inactive projects"; remove_selected stale-venv "$(printf '%s\n' "$st" | grep -v '/node_modules$')"; }
  fi
  other_cache_clean

  log "== Release free space to Windows"
  has fstrim && run fstrim -av
  [ $DRY -eq 1 ] || sync
  after=$(df -Pk / | awk 'NR==2{print $3*1024}')
  info used "$after"
  info freed "$((before - after))"
}

case $MODE in
  scan) scan ;;
  clean|preview) clean ;;
  *) echo "usage: scan | clean <ids> | preview <ids>" >&2; exit 2 ;;
esac
