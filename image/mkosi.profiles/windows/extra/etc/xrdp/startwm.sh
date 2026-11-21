#!/bin/sh
# xrdp session start for the Hyper-V enhanced session: Plasma on X11.
# shellcheck source=/dev/null
if [ -r /etc/profile ]; then
    . /etc/profile
fi
unset DBUS_SESSION_BUS_ADDRESS
unset XDG_RUNTIME_DIR
export XDG_SESSION_TYPE=x11
export DESKTOP_SESSION=plasma
export XDG_CURRENT_DESKTOP=KDE
exec dbus-run-session -- startplasma-x11
