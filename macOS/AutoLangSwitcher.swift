// AutoLangSwitcher for macOS - remembers the keyboard input source you last used in each
// application and restores it when that application becomes active again.
//
// Build: ./build.sh  (needs Apple's Command Line Tools: xcode-select --install)
// Data:  stored in the app's preferences (defaults domain com.autolangswitcher.app).
//        Until the first change, the old com.keyboardmemory.app preferences are read instead.
// One file on purpose: swiftc treats a single file's top-level code (bottom) as the program start.

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

    static func name(_ id: String?) -> String {
        guard let id = id else { return "unknown" }
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
final class AppDelegate: NSObject, NSApplicationDelegate {
    // Names used when the app was called Keyboard Memory.
    static let legacyBundleID = "com.keyboardmemory.app"
    static let legacyAgentLabel = "com.keyboardmemory.agent"
    static let agentLabel = "com.autolangswitcher.agent"
    static let timeFormat: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss.SSS"
        return f
    }()

    let defaultsKey = "layouts"
    var map: [String: String] = [:]      // app bundle id -> input source id
    var currentApp: String?
    var paused = false
    var logLines: [String] = []

    // An automatic switch being checked by verifySwitch (same idea as the Windows version).
    var expected: String?        // input source the switch asked for
    var retries = 0              // re-requests already made for this switch
    var failedSource: String?    // source left behind by a failed switch; not the user's choice

    var statusItem: NSStatusItem!
    var pauseItem: NSMenuItem!

    // Settings window parts, built in the extension below
    var settingsWindow: NSWindow?
    var settingsTabs: NSTabView?
    var appsTable: NSTableView?
    var removeButton: NSButton?
    var appRows: [(app: String, source: String)] = []
    var pauseBox: NSButton?
    var loginBox: NSButton?
    var savedListView: NSTextView?
    var logView: NSTextView?

    func launchAgentURL(_ label: String) -> URL {
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
        log("started, \(map.count) app(s) loaded")

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

    @objc func appActivated(_ note: Notification) {
        guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
        handleActivation(app)
    }

    func handleActivation(_ app: NSRunningApplication) {
        NSObject.cancelPreviousPerformRequests(withTarget: self, selector: #selector(verifySwitch), object: nil)
        expected = nil
        failedSource = nil
        guard let id = app.bundleIdentifier ?? app.executableURL?.lastPathComponent else { return }
        // Our own Settings window: don't record source changes made while it is in front.
        if id == Bundle.main.bundleIdentifier {
            currentApp = nil
            return
        }
        currentApp = id
        if paused { return }

        let cur = InputSources.currentID()
        guard let wanted = map[id] else {
            if let cur = cur { record(id, cur) }   // first time we see this app: remember what it starts with
            return
        }
        if InputSources.find(wanted) == nil {
            log("focus \(id): saved input source \(InputSources.name(wanted)) is not enabled, skipping")
            return
        }
        log("auto switch \(id): \(InputSources.name(cur)) -> \(InputSources.name(wanted))")
        if wanted != cur { InputSources.select(wanted) }
        // Check shortly that the switch stuck; source changes are not recorded until then.
        expected = wanted
        retries = 0
        perform(#selector(verifySwitch), with: nil, afterDelay: 0.6)
    }

    @objc func verifySwitch() {
        guard let want = expected, let app = currentApp else { return }
        let cur = InputSources.currentID()
        if cur == want {
            expected = nil
            return
        }
        if retries < 2 {
            retries += 1
            InputSources.select(want)
            log("auto switch retry \(retries) \(app): \(InputSources.name(cur)) -> \(InputSources.name(want))")
            perform(#selector(verifySwitch), with: nil, afterDelay: 0.5)
            return
        }
        // Keep the remembered source instead of mistaking the unchanged one for the user's choice.
        log("auto switch FAILED \(app): still \(InputSources.name(cur)), keeping \(InputSources.name(want))")
        failedSource = cur
        expected = nil
    }

    // The user switched input source (menu, Ctrl+Space, Globe key...), or we did.
    @objc func inputSourceChanged() {
        guard !paused, expected == nil, let app = currentApp,
              let cur = InputSources.currentID(), cur != failedSource else { return }
        record(app, cur)
    }

    func record(_ app: String, _ source: String) {
        let old = map[app]
        guard old != source else { return }
        map[app] = source
        save()
        // Automatic switches set the remembered source, so any other change came from the user.
        if let old = old {
            log("manual switch \(app): \(InputSources.name(old)) -> \(InputSources.name(source))")
        } else {
            log("remember new app \(app): \(InputSources.name(source))")
        }
    }

    func save() {
        UserDefaults.standard.set(map, forKey: defaultsKey)
    }

    // MARK: Debug log (shown on the Log tab)

    func log(_ msg: String) {
        logLines.append(Self.timeFormat.string(from: Date()) + "  " + msg)
        if logLines.count > 500 { logLines.removeFirst(logLines.count - 500) }
    }

    // MARK: Menu bar

    func setupMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = MenuBarIcon.make()
            button.toolTip = "AutoLangSwitcher"
        }

        let menu = NSMenu()
        let settings = NSMenuItem(title: "Settings…", action: #selector(showSettings), keyEquivalent: ",")
        settings.target = self
        menu.addItem(settings)

        pauseItem = NSMenuItem(title: "Pause", action: #selector(togglePause), keyEquivalent: "")
        pauseItem.target = self
        menu.addItem(pauseItem)
        menu.addItem(.separator())

        let about = NSMenuItem(title: "About AutoLangSwitcher", action: #selector(showAbout), keyEquivalent: "")
        about.target = self
        menu.addItem(about)
        menu.addItem(NSMenuItem(title: "Quit AutoLangSwitcher",
                                action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
    }

    func displayName(_ bundleID: String) -> String {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) {
            return FileManager.default.displayName(atPath: url.path)
                .replacingOccurrences(of: ".app", with: "")
        }
        return bundleID
    }

    // Standard macOS About panel; the version comes from Info.plist (set in build.sh).
    // Edit the credit lines here (the Windows version has the same text in AutoLangSwitcherContext.cs).
    @objc func showAbout() {
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

    func setPaused(_ value: Bool) {
        paused = value
        pauseItem.state = value ? .on : .off
        pauseBox?.state = value ? .on : .off
    }

    @objc func togglePause() {
        setPaused(!paused)
    }

    func forgetAll() {
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

    var loginEnabled: Bool {
        let fm = FileManager.default
        return fm.fileExists(atPath: launchAgentURL(Self.agentLabel).path)
            || fm.fileExists(atPath: launchAgentURL(Self.legacyAgentLabel).path)
    }

    func writeLaunchAgent() {
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
    func migrateLaunchAgent() {
        let fm = FileManager.default
        let legacy = launchAgentURL(Self.legacyAgentLabel)
        guard fm.fileExists(atPath: legacy.path) else { return }
        if !fm.fileExists(atPath: launchAgentURL(Self.agentLabel).path) { writeLaunchAgent() }
        if fm.fileExists(atPath: launchAgentURL(Self.agentLabel).path) { try? fm.removeItem(at: legacy) }
    }

    // Returns the state read back from disk after the change.
    func setLogin(_ enable: Bool) -> Bool {
        if enable {
            writeLaunchAgent()
        } else {
            try? FileManager.default.removeItem(at: launchAgentURL(Self.agentLabel))
            try? FileManager.default.removeItem(at: launchAgentURL(Self.legacyAgentLabel))
        }
        let on = loginEnabled
        log("start at login: " + (on ? "on" : "off"))
        return on
    }
}

// MARK: - Settings window
// Same tabs as the Windows version (SettingsWindow.cs): Apps, General, Saved list, Log, Help.

extension AppDelegate: NSTableViewDataSource, NSTableViewDelegate, NSTabViewDelegate {

    @objc func showSettings() {
        if settingsWindow == nil { buildSettingsWindow() }
        refreshSelectedTab()
        NSApp.activate(ignoringOtherApps: true)   // menu bar apps are in the background
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    func buildSettingsWindow() {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 440),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "AutoLangSwitcher Settings"
        window.minSize = NSSize(width: 420, height: 320)
        window.isReleasedWhenClosed = false   // reused each time Settings is opened
        window.center()

        let tabs = NSTabView()
        tabs.addTabViewItem(tabItem("Apps", appsTab()))
        tabs.addTabViewItem(tabItem("General", generalTab()))
        tabs.addTabViewItem(tabItem("Saved list", savedListTab()))
        tabs.addTabViewItem(tabItem("Log", logTab()))
        tabs.addTabViewItem(tabItem("Help", helpTab()))
        tabs.delegate = self   // set after adding, so building the tabs doesn't trigger a refresh

        let content = NSView()
        tabs.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(tabs)
        NSLayoutConstraint.activate([
            tabs.topAnchor.constraint(equalTo: content.topAnchor, constant: 12),
            tabs.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 12),
            tabs.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -12),
            tabs.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12),
        ])
        window.contentView = content
        settingsTabs = tabs
        settingsWindow = window
    }

    // Reload the visible tab each time it is shown, so it is never out of date.
    func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        refreshSelectedTab()
    }

    func refreshSelectedTab() {
        pauseBox?.state = paused ? .on : .off
        switch settingsTabs?.selectedTabViewItem?.identifier as? String {
        case "Apps": fillApps()
        case "General": loginBox?.state = loginEnabled ? .on : .off
        case "Saved list": fillSavedList()
        case "Log": fillLog()
        default: break
        }
    }

    // MARK: Layout helpers

    func tabItem(_ label: String, _ view: NSView) -> NSTabViewItem {
        let item = NSTabViewItem(identifier: label)
        item.label = label
        view.autoresizingMask = [.width, .height]   // follow the tab view when the window is resized
        item.view = view
        return item
    }

    // A tab page: the main view fills it, with an optional row of buttons along the bottom.
    func page(_ main: NSView, _ buttons: [NSView]) -> NSView {
        let v = NSView(frame: NSRect(x: 0, y: 0, width: 520, height: 360))
        main.translatesAutoresizingMaskIntoConstraints = false
        v.addSubview(main)
        var c = [
            main.topAnchor.constraint(equalTo: v.topAnchor, constant: 12),
            main.leadingAnchor.constraint(equalTo: v.leadingAnchor, constant: 12),
            main.trailingAnchor.constraint(equalTo: v.trailingAnchor, constant: -12),
        ]
        if buttons.isEmpty {
            c.append(main.bottomAnchor.constraint(equalTo: v.bottomAnchor, constant: -12))
        } else {
            let bar = NSStackView(views: buttons)
            bar.orientation = .horizontal
            bar.translatesAutoresizingMaskIntoConstraints = false
            v.addSubview(bar)
            c += [
                bar.topAnchor.constraint(equalTo: main.bottomAnchor, constant: 8),
                bar.leadingAnchor.constraint(equalTo: v.leadingAnchor, constant: 12),
                bar.trailingAnchor.constraint(lessThanOrEqualTo: v.trailingAnchor, constant: -12),
                bar.bottomAnchor.constraint(equalTo: v.bottomAnchor, constant: -12),
            ]
        }
        NSLayoutConstraint.activate(c)
        return v
    }

    func makeTextView() -> (NSScrollView, NSTextView) {
        let scroll = NSTextView.scrollableTextView()
        let text = scroll.documentView as! NSTextView
        text.isEditable = false
        text.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        return (scroll, text)
    }

    // MARK: Apps tab

    func appsTab() -> NSView {
        let table = NSTableView()
        for (id, title, width) in [("app", "Program", CGFloat(240)), ("source", "Input source", CGFloat(220))] {
            let col = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            col.title = title
            col.width = width
            table.addTableColumn(col)
        }
        table.allowsMultipleSelection = true
        table.usesAlternatingRowBackgroundColors = true
        table.dataSource = self
        table.delegate = self
        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        appsTable = table

        let remove = NSButton(title: "Remove", target: self, action: #selector(removeSelectedApps))
        remove.isEnabled = false
        removeButton = remove
        let removeAll = NSButton(title: "Remove all", target: self, action: #selector(removeAllApps))
        return page(scroll, [remove, removeAll])
    }

    func fillApps() {
        appRows = map.map { (app: $0.key, source: $0.value) }
            .sorted { displayName($0.app).lowercased() < displayName($1.app).lowercased() }
        appsTable?.reloadData()
        removeButton?.isEnabled = false
    }

    func numberOfRows(in tableView: NSTableView) -> Int {
        appRows.count
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let r = appRows[row]
        let isApp = tableColumn?.identifier.rawValue == "app"
        let field = NSTextField(labelWithString: isApp ? displayName(r.app) : InputSources.name(r.source))
        field.lineBreakMode = .byTruncatingTail
        if isApp { field.toolTip = r.app }
        return field
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        removeButton?.isEnabled = !(appsTable?.selectedRowIndexes.isEmpty ?? true)
    }

    @objc func removeSelectedApps() {
        guard let table = appsTable else { return }
        for i in table.selectedRowIndexes where i < appRows.count {
            map.removeValue(forKey: appRows[i].app)
        }
        save()
        fillApps()
    }

    @objc func removeAllApps() {
        forgetAll()
        fillApps()
    }

    // MARK: General tab

    func generalTab() -> NSView {
        let pause = NSButton(checkboxWithTitle: "Pause (stop switching input sources for now)",
                             target: self, action: #selector(pauseBoxClicked(_:)))
        let login = NSButton(checkboxWithTitle: "Start at Login", target: self, action: #selector(loginBoxClicked(_:)))
        pauseBox = pause
        loginBox = login
        let stack = NSStackView(views: [pause, login])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        let v = NSView(frame: NSRect(x: 0, y: 0, width: 520, height: 360))
        stack.translatesAutoresizingMaskIntoConstraints = false
        v.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: v.topAnchor, constant: 16),
            stack.leadingAnchor.constraint(equalTo: v.leadingAnchor, constant: 16),
        ])
        return v
    }

    @objc func pauseBoxClicked(_ sender: NSButton) {
        setPaused(sender.state == .on)   // also updates the menu bar item
    }

    @objc func loginBoxClicked(_ sender: NSButton) {
        sender.state = setLogin(sender.state == .on) ? .on : .off   // show what is really on disk
    }

    // MARK: Saved list tab (Windows shows its settings file here; macOS keeps the list in preferences)

    func savedListTab() -> NSView {
        let (scroll, text) = makeTextView()
        savedListView = text
        let refresh = NSButton(title: "Refresh", target: self, action: #selector(fillSavedList))
        return page(scroll, [refresh])
    }

    @objc func fillSavedList() {
        let domain = Bundle.main.bundleIdentifier ?? "com.autolangswitcher.app"
        var s = "Stored in macOS preferences: ~/Library/Preferences/\(domain).plist\n"
        s += "Format: app id = input source id ; name\n\n"
        if map.isEmpty { s += "(no apps remembered yet)\n" }
        for (app, source) in map.sorted(by: { $0.key < $1.key }) {
            s += "\(app) = \(source) ; \(InputSources.name(source))\n"
        }
        savedListView?.string = s
    }

    // MARK: Log tab

    func logTab() -> NSView {
        let (scroll, text) = makeTextView()
        logView = text
        let refresh = NSButton(title: "Refresh", target: self, action: #selector(fillLog))
        let copy = NSButton(title: "Copy all", target: self, action: #selector(copyLog))
        let clear = NSButton(title: "Clear", target: self, action: #selector(clearLog))
        let legend = NSTextField(labelWithString: "Blue: you switched   Green: app switched   Gray: focus   Red: problem")
        legend.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        legend.textColor = .secondaryLabelColor
        return page(scroll, [refresh, copy, clear, legend])
    }

    @objc func fillLog() {
        guard let text = logView else { return }
        let font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        let out = NSMutableAttributedString()
        for line in logLines {
            out.append(NSAttributedString(string: line + "\n",
                                          attributes: [.font: font, .foregroundColor: logColor(line)]))
        }
        text.textStorage?.setAttributedString(out)
        text.scrollToEndOfDocument(nil)
    }

    // Colors follow the wording used by log() calls above.
    func logColor(_ line: String) -> NSColor {
        if line.contains("FAILED") || line.contains("not enabled") { return .systemRed }
        if line.contains("  manual switch ") || line.contains("  remember ") { return .systemBlue }
        if line.contains("  auto switch ") { return .systemGreen }
        if line.contains("  focus ") { return .secondaryLabelColor }
        return .labelColor
    }

    @objc func copyLog() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(logLines.joined(separator: "\n"), forType: .string)
    }

    @objc func clearLog() {
        logLines.removeAll()
        fillLog()
    }

    // MARK: Help tab

    func helpTab() -> NSView {
        let (scroll, text) = makeTextView()
        text.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        text.string = Self.helpText
        return page(scroll, [])
    }

    static let helpText = """
        WHAT IT DOES
        AutoLangSwitcher remembers the keyboard input source you last used in each app and switches back to it when you return to that app. For example: Hebrew in WhatsApp, English in your code editor.

        HOW TO USE IT
        There is nothing to set up. Just use your Mac as usual:
          - Switch the input source in an app (Ctrl+Space, the Globe key, or the input menu in the menu bar). AutoLangSwitcher remembers it.
          - When you come back to that app, even after a restart, its input source comes back by itself.
          - To change an app's input source, just switch it again. The new choice replaces the old one.

        It remembers by app, not by window or tab: all browser tabs share one input source.

        HOW IT WORKS
          1. macOS tells AutoLangSwitcher each time a different app comes to the front.
          2. For that app, it looks up the saved input source and switches to it.
          3. About half a second later it checks that the switch worked, and asks again if it didn't.
          4. macOS also tells it whenever you change the input source, so your choice is remembered right away for the app in front.

        It never reads what you type, and it does not fix text typed in the wrong language.

        THE TABS
          Apps: the apps it remembers. Select some and click Remove to forget them.
          General: pause it, and start it when you log in.
          Saved list: the remembered list, as stored in the app's preferences.
          Log: what it has been doing. Blue lines are switches you made, green lines are switches it made, red lines are problems.

        TIPS
          - Turn off macOS's own "Automatically switch to a document's input source" so the two don't fight. It's in System Settings > Keyboard > Text Input > Input Sources > Edit.
          - If an app keeps getting the wrong input source, open the Log tab right after it happens, click Copy all, and paste it into an issue on GitHub:
            https://github.com/AniDavid/AutoLangSwitcher/issues
          - Pause it from the menu bar if you want to switch input sources by hand for a while.
        """
}

// MARK: - Main

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)   // menu bar only, no Dock icon
app.run()
