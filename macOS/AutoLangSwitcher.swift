// AutoLangSwitcher for macOS (formerly Keyboard Memory) - remembers the keyboard input source you
// last used in each application and restores it when that application becomes active again.
//
// Build: ./build.sh  (needs Apple's Command Line Tools: xcode-select --install)
// Data:  stored in the app's preferences (defaults domain com.autolangswitcher.app).
//        Until the first change, the old com.keyboardmemory.app preferences are read instead.

import Cocoa
import Carbon

// MARK: - Input sources (Text Input Sources API)

enum InputSources {
    static func currentID() -> String? {
        guard let src = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue() else { return nil }
        return property(src, kTISPropertyInputSourceID)
    }

    static func property(_ src: TISInputSource, _ key: CFString) -> String? {
        guard let p = TISGetInputSourceProperty(src, key) else { return nil }
        return Unmanaged<CFString>.fromOpaque(p).takeUnretainedValue() as String
    }

    static func find(_ id: String) -> TISInputSource? {
        let filter = [kTISPropertyInputSourceID as String: id] as CFDictionary
        guard let list = TISCreateInputSourceList(filter, false)?.takeRetainedValue() as? [TISInputSource] else {
            return nil
        }
        return list.first
    }

    @discardableResult
    static func select(_ id: String) -> Bool {
        guard let src = find(id) else { return false }
        return TISSelectInputSource(src) == noErr
    }

    static func name(_ id: String) -> String {
        if let src = find(id), let n = property(src, kTISPropertyLocalizedName) { return n }
        return id
    }
}

// MARK: - Menu bar icon

// Keyboard with a "restore" arrow badge - the same design as the Windows tray icon, drawn as a
// one-colour template image so macOS tints it for light and dark menu bars.
enum MenuBarIcon {
    static func make() -> NSImage {
        // Flipped (y points down) so the coordinates match the Windows drawing on a 64x64 grid.
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { _ in
            guard let ctx = NSGraphicsContext.current else { return false }
            let scale = NSAffineTransform()
            scale.scale(by: 18.0 / 64.0)
            scale.concat()
            NSColor.black.set()

            // Keyboard body
            NSBezierPath(roundedRect: NSRect(x: 2, y: 12, width: 56, height: 38), xRadius: 9, yRadius: 9).fill()

            // Cut out the keys, the space bar and a ring around the badge
            let c = NSPoint(x: 48, y: 48)
            let r: CGFloat = 14
            ctx.compositingOperation = .destinationOut
            for row in 0..<2 {
                for col in 0..<4 {
                    let key = NSRect(x: 8 + CGFloat(col) * 11, y: 18 + CGFloat(row) * 10, width: 7, height: 6)
                    NSBezierPath(roundedRect: key, xRadius: 1.5, yRadius: 1.5).fill()
                }
            }
            NSBezierPath(roundedRect: NSRect(x: 14, y: 39, width: 24, height: 6), xRadius: 1.5, yRadius: 1.5).fill()
            NSBezierPath(ovalIn: NSRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r)).fill()

            // Badge disc
            ctx.compositingOperation = .sourceOver
            NSBezierPath(ovalIn: NSRect(x: c.x - r + 2.5, y: c.y - r + 2.5, width: 2 * r - 5, height: 2 * r - 5)).fill()

            // Cut a clockwise arrow into the disc: arc from 300 to 180 degrees, head at the end.
            // With y pointing down, increasing angles (clockwise: false) run clockwise on screen.
            ctx.compositingOperation = .destinationOut
            let ar: CGFloat = 6.5
            let arc = NSBezierPath()
            arc.appendArc(withCenter: c, radius: ar, startAngle: 300, endAngle: 180, clockwise: false)
            arc.lineWidth = 3.2
            arc.lineCapStyle = .round
            arc.stroke()
            let a = CGFloat.pi   // the arc's end angle, 180 degrees
            let (cs, sn) = (cos(a), sin(a))
            let end = NSPoint(x: c.x + ar * cs, y: c.y + ar * sn)
            let head = NSBezierPath()
            head.move(to: NSPoint(x: end.x - sn * 5.5, y: end.y + cs * 5.5))   // tip, along the clockwise tangent
            head.line(to: NSPoint(x: end.x + cs * 4.2, y: end.y + sn * 4.2))
            head.line(to: NSPoint(x: end.x - cs * 4.2, y: end.y - sn * 4.2))
            head.close()
            head.fill()
            return true
        }
        img.isTemplate = true
        img.accessibilityDescription = "AutoLangSwitcher"
        return img
    }
}

