#!/bin/bash
# Builds AutoLangSwitcher.app, installs it in ~/Applications and starts it.
set -e
cd "$(dirname "$0")"

if ! command -v swiftc >/dev/null 2>&1; then
    echo "The Swift compiler is missing. Install Apple's Command Line Tools with:"
    echo "    xcode-select --install"
    echo "then run this script again."
    exit 1
fi

APP="AutoLangSwitcher.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"

echo "Compiling..."
swiftc -O -parse-as-library -o "$APP/Contents/MacOS/AutoLangSwitcher" *.swift \
    -framework Cocoa -framework Carbon

cat > "$APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>             <string>AutoLangSwitcher</string>
    <key>CFBundleDisplayName</key>      <string>AutoLangSwitcher</string>
    <key>CFBundleIdentifier</key>       <string>com.autolangswitcher.app</string>
    <key>CFBundleExecutable</key>       <string>AutoLangSwitcher</string>
    <key>CFBundlePackageType</key>      <string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.2.2</string>
    <key>CFBundleVersion</key>          <string>8</string>
    <key>LSMinimumSystemVersion</key>   <string>11.0</string>
    <key>LSUIElement</key>              <true/>
</dict>
</plist>
EOF

# Ad-hoc signature so macOS will run it on Apple Silicon
codesign --force --sign - "$APP"

# Install into ~/Applications (stop a running copy first).
# The app used to be called KeyboardMemory; stop and remove that too so the two don't fight.
pkill -x AutoLangSwitcher 2>/dev/null || true
pkill -x KeyboardMemory 2>/dev/null || true
rm -rf "KeyboardMemory.app" "$HOME/Applications/KeyboardMemory.app"
mkdir -p "$HOME/Applications"
rm -rf "$HOME/Applications/$APP"
cp -R "$APP" "$HOME/Applications/"
open "$HOME/Applications/$APP"

echo
echo "Installed to ~/Applications/$APP and started."
echo "Look for the keyboard icon in the menu bar."
