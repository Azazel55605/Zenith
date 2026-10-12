#!/usr/bin/env bash
# Run manually as the release user in a trusted, clean Zenith checkout.
# Build/test/sign and publish. No automatic pull, key generation or guest activation.
set -euo pipefail
if [[ $# != 3 ]]; then
    echo 'usage: build-kernel.sh VERSION EXISTING_KEY_DIRECTORY PUBLICATION_ROOT' >&2
    exit 2
fi
version=$1
keys=$(realpath -e -- "$2")
published=$(realpath -e -- "$3")
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd -- "$repo"
export PATH="$HOME/.dotnet/tools:$PATH"
if [[ -f cosmos.local.props ]]; then
    echo 'Local Cosmos override refused; distributor uses released dependencies.' >&2
    exit 1
fi
if [[ "$keys" == "$published" || "$keys" == "$published/"* ]]; then
    echo 'Signing keys must be outside the web root.' >&2
    exit 1
fi
if [[ ! -f "$keys/private.pem" || ! -f "$keys/public.pem" ]]; then
    echo 'Import the existing signing key; this workflow never generates or rotates it.' >&2
    exit 1
fi
python3 - "$keys/private.pem" <<'PY'
import os, stat, sys
mode = os.stat(sys.argv[1]).st_mode
if not stat.S_ISREG(mode) or stat.S_IMODE(mode) & 0o077:
    raise SystemExit('Private signing key must be a regular file readable only by its owner.')
PY
clean_checkout() {
    git diff --quiet && git diff --cached --quiet &&
        [[ -z $(git ls-files --others --exclude-standard) ]]
}
clean_checkout || { echo 'Commit or remove local checkout changes before a release build.' >&2; exit 1; }
commit=$(git rev-parse HEAD)
mkdir -p obj/distributor
exec 9>obj/distributor/build.lock
flock -n 9 || { echo 'Another distributor build is running.' >&2; exit 1; }
work=$(mktemp -d "$repo/obj/distributor/release-XXXXXXXX")
echo "Release workspace: $work; source commit: $commit"
dotnet test tests/Zenith.Tests --logger 'console;verbosity=minimal' >"$work/tests.log" 2>&1 || {
    echo "Host tests failed; see $work/tests.log" >&2; exit 1;
}
dotnet publish -c Release -r linux-x64 -o "$work/build" >"$work/build.log" 2>&1 || {
    echo "Kernel build failed; see $work/build.log" >&2; exit 1;
}
clean_checkout && [[ $(git rev-parse HEAD) == "$commit" ]] || {
    echo 'Source checkout changed during build; publication refused.' >&2; exit 1;
}
tool="$repo/tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll"
dotnet build tools/Zenith.PackageTool -c Release --nologo >"$work/package-tool.log" 2>&1
dotnet "$tool" kernel-seal "$version" "$work/build/Zenith.elf" "$keys/private.pem" "$work/kernel.zpk"
clean_checkout && [[ $(git rev-parse HEAD) == "$commit" ]] || {
    echo 'Source checkout changed before publication; refusing this release.' >&2; exit 1;
}
python3 -B tools/publish-package.py --root "$published" --package "$work/kernel.zpk" \
    --public-key "$keys/public.pem" --tool "$tool" --kind kernel --repository core \
    --name zenith-kernel --version "$version" --source-commit "$commit"
echo "Published after host tests/build/signature checks. Native guest acceptance remains manual. Logs: $work"
