#!/bin/sh
set -eu

install_dir="$HOME/Applications/ConvertDesk.app"
if [ -L "$install_dir" ] || [ ! -d "$install_dir" ]; then
    printf '%s\n' "No ordinary ConvertDesk app bundle was found at $install_dir."
    exit 1
fi
resolved_parent=$(CDPATH= cd -- "$HOME/Applications" && pwd -P)
resolved_app=$(CDPATH= cd -- "$install_dir" && pwd -P)
if [ "$resolved_app" != "$resolved_parent/ConvertDesk.app" ]; then
    printf '%s\n' 'Refusing to remove an app outside the expected Applications directory.'
    exit 1
fi
bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$install_dir/Contents/Info.plist")
if [ "$bundle_id" != 'com.convertdesk.app' ]; then
    printf '%s\n' 'The bundle is not ConvertDesk.'
    exit 1
fi
"$install_dir/Contents/MacOS/ConvertDesk" --uninstall-cleanup
if [ -L "$HOME/.local/bin/convertdesk" ] && [ "$(readlink "$HOME/.local/bin/convertdesk")" = "$install_dir/Contents/MacOS/filee-cli" ]; then
    rm -- "$HOME/.local/bin/convertdesk"
fi
rm -rf -- "$install_dir"
printf '%s\n' 'ConvertDesk was removed. Your settings and conversion results were kept.'
