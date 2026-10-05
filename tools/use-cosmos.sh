#!/usr/bin/env bash
# Switches which Cosmos kernel packages Zenith builds against.
#
#   tools/use-cosmos.sh release   the published release from nuget.org (default; what CI uses)
#   tools/use-cosmos.sh local     packages built from a local Cosmos checkout
#   tools/use-cosmos.sh status    show the current choice
#
# The local checkout defaults to ../Cosmos (override with COSMOS_DIR). Build its packages first:
#   cd ../Cosmos && DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH ./.devcontainer/postCreateCommand.sh
# (Cosmos needs a newer .NET SDK than Zenith; see docs/DEVELOPMENT.md.)
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cosmos_dir=${COSMOS_DIR:-$root/../Cosmos}
feed="$cosmos_dir/artifacts/package/release"
local_props="$root/cosmos.local.props"
global_json="$root/global.json"
release_version=3.0.89   # keep in sync with Directory.Build.props

# The Cosmos SDK pulls in some Cosmos packages at its own version, so the SDK (pinned in
# global.json) has to switch along with the kernel packages. global.json is tracked; while it
# points at a local build it is marked skip-worktree so the change can't be committed by accident.
set_sdk_version() {
    sed -i -E "s|(\"Cosmos\.Sdk\"[[:space:]]*:[[:space:]]*\")[^\"]+(\")|\1$1\2|" "$global_json"
}

case "${1:-status}" in
    local)
        package=$(ls -t "$feed"/Cosmos.Kernel.System.[0-9]*.nupkg 2>/dev/null | head -n1 || true)
        [[ -n "$package" ]] || { echo "no Cosmos.Kernel.System package in $feed; build Cosmos first" >&2; exit 1; }
        version=$(basename "$package" .nupkg)
        version=${version#Cosmos.Kernel.System.}
        cat > "$local_props" <<EOF
<Project>
  <!-- Written by tools/use-cosmos.sh local; delete it (or run 'use-cosmos.sh release') to go back. -->
  <PropertyGroup>
    <CosmosVersion>$version</CosmosVersion>
    <RestoreAdditionalProjectSources>$feed</RestoreAdditionalProjectSources>
  </PropertyGroup>
</Project>
EOF
        set_sdk_version "$version"
        git -C "$root" update-index --skip-worktree global.json
        echo "Zenith now builds against local Cosmos $version ($feed)"
        ;;
    release)
        rm -f "$local_props"
        git -C "$root" update-index --no-skip-worktree global.json
        set_sdk_version "$release_version"
        echo "Zenith now builds against the Cosmos release from nuget.org"
        ;;
    status)
        if [[ -f "$local_props" ]]; then
            grep -o '<CosmosVersion>[^<]*' "$local_props" | sed 's/<CosmosVersion>/local Cosmos /'
        else
            echo "Cosmos release (nuget.org)"
        fi
        ;;
    *)
        echo "usage: tools/use-cosmos.sh [release|local|status]" >&2
        exit 2
        ;;
esac
