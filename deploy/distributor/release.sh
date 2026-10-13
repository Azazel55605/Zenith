#!/usr/bin/env bash
# Manual trusted releases; configuration is data, never sourced shell code.
set -euo pipefail
export PATH="$HOME/.dotnet/tools:$PATH:/usr/local/sbin:/usr/sbin:/sbin"
exec python3 "$(dirname -- "${BASH_SOURCE[0]}")/release.py" "$@"
