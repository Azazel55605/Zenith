#!/usr/bin/env bash
# Install the optional manager on the existing dedicated Debian distributor.
set -euo pipefail
if [[ "$EUID" -ne 0 ]]; then echo 'Run with sudo on the distributor VM.' >&2; exit 1; fi
dashboard_source="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
for dashboard_tool in htpasswd visudo nginx systemctl; do
    command -v "$dashboard_tool" >/dev/null || { echo "Missing $dashboard_tool (install sudo nginx apache2-utils)." >&2; exit 1; }
done
id zenith-release >/dev/null
getent group zenith-release >/dev/null
if ! id zenith-dashboard >/dev/null 2>&1; then
    adduser --system --group --no-create-home zenith-dashboard
fi
usermod -aG zenith-release zenith-dashboard
install -d -o root -g root -m 0755 /opt/zenith-dashboard /opt/zenith-dashboard/web
install -o root -g root -m 0644 "$dashboard_source/dashboard.py" "$dashboard_source/retry.py" /opt/zenith-dashboard/
install -o root -g root -m 0644 "$dashboard_source"/web/* /opt/zenith-dashboard/web/
for dashboard_parent in /home/zenith-release/.local /home/zenith-release/.local/state /home/zenith-release/.local/state/zenith; do
    if [[ ! -d "$dashboard_parent" ]]; then install -d -o zenith-release -g zenith-release -m 0750 "$dashboard_parent"; fi
done
install -d -o zenith-release -g zenith-release -m 0750 /home/zenith-release/.local/state/zenith/distributor
for dashboard_parent in /home/zenith-release /home/zenith-release/.local /home/zenith-release/.local/state /home/zenith-release/.local/state/zenith; do
    chgrp zenith-release "$dashboard_parent"
    chmod g+x "$dashboard_parent"
done
find /home/zenith-release/.local/state/zenith/distributor -maxdepth 1 -type f \
    \( -name '*.json' -o -name '*.log' -o -name 'poll.lock' \) -exec chgrp zenith-release {} + -exec chmod 0640 {} +
visudo -cf "$dashboard_source/dashboard.sudoers"
install -o root -g root -m 0440 "$dashboard_source/dashboard.sudoers" /etc/sudoers.d/zenith-dashboard
if [[ -f /etc/nginx/zenith-dashboard.htpasswd ]]; then
    echo 'Existing dashboard password file retained.'
else
    htpasswd -c /etc/nginx/zenith-dashboard.htpasswd admin
fi
chown root:www-data /etc/nginx/zenith-dashboard.htpasswd
chmod 0640 /etc/nginx/zenith-dashboard.htpasswd
install -d -m 0755 /etc/nginx/snippets
install -m 0644 "$dashboard_source/dashboard-nginx.conf" /etc/nginx/snippets/zenith-dashboard.conf
install -m 0644 "$dashboard_source/zenith-dashboard.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now zenith-dashboard.service
systemctl restart zenith-dashboard.service
python3 "$dashboard_source/dashboard-setup.py"
echo 'Open https://zenith-stable.net.local/manage/ and log in as admin.'
