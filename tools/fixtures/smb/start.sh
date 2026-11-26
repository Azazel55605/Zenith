#!/bin/sh
set -eu
# Read a new throwaway password for each container, without placing it in argv or the environment.
IFS= read -r password
printf '%s\n%s\n' "$password" "$password" | smbpasswd -s -a zenithtest >/dev/null
unset password
exec smbd --foreground --no-process-group --configfile=/etc/samba/smb.conf
