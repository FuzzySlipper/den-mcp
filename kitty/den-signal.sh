#!/usr/bin/env bash

# Helper functions for den-managed agent windows running inside Kitty.
# Source this file from bash or zsh wrappers, then call den_signal KEY VALUE
# to publish window-scoped user variables over OSC 1337.

den_signal__base64() {
    printf '%s' "${1-}" | base64 | tr -d '\r\n'
}

den_signal__emit() {
    local key="${1-}"
    local encoded="${2-}"

    if [ -t 1 ]; then
        printf '\033]1337;SetUserVar=%s=%s\007' "$key" "$encoded"
        return 0
    fi

    if [ -e /dev/tty ] && [ -w /dev/tty ]; then
        printf '\033]1337;SetUserVar=%s=%s\007' "$key" "$encoded" > /dev/tty
        return 0
    fi

    if [ -t 2 ]; then
        printf '\033]1337;SetUserVar=%s=%s\007' "$key" "$encoded" >&2
        return 0
    fi

    return 0
}

den_signal() {
    local key="${1-}"
    local value="${2-}"
    local encoded

    if [ -z "$key" ]; then
        printf 'den_signal: missing key\n' >&2
        return 1
    fi

    # Outside Kitty this is a deliberate no-op so wrappers can source and call
    # the helper unconditionally.
    if [ -z "${KITTY_WINDOW_ID-}" ]; then
        return 0
    fi

    encoded="$(den_signal__base64 "$value")"
    den_signal__emit "$key" "$encoded"
}

den_signal_clear() {
    den_signal "${1-}" ""
}
