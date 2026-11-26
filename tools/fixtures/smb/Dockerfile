FROM debian:bookworm-slim
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends samba samba-dsdb-modules && rm -rf /var/lib/apt/lists/*
RUN useradd --no-create-home --shell /usr/sbin/nologin zenithtest && install -d -m 0700 -o zenithtest -g zenithtest /srv/transfer
COPY smb.conf /etc/samba/smb.conf
COPY start.sh /usr/local/bin/start-fixture
ENTRYPOINT ["/bin/sh", "/usr/local/bin/start-fixture"]
