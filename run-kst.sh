#!/bin/sh
# Launch ServUO with the server clock pinned to Korea Standard Time.
# Ensures KST on any host (even a UTC Linux VPS).
export TZ=Asia/Seoul
exec mono ServUO.exe "$@"
