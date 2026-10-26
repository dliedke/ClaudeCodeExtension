#!/usr/bin/env bash
# Claude Code launcher for "Run-Claude.ps1 -Wsl" (see docs/SKILLS.md -> Watch PRs and issues).
#   run-claude-wsl.sh <claude> <prompt-file> <claude flags...>
# A corporate proxy may answer Anthropic requests with a "Continue" notice page (urlblock.php)
# instead of the API. The notice is accepted before Claude starts and again every 4 minutes while
# it runs, so a long unattended run isn't cut off when the acceptance expires.

claude=$1
prompt=$2
shift 2

accept_proxy_notice() {
    local url loc
    for url in https://api.anthropic.com/v1/messages https://claude.ai/; do
        loc=$(curl -s -m 15 -o /dev/null -w '%{redirect_url}' "$url")
        case "$loc" in
            *urlblock.php*)
                curl -s -m 15 -o /dev/null -X POST -d 'ok=Continue' "$loc" &&
                    echo "Accepted the proxy notice for ${url%/v1/*}." >&2
                ;;
        esac
    done
}

accept_proxy_notice
# Stops on its own once this script is gone, even if it is killed without running the trap.
( while sleep 240 && kill -0 $$ 2>/dev/null; do accept_proxy_notice; done ) &
keeper=$!
trap 'kill $keeper 2>/dev/null' EXIT

"$claude" "$@" < "$prompt"
