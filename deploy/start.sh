#!/usr/bin/env bash
set -euo pipefail

# Only the unprivileged Playwright UID is restricted. Keep the firewall
# inside this container's network namespace and fail startup if it cannot load.
browser_uid="$(id -u pwuser)"
iptables -w -I OUTPUT -m owner --uid-owner "$browser_uid" -j REJECT
iptables -w -I OUTPUT -m owner --uid-owner "$browser_uid" -p tcp -d 127.0.0.1 --dport 3128 -j ACCEPT
iptables -w -I OUTPUT -m owner --uid-owner "$browser_uid" -p tcp -s 127.0.0.1 --sport 3000 \
    -m conntrack --ctstate ESTABLISHED -j ACCEPT
ip6tables -w -I OUTPUT -m owner --uid-owner "$browser_uid" -j REJECT

if compgen -G "/usr/local/share/ca-certificates/research-mcp/*.crt" > /dev/null; then
    update-ca-certificates
fi

state=/var/lib/research-mcp
searxroot=/opt/searxng-root
install -d -o debian-tor -g debian-tor -m 0700 "$state/tor"
chown -hR debian-tor:debian-tor "$state/tor"
install -d -o yacy -g yacy "$state/yacy"
install -d -m 0700 "$state/secrets"
install -d -o 977 -g 977 "$searxroot/var/cache/searxng"
cp -L /etc/resolv.conf /etc/hosts "$searxroot/etc/"
if [[ -z "${SEARXNG_SECRET:-}" ]]; then
    if [[ ! -s "$state/secrets/searxng" ]]; then
        (umask 077; head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$state/secrets/searxng")
    fi
    export SEARXNG_SECRET="$(cat "$state/secrets/searxng")"
fi

as_user() {
    local user="$1"
    shift
    local home
    home="$(getent passwd "$user" | cut -d: -f6)"
    if [[ "$user" != app ]]; then
        set -- env -i "PATH=$PATH" "HOME=$home" "LANG=${LANG:-C.UTF-8}" "JAVA_HOME=$JAVA_HOME" \
            "PLAYWRIGHT_BROWSERS_PATH=$PLAYWRIGHT_BROWSERS_PATH" "$@"
    fi
    exec setpriv --reuid "$user" --regid "$(id -g "$user")" --init-groups --no-new-privs \
        env -u SEARXNG_SECRET "HOME=$home" "$@"
}

children=()
cleanup() {
    trap - EXIT TERM INT
    if (( ${#children[@]} )); then
        kill "${children[@]}" 2>/dev/null || true
        wait "${children[@]}" 2>/dev/null || true
    fi
}
trap cleanup EXIT
trap 'exit 0' TERM INT

tor_log=/run/research-mcp-tor.log
install -o debian-tor -g debian-tor -m 0600 /dev/null "$tor_log"
(as_user debian-tor tor -f /etc/tor/torrc --DataDirectory "$state/tor" --SocksPort 127.0.0.1:9050 \
    --Log 'notice stdout' --Log "notice file $tor_log") &
children+=("$!")
(cd /opt/yacy_search_server; as_user yacy java -server -Xmx600m -Djava.awt.headless=true -Dfile.encoding=UTF-8 -classpath ".:lib/*" net.yacy.yacy -startup "$state/yacy") &
children+=("$!")
(as_user pwuser node /usr/lib/node_modules/playwright-core/cli.js run-server --host 127.0.0.1 --port 3000) &
children+=("$!")
# SearXNG verifies the Tor proxy when it loads Ahmia.
deadline=$((SECONDS + 120))
until grep -q 'Bootstrapped 100%' "$tor_log"; do
    if (( SECONDS >= deadline )) || ! kill -0 "${children[@]}" 2>/dev/null; then
        echo "Tor did not establish its first circuit." >&2
        exit 1
    fi
    sleep 1
done
(
    exec env -i "PATH=/usr/bin:/bin" LANG=C.UTF-8 "SEARXNG_SECRET=$SEARXNG_SECRET" SEARXNG_SETTINGS_PATH=/etc/searxng/settings.yml \
        /usr/sbin/chroot --userspec=977:977 "$searxroot" /bin/sh -c \
        'cd /usr/local/searxng && exec .venv/bin/granian --interface wsgi --host 127.0.0.1 --port 8080 searx.webapp:app'
) &
children+=("$!")
deadline=$((SECONDS + 60))
while ! curl -fsS --max-time 2 http://127.0.0.1:8080/config -o /dev/null 2>/dev/null ||
      ! curl -fsS --max-time 2 http://127.0.0.1:8090/yacysearch.json -o /dev/null 2>/dev/null; do
    if (( SECONDS >= deadline )) || ! kill -0 "${children[@]}" 2>/dev/null; then
        echo "Internal search services did not start." >&2
        exit 1
    fi
    sleep 1
done
(as_user app dotnet /app/SearxngResearchMcp.dll "$@") &
children+=("$!")

set +e
wait -n -p failed "${children[@]}"
status=$?
echo "Process ${failed:-unknown} stopped (exit $status); stopping the container." >&2
exit "$(( status == 0 ? 1 : status ))"