// MARK: - App

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    // Names used when the app was called Keyboard Memory.
    private static let legacyBundleID = "com.keyboardmemory.app"
    private static let legacyAgentLabel = "com.keyboardmemory.agent"
    private static let agentLabel = "com.autolangswitcher.agent"

    private let defaultsKey = "layouts"
    private var map: [String: String] = [:]      // app bundle id -> input source id
    private var currentApp: String?
    private var paused = false

    private var statusItem: NSStatusItem!
    private let appsMenu = NSMenu()
    private var pauseItem: NSMenuItem!
    private var loginItem: NSMenuItem!

    private func launchAgentURL(_ label: String) -> URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Only one copy at a time
        if let id = Bundle.main.bundleIdentifier,
           NSRunningApplication.runningApplications(withBundleIdentifier: id).count > 1 {
            NSApp.terminate(nil)
            return
        }
        // An older Keyboard Memory would fight over the input source; close it.
        NSRunningApplication.runningApplications(withBundleIdentifier: Self.legacyBundleID).forEach { $0.terminate() }

        // Until this app saves its own list, read the one Keyboard Memory left behind (never changed).
        map = UserDefaults.standard.dictionary(forKey: defaultsKey) as? [String: String]
            ?? UserDefaults(suiteName: Self.legacyBundleID)?.dictionary(forKey: defaultsKey) as? [String: String]
            ?? [:]
        migrateLaunchAgent()
        setupMenu()

        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(appActivated(_:)),
            name: NSWorkspace.didActivateApplicationNotification, object: nil)

        DistributedNotificationCenter.default().addObserver(
            self, selector: #selector(inputSourceChanged),
            name: NSNotification.Name(kTISNotifySelectedKeyboardInputSourceChanged as String),
            object: nil, suspensionBehavior: .deliverImmediately)

        if let front = NSWorkspace.shared.frontmostApplication { handleActivation(front) }
    }

    // MARK: Core logic

    @objc private func appActivated(_ note: Notification) {
        guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
        handleActivation(app)
    }

    private func handleActivation(_ app: NSRunningApplication) {
        guard let id = app.bundleIdentifier ?? app.executableURL?.lastPathComponent,
              id != Bundle.main.bundleIdentifier else { return }
        currentApp = id
        if paused { return }

        if let wanted = map[id] {
            if wanted != InputSources.currentID() {
                if !InputSources.select(wanted) {
                    NSLog("AutoLangSwitcher: input source \(wanted) is not enabled, skipping \(id)")
                }
            }
        } else if let cur = InputSources.currentID() {
            record(id, cur)   // first time we see this app: remember what it starts with
        }
    }

    // The user switched input source (menu, Ctrl+Space, Globe key...), or we did.
    @objc private func inputSourceChanged() {
        guard !paused, let app = currentApp, let cur = InputSources.currentID() else { return }
        record(app, cur)
    }

    private func record(_ app: String, _ source: String) {
        guard map[app] != source else { return }
        map[app] = source
        save()
    }

    private func save() {
        UserDefaults.standard.set(map, forKey: defaultsKey)
    }

    // MARK: Menu bar

    private func setupMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = MenuBarIcon.make()
            button.toolTip = "AutoLangSwitcher"
        }

        let menu = NSMenu()
        let about = NSMenuItem(title: "About AutoLangSwitcher", action: #selector(showAbout), keyEquivalent: "")
        about.target = self
        menu.addItem(about)
        menu.addItem(.separator())

        let appsItem = NSMenuItem(title: "Remembered Apps", action: nil, keyEquivalent: "")
        appsMenu.delegate = self
        appsItem.submenu = appsMenu
        menu.addItem(appsItem)
        menu.addItem(.separator())

        pauseItem = NSMenuItem(title: "Pause", action: #selector(togglePause), keyEquivalent: "")
        pauseItem.target = self
        menu.addItem(pauseItem)

        loginItem = NSMenuItem(title: "Start at Login", action: #selector(toggleLogin), keyEquivalent: "")
        loginItem.target = self
        loginItem.state = loginEnabled ? .on : .off
        menu.addItem(loginItem)

        let forget = NSMenuItem(title: "Forget All Apps", action: #selector(forgetAll), keyEquivalent: "")
        forget.target = self
        menu.addItem(forget)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit AutoLangSwitcher",
                                action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
    }

    // Rebuild the "Remembered Apps" submenu each time it opens
    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === appsMenu else { return }
        menu.removeAllItems()
        if map.isEmpty {
            let none = NSMenuItem(title: "(none yet)", action: nil, keyEquivalent: "")
            none.isEnabled = false
            menu.addItem(none)
            return
        }
        let hint = NSMenuItem(title: "Click an app to forget it", action: nil, keyEquivalent: "")
        hint.isEnabled = false
        menu.addItem(hint)
        menu.addItem(.separator())
        for (app, source) in map.sorted(by: { displayName($0.key).lowercased() < displayName($1.key).lowercased() }) {
            let item = NSMenuItem(title: "\(displayName(app))  →  \(InputSources.name(source))",
                                  action: #selector(forgetApp(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = app
            menu.addItem(item)
        }
    }

    private func displayName(_ bundleID: String) -> String {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) {
            return FileManager.default.displayName(atPath: url.path)
                .replacingOccurrences(of: ".app", with: "")
        }
        return bundleID
    }

    @objc private func forgetApp(_ sender: NSMenuItem) {
        guard let app = sender.representedObject as? String else { return }
        map.removeValue(forKey: app)
        save()
    }

    // Standard macOS About panel; the version comes from Info.plist (set in build.sh).
    // Edit the credit lines here (the Windows version has the same text in AutoLangSwitcherContext.cs).
    @objc private func showAbout() {
        let credits = """
            Remembers the keyboard language you use in each program and switches back to it when you return.

            Created by AniDavid
            Built with the help of Claude (Anthropic)
            """
        NSApp.activate(ignoringOtherApps: true)   // menu bar apps are in the background; bring the panel forward
        NSApp.orderFrontStandardAboutPanel(options: [
            .credits: NSAttributedString(string: credits, attributes: [
                .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize),
                .foregroundColor: NSColor.labelColor,
            ]),
        ])
    }

    @objc private func togglePause() {
        paused.toggle()
        pauseItem.state = paused ? .on : .off
    }

    @objc private func forgetAll() {
        let alert = NSAlert()
        alert.messageText = "Forget the remembered input source for all apps?"
        alert.addButton(withTitle: "Forget All")
        alert.addButton(withTitle: "Cancel")
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn {
            map.removeAll()
            save()
        }
    }

    // MARK: Start at login
    // A LaunchAgent in ~/Library/LaunchAgents starts the app at login.

    private var loginEnabled: Bool {
        let fm = FileManager.default
        return fm.fileExists(atPath: launchAgentURL(Self.agentLabel).path)
            || fm.fileExists(atPath: launchAgentURL(Self.legacyAgentLabel).path)
    }

    private func writeLaunchAgent() {
        guard let exe = Bundle.main.executablePath else { return }
        let url = launchAgentURL(Self.agentLabel)
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let plist: NSDictionary = [
            "Label": Self.agentLabel,
            "ProgramArguments": [exe],
            "RunAtLoad": true,
            "ProcessType": "Interactive",
        ]
        plist.write(to: url, atomically: true)
    }

    // Keyboard Memory's LaunchAgent points at the old app. Replace it, removing the old one
    // only once the new one exists.
    private func migrateLaunchAgent() {
        let fm = FileManager.default
        let legacy = launchAgentURL(Self.legacyAgentLabel)
        guard fm.fileExists(atPath: legacy.path) else { return }
        if !fm.fileExists(atPath: launchAgentURL(Self.agentLabel).path) { writeLaunchAgent() }
        if fm.fileExists(atPath: launchAgentURL(Self.agentLabel).path) { try? fm.removeItem(at: legacy) }
    }

    @objc private func toggleLogin() {
        if loginEnabled {
            try? FileManager.default.removeItem(at: launchAgentURL(Self.agentLabel))
            try? FileManager.default.removeItem(at: launchAgentURL(Self.legacyAgentLabel))
        } else {
            writeLaunchAgent()
        }
        loginItem.state = loginEnabled ? .on : .off
    }
}

// MARK: - Main

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)   // menu bar only, no Dock icon
app.run()
