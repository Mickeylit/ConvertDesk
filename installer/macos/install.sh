#!/bin/sh
set -eu

source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
install_dir="$HOME/Applications/ConvertDesk.app"
bin_dir="$HOME/.local/bin"
if [ -e "$install_dir" ] || [ -L "$install_dir" ]; then
    printf '%s\n' "An installation already exists at $install_dir. Run uninstall.sh first."
    exit 1
fi
# A convertdesk link left by an earlier ConvertDesk whose app is gone (moved to the Trash instead of uninstall.sh) is replaced.
# Anything else at that path isn't ours and stays.
stale_link=false
if [ -L "$bin_dir/convertdesk" ] && [ ! -e "$bin_dir/convertdesk" ]; then
    case $(readlink "$bin_dir/convertdesk") in
        */ConvertDesk.app/Contents/MacOS/filee-cli) stale_link=true ;;
    esac
fi
if [ "$stale_link" = false ] && { [ -e "$bin_dir/convertdesk" ] || [ -L "$bin_dir/convertdesk" ]; }; then
    printf '%s\n' "$bin_dir/convertdesk already exists. Remove or rename it before installing ConvertDesk."
    exit 1
fi
test -x "$source_dir/ConvertDesk.app/Contents/MacOS/ConvertDesk"
mkdir -p "$HOME/Applications" "$bin_dir"
/usr/bin/ditto "$source_dir/ConvertDesk.app" "$install_dir"
# ditto keeps the download's quarantine flag: macOS would then start ConvertDesk from a translocated copy and could stop
# the convertdesk command without a word (#316).
/usr/bin/xattr -dr com.apple.quarantine "$install_dir" 2>/dev/null || true
if [ "$stale_link" = true ]; then
    rm -f -- "$bin_dir/convertdesk"
    printf '%s\n' "Replaced the convertdesk link left by an earlier installation."
fi
ln -s "$install_dir/Contents/MacOS/filee-cli" "$bin_dir/convertdesk"
printf '%s\n' "Installed $install_dir" "CLI: $bin_dir/convertdesk (add $bin_dir to PATH if needed)."
printf '%s\n' 'Open ConvertDesk from Applications. Grant Accessibility and Finder Automation access when requested.'
printf '%s\n' 'If ConvertDesk is already listed and switched on under Accessibility, remove it with the - button and add it again with +.'
