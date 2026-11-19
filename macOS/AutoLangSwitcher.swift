// AutoLangSwitcher for macOS - remembers the keyboard input source you last used in each
// application and restores it when that application becomes active again.
//
// Build: ./build.sh  (needs Apple's Command Line Tools: xcode-select --install)
// Data:  stored in the app's preferences (defaults domain com.autolangswitcher.app).
//        Until the first change, the old com.keyboardmemory.app preferences are read instead.
// Smart Keyboard Auto Switch is in SmartSwitch.swift. The program starts at Main (bottom).

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
    // caps: Caps Lock is on - orange (not a template, so macOS keeps the colour) with an "A" instead of the keys.
    static func make(caps: Bool = false) -> NSImage {
        // Flipped (y points down) so the coordinates match the Windows drawing on a 64x64 grid.
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { _ in
            guard let ctx = NSGraphicsContext.current else { return false }
            let scale = NSAffineTransform()
            scale.scale(by: 18.0 / 64.0)
            scale.concat()
            (caps ? NSColor.systemOrange : NSColor.black).set()

            // Keyboard body
            NSBezierPath(roundedRect: NSRect(x: 2, y: 12, width: 56, height: 38), xRadius: 9, yRadius: 9).fill()

            // Cut out the keys, the space bar and a ring around the badge
            let c = NSPoint(x: 48, y: 48)
            let r: CGFloat = 14
            ctx.compositingOperation = .destinationOut
            if caps {
                // A bold capital "A" (outline plus its triangular hole), so the state doesn't depend on colour.
                let letter = NSBezierPath()
                letter.windingRule = .evenOdd
                let outline: [NSPoint] = [NSPoint(x: 19, y: 15), NSPoint(x: 29, y: 15), NSPoint(x: 40, y: 47),
                                          NSPoint(x: 32, y: 47), NSPoint(x: 29.5, y: 40), NSPoint(x: 18.5, y: 40),
                                          NSPoint(x: 16, y: 47), NSPoint(x: 8, y: 47)]
                let hole: [NSPoint] = [NSPoint(x: 24, y: 24), NSPoint(x: 27.2, y: 34), NSPoint(x: 20.8, y: 34)]
                for shape in [outline, hole] {
                    letter.move(to: shape[0])
                    shape.dropFirst().forEach { letter.line(to: $0) }
                    letter.close()
                }
                letter.fill()
            } else {
                for row in 0..<2 {
                    for col in 0..<4 {
                        let key = NSRect(x: 8 + CGFloat(col) * 11, y: 18 + CGFloat(row) * 10, width: 7, height: 6)
                        NSBezierPath(roundedRect: key, xRadius: 1.5, yRadius: 1.5).fill()
                    }
                }
                NSBezierPath(roundedRect: NSRect(x: 14, y: 39, width: 24, height: 6), xRadius: 1.5, yRadius: 1.5).fill()
            }
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
        img.isTemplate = !caps
        img.accessibilityDescription = caps ? "AutoLangSwitcher, Caps Lock on" : "AutoLangSwitcher"
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
    var capsOn = false
    let normalIcon = MenuBarIcon.make()
    let capsIcon = MenuBarIcon.make(caps: true)
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
    var smartOffButtons: [NSButton] = []
    var pauseBox: NSButton?
    var loginBox: NSButton?
    var savedListView: NSTextView?
    var logView: NSTextView?
    var smartBox: NSButton?
    var smartRadios: [NSButton] = []   // only suggest, fix automatically
    var smartKeyPopup: NSPopUpButton?
    var keepView: NSTextView?
    var keepShown = Set<String>()   // the words keepView was filled with
    var offView: NSTextView?

    lazy var smart = SmartSwitch(app: self)

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
        smart.load()
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
        smart.switched(to: app.processIdentifier)
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

    @objc func checkCapsLock() {
        let on = CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
        guard on != capsOn, let button = statusItem.button else { return }
        capsOn = on
        button.image = on ? capsIcon : normalIcon
        button.toolTip = on ? "CAPS LOCK ON - AutoLangSwitcher" : "AutoLangSwitcher"
    }

    func setupMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = normalIcon
            button.toolTip = "AutoLangSwitcher"
        }
        // Caps Lock indicator. Polling the modifier state needs no special permission (a key monitor would).
        Timer.scheduledTimer(timeInterval: 0.3, target: self, selector: #selector(checkCapsLock),
                             userInfo: nil, repeats: true)

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
        tabs.addTabViewItem(tabItem("Smart switch off", offAppsTab()))
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
        case "General":
            loginBox?.state = loginEnabled ? .on : .off
            showKeepWords()   // a fix undone since adds words
        case "Smart switch off": offView?.string = smart.offAppsText
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
        for (id, title, width) in [("app", "Program", CGFloat(220)), ("source", "Input source", CGFloat(170)),
                                   ("smart", "Smart switch", CGFloat(90))] {
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
        // Select apps (Command-click or Shift-click for several) to add them to, or take them off, the
        // Smart switch off list.
        let off = NSButton(title: "Smart switch off", target: self, action: #selector(smartOffForSelected))
        let on = NSButton(title: "Smart switch on", target: self, action: #selector(smartOnForSelected))
        smartOffButtons = [off, on]
        return page(scroll, [remove, removeAll, off, on])
    }

    func fillApps() {
        appRows = map.map { (app: $0.key, source: $0.value) }
            .sorted { displayName($0.app).lowercased() < displayName($1.app).lowercased() }
        appsTable?.reloadData()
        removeButton?.isEnabled = false
        smartOffButtons.forEach { $0.isEnabled = false }
    }

    func numberOfRows(in tableView: NSTableView) -> Int {
        appRows.count
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let r = appRows[row]
        let column = tableColumn?.identifier.rawValue
        let isApp = column == "app"
        let text = isApp ? displayName(r.app) : column == "smart" ? (smart.isOff(r.app) ? "off" : "") : InputSources.name(r.source)
        let field = NSTextField(labelWithString: text)
        field.lineBreakMode = .byTruncatingTail
        if isApp { field.toolTip = r.app }
        return field
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        let any = !(appsTable?.selectedRowIndexes.isEmpty ?? true)
        removeButton?.isEnabled = any
        smartOffButtons.forEach { $0.isEnabled = any }
    }

    @objc func smartOffForSelected() { setSelectedAppsOff(true) }
    @objc func smartOnForSelected() { setSelectedAppsOff(false) }

    func setSelectedAppsOff(_ off: Bool) {
        guard let table = appsTable else { return }
        smart.setAppsOff(table.selectedRowIndexes.filter { $0 < appRows.count }.map { appRows[$0].app }, off)
        fillApps()
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
        let smartOn = NSButton(checkboxWithTitle: "Smart Keyboard Auto Switch: fix words typed in the wrong input source",
                               target: self, action: #selector(smartChanged(_:)))
        let note = NSTextField(wrappingLabelWithString: "Reads your typing to check each finished word. Nothing is saved or sent, except the list below. " +
                               "Needs the Accessibility permission.")
        note.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        note.textColor = .secondaryLabelColor
        note.preferredMaxLayoutWidth = 440
        // Radio buttons with the same action in the same view work as one group.
        let suggest = NSButton(radioButtonWithTitle: "Only suggest (press the fix key to fix the word)",
                               target: self, action: #selector(smartChanged(_:)))
        let auto = NSButton(radioButtonWithTitle: "Fix automatically", target: self, action: #selector(smartChanged(_:)))
        smartOn.state = smart.on ? .on : .off
        (smart.auto ? auto : suggest).state = .on
        smartBox = smartOn
        smartRadios = [suggest, auto]
        let keyPopup = NSPopUpButton(frame: .zero, pullsDown: false)
        keyPopup.addItems(withTitles: SmartSwitch.fixKeys.map { $0.name })
        keyPopup.selectItem(at: smart.fixKey)
        keyPopup.target = self
        keyPopup.action = #selector(smartChanged(_:))
        smartKeyPopup = keyPopup
        let keyRow = NSStackView(views: [NSTextField(labelWithString: "Fix key:"), keyPopup,
                                         NSTextField(labelWithString: "(also undoes a fix, or changes the last word)")])
        let keepLabel = NSTextField(wrappingLabelWithString: "Never change these words (as they appear when typed; words you undo are added here):")
        keepLabel.preferredMaxLayoutWidth = 440
        let (keepScroll, keepText) = makeTextView()
        keepText.isEditable = true
        keepView = keepText
        showKeepWords()
        keepScroll.borderType = .bezelBorder
        keepScroll.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([keepScroll.widthAnchor.constraint(equalToConstant: 320),
                                     keepScroll.heightAnchor.constraint(equalToConstant: 70)])
        let saveKeep = NSButton(title: "Save list", target: self, action: #selector(saveKeepWords))
        let smartGroup = NSStackView(views: [note, suggest, auto, keyRow, keepLabel, keepScroll, saveKeep])
        smartGroup.orientation = .vertical
        smartGroup.alignment = .leading
        smartGroup.spacing = 6
        smartGroup.edgeInsets = NSEdgeInsets(top: 0, left: 20, bottom: 0, right: 0)
        updateSmartControls()
        let stack = NSStackView(views: [pause, login, smartOn, smartGroup])
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

    @objc func smartChanged(_ sender: Any) {
        smart.set(on: smartBox?.state == .on, auto: smartRadios.last?.state == .on,
                  key: max(smartKeyPopup?.indexOfSelectedItem ?? 0, 0))
        updateSmartControls()
    }

    func updateSmartControls() {
        smartRadios.forEach { $0.isEnabled = smart.on }
        smartKeyPopup?.isEnabled = smart.on
        keepView?.isEditable = smart.on
    }

    func showKeepWords() {
        keepView?.string = smart.keepWordsText
        keepShown = smart.keepWords
    }

    @objc func saveKeepWords() {
        // Words added by an undo since the box was filled aren't in it: keep them.
        smart.setKeepWords(keepView?.string ?? "", keeping: smart.keepWords.subtracting(keepShown))
        showKeepWords()
    }

    // MARK: Smart switch off tab

    func offAppsTab() -> NSView {
        let (scroll, text) = makeTextView()
        text.isEditable = true
        offView = text
        let label = NSTextField(wrappingLabelWithString: "Smart Keyboard Auto Switch stays off in these apps (bundle ids, one per line; " +
                                "\"com.jetbrains.*\" matches a family). Code editors and terminals are here to start with: " +
                                "keys there can be commands, and auto-complete changes the text.")
        label.preferredMaxLayoutWidth = 480
        let column = NSStackView(views: [label, scroll])
        column.orientation = .vertical
        column.alignment = .leading
        column.spacing = 8
        scroll.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([scroll.widthAnchor.constraint(equalTo: column.widthAnchor)])
        let save = NSButton(title: "Save", target: self, action: #selector(saveOffApps))
        return page(column, [save])
    }

    @objc func saveOffApps() {
        smart.setOffApps(offView?.string ?? "")
        offView?.string = smart.offAppsText
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
        let legend = NSTextField(labelWithString: "Blue: you switched   Green: app switched   Purple: smart switch   Gray: focus   Red: problem")
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
        if line.contains("  smart switch ") { return .systemPurple }
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
        text.drawsBackground = true
        text.backgroundColor = .white   // the page has its own colors, also in dark mode
        text.textContainerInset = NSSize(width: 8, height: 8)
        let key = SmartSwitch.fixKeys[smart.fixKey].name
        if let help = Self.helpPage(fixKey: key) { text.textStorage?.setAttributedString(help) }
        return page(scroll, [])
    }

    // The Help page as rich text. {IMG:name} becomes the picture help-name.png from the app's Resources
    // (made with --screenshots, copied in by build.sh), or nothing if it isn't there.
    static func helpPage(fixKey: String) -> NSAttributedString? {
        var html = helpHTML.replacingOccurrences(of: "{FIXKEY}", with: fixKey)
        for name in shotNames {
            let img = Bundle.main.url(forResource: "help-\(name)", withExtension: "png") != nil
                ? "<p><img src='help-\(name).png'></p>" : ""
            html = html.replacingOccurrences(of: "{IMG:\(name)}", with: img)
        }
        guard let data = html.data(using: .utf8),
              let page = NSMutableAttributedString(html: data, baseURL: Bundle.main.resourceURL ?? URL(fileURLWithPath: "/"),
                                                   documentAttributes: nil) else { return nil }
        page.enumerateAttribute(.paragraphStyle, in: NSRange(location: 0, length: page.length)) { value, range, _ in
            guard let style = value as? NSParagraphStyle, !style.textLists.isEmpty,
                  let plain = style.mutableCopy() as? NSMutableParagraphStyle else { return }
            plain.textLists = []
            page.addAttribute(.paragraphStyle, value: plain, range: range)
        }
        // Shrink the pictures to the width of the tab.
        page.enumerateAttribute(.attachment, in: NSRange(location: 0, length: page.length)) { value, _, _ in
            guard let a = value as? NSTextAttachment else { return }
            let size = a.image?.size ?? a.bounds.size
            if size.width > 460 { a.bounds = NSRect(x: 0, y: 0, width: 460, height: size.height * 460 / size.width) }
        }
        return page
    }

    static let shotNames = ["apps", "general", "off"]   // the first three tabs, in order

    // AutoLangSwitcher --screenshots <folder>: saves the first three Settings tabs as help-<name>.png,
    // using example apps (not yours). Nothing is loaded or saved.
    func takeScreenshots(to dir: String) {
        // Names that aren't installed apps or enabled input sources are shown as they are.
        map = ["WhatsApp": "Hebrew", "Microsoft Word": "Hebrew", "Mail": "Hebrew", "Safari": "ABC", "com.apple.Terminal": "ABC"]
        smart.offApps = Set(SmartSwitch.defaultOffApps.map { $0.lowercased() })
        smart.on = true   // so the General tab shows its options enabled
        buildSettingsWindow()
        guard let window = settingsWindow, let tabs = settingsTabs, let view = window.contentView else { exit(1) }
        window.appearance = NSAppearance(named: .aqua)   // light, to match the Help page
        let folder = URL(fileURLWithPath: dir)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        view.layoutSubtreeIfNeeded()
        if let warm = view.bitmapImageRepForCachingDisplay(in: view.bounds) { view.cacheDisplay(in: view.bounds, to: warm) }   // the first drawing misses the tab bar
        for (i, name) in Self.shotNames.enumerated() {
            tabs.selectTabViewItem(at: i)
            refreshSelectedTab()
            view.layoutSubtreeIfNeeded()
            guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { continue }
            view.cacheDisplay(in: view.bounds, to: rep)
            try? rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent("help-\(name).png"))
        }
        // The Help tab itself, to check how the page looks (not used in the page).
        guard let help = Self.helpPage(fixKey: "Control+Option+Z") else { print("the Help page could not be read"); exit(2) }
        var pictures = 0
        help.enumerateAttribute(.attachment, in: NSRange(location: 0, length: help.length)) { v, _, _ in if v != nil { pictures += 1 } }
        print("Help page: \(help.length) characters, \(pictures) picture(s)")
        tabs.selectTabViewItem(at: 5)
        view.layoutSubtreeIfNeeded()
        if let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
            view.cacheDisplay(in: view.bounds, to: rep)
            try? rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent("check-help.png"))
        }
    }

    // The Help tab's page. NSAttributedString reads simple HTML and CSS only. Hebrew is written as entities.
    static let helpHTML = """
        <html><head><meta charset='utf-8'><style>
        body { font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px; color: #222; }
        h1 { font-size: 20px; color: #1a5fb4; margin: 0 0 4px; }
        h2 { font-size: 15px; color: #1a5fb4; margin: 18px 0 4px; }
        p { margin: 5px 0; }
        li { margin: 2px 0; }
        code, kbd { font-family: Menlo, monospace; font-size: 12px; color: #333; background-color: #f0f0f0; }
        .lead { color: #555; }
        .note { color: #1a4f8b; }
        .he { font-weight: bold; }
        .blue { color: #1a5fb4; font-weight: bold; } .green { color: #2e8b3d; font-weight: bold; }
        .purple { color: #8a3ab9; font-weight: bold; } .gray { color: #777; font-weight: bold; } .red { color: #c01c28; font-weight: bold; }
        a { color: #1a5fb4; }
        </style></head><body>

        <h1>AutoLangSwitcher</h1>
        <p class='lead'>Remembers the keyboard input source you last used in each app, and switches back to it when you return.
        For example: Hebrew in WhatsApp, English in your code editor.</p>

        <h2>How to use it</h2>
        <p>There is nothing to set up. Just use your Mac as usual:</p>
        <ul>
        <li>Switch the input source in an app (<kbd>Control</kbd>+<kbd>Space</kbd>, the Globe key, or the input menu in the menu bar). AutoLangSwitcher remembers it.</li>
        <li>When you come back to that app, even after a restart, its input source comes back by itself.</li>
        <li>To change an app's input source, just switch it again. The new choice replaces the old one.</li>
        </ul>
        <p class='note'>It remembers by <b>app</b>, not by window or tab: all browser tabs share one input source.</p>

        <h2>The tabs</h2>
        {IMG:apps}
        <ul>
        <li><b>Apps:</b> the apps it remembers and their input sources. Select some (<kbd>Command</kbd>-click for several) and click
        <b>Remove</b> to forget them, or <b>Smart switch off</b> / <b>Smart switch on</b> to choose where Smart Keyboard Auto Switch works.</li>
        <li><b>General:</b> pause it, start it when you log in, and Smart Keyboard Auto Switch.</li>
        <li><b>Smart switch off:</b> the apps where Smart Keyboard Auto Switch stays off, by bundle id (<code>com.jetbrains.*</code> matches a family).</li>
        <li><b>Saved list:</b> the remembered list, as stored in the app's preferences.</li>
        <li><b>Log:</b> what it has been doing: <span class='blue'>blue</span> switches you made, <span class='green'>green</span> switches it made,
        <span class='purple'>purple</span> smart switch, <span class='gray'>gray</span> focus changes, <span class='red'>red</span> problems.</li>
        </ul>

        <h2>Smart Keyboard Auto Switch</h2>
        <p class='lead'>Optional, and off unless you turn it on in General.</p>
        {IMG:general}
        <p>Typed <code>akuo</code> when you meant <span class='he'>&#x5E9;&#x5DC;&#x5D5;&#x5DD;</span>? When you finish a word (with <kbd>Space</kbd>,
        punctuation like <code>, . ? !</code>, or <kbd>Return</kbd>), AutoLangSwitcher checks whether the same keys make a real word in another
        of your enabled input sources. If they do:</p>
        <ul>
        <li><b>Only suggest:</b> the fix appears next to the menu bar icon. Press <kbd>{FIXKEY}</kbd> within 10 seconds to retype the word.</li>
        <li><b>Fix automatically:</b> it retypes the word right away. A word ended with <kbd>Return</kbd> is fixed before the Return goes through,
        so a chat message is sent already fixed. (When only suggesting, words ended with Return aren't checked: the message would already be sent.)</li>
        </ul>
        <p>Either way it also switches the input source.</p>
        <p><b>The fix key</b> (<kbd>{FIXKEY}</kbd>, you can choose another in General) does three things:</p>
        <ul>
        <li>Fixes a suggested word.</li>
        <li>Right after a fix you didn't want, puts the word back. That word then goes on the <b>Never change these words</b> list.</li>
        <li>Right after a word that looks right but isn't, like <code>tbh</code> when you meant <span class='he'>&#x5D0;&#x5E0;&#x5D9;</span>, changes it to
        your other keyboard language. Press it again to put it back.</li>
        </ul>
        <p><b>Good to know</b></p>
        <ul>
        <li>Short words that are real in both languages are decided by the words around them: <code>do akuo</code> becomes
        <span class='he'>&#x5D2;&#x5DD; &#x5E9;&#x5DC;&#x5D5;&#x5DD;</span>, while <code>I went to akuo</code> keeps its English <code>to</code>.</li>
        <li>Words that aren't in any dictionary, like names, are left alone. It uses the spelling dictionaries built into macOS, plus letter
        statistics for English, Hebrew, Russian, Ukrainian, Arabic, Greek, French, German, Italian and Spanish.</li>
        <li>It stays off in code editors and terminals (VS Code, Xcode, JetBrains IDEs, Terminal, iTerm and others), where keys can be commands
        and auto-complete changes the text. Change that on the <b>Smart switch off</b> tab, or with the buttons on the <b>Apps</b> tab.</li>
        <li>macOS asks you to allow AutoLangSwitcher under System Settings &gt; Privacy &amp; Security &gt; <b>Accessibility</b>. It is needed to
        read the keys and to retype the word. After you rebuild the app, macOS may ask again: remove the old entry and allow the new one.</li>
        </ul>
        {IMG:off}
        <p class='note'><b>Privacy:</b> the letters of the current word stay in memory only until you finish it. Nothing is logged or sent, and only
        the never-change list is saved. Password fields are never read (macOS hides them from all apps). While the feature is off,
        AutoLangSwitcher doesn't read your typing at all.</p>

        <h2>How it works</h2>
        <ol>
        <li>macOS tells AutoLangSwitcher each time a different app comes to the front.</li>
        <li>For that app, it looks up the saved input source and switches to it.</li>
        <li>About half a second later it checks that the switch worked, and asks again if it didn't.</li>
        <li>macOS also tells it whenever you change the input source, so your choice is remembered right away for the app in front.</li>
        </ol>

        <h2>Tips</h2>
        <ul>
        <li>Turn off macOS's own <b>Automatically switch to a document's input source</b> so the two don't fight. It's in
        System Settings &gt; Keyboard &gt; Text Input &gt; Input Sources &gt; Edit.</li>
        <li>An app keeps getting the wrong input source? Open the Log tab right after it happens, click <b>Copy all</b>, and paste it into
        <a href='https://github.com/AniDavid/AutoLangSwitcher/issues'>an issue on GitHub</a>.</li>
        <li><b>Pause</b> it from the menu bar to switch input sources by hand for a while.</li>
        <li>While Caps Lock is on, the menu bar keyboard turns orange and shows a capital <b>A</b>.</li>
        </ul>
        </body></html>
        """
}

// MARK: - Main
// An explicit main actor entry point (built with -parse-as-library): newer Swift compilers
// reject creating the @MainActor AppDelegate from plain top-level code.

@main
struct Main {
    @MainActor static func main() {
        let app = NSApplication.shared
        if CommandLine.arguments.contains("--selftest") { exit(SmartSwitch.selfTest()) }
        let args = CommandLine.arguments
        if args.count == 3, args[1] == "--screenshots" {
            AppDelegate().takeScreenshots(to: args[2])
            exit(0)
        }
        let delegate = AppDelegate()
        app.delegate = delegate                // NSApplication holds its delegate weakly
        app.setActivationPolicy(.accessory)    // menu bar only, no Dock icon
        withExtendedLifetime(delegate) { app.run() }
    }
}
