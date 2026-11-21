// Language rules, set per app on the Apps tab (same as LanguageRules.cs on Windows):
//   Lock to X:  the app gets X every time it comes to the front. Switching inside it is allowed but never remembered.
//   Start in X: the app gets X when it is opened (after being closed); while it runs, switches are remembered as usual.
// Saved in the app's preferences under "languageRules": bundle id -> "lock:<input source id>" or "start:<input source id>".

import Cocoa

extension AppDelegate: NSMenuDelegate {
    static let rulesKey = "languageRules"

    func loadRules() {
        rules = UserDefaults.standard.dictionary(forKey: Self.rulesKey) as? [String: String] ?? [:]
    }

    static func parseRule(_ text: String?) -> (lock: Bool, source: String)? {
        guard let text = text, let colon = text.firstIndex(of: ":") else { return nil }
        let kind = text[..<colon], source = String(text[text.index(after: colon)...])
        guard kind == "lock" || kind == "start", !source.isEmpty else { return nil }
        return (kind == "lock", source)
    }

    // The input source an app should get when it comes to the front; nil for none.
    static func wanted(rule: String?, launched: Bool, remembered: String?) -> String? {
        if let r = parseRule(rule), r.lock || launched { return r.source }
        return remembered
    }

    func isLocked(_ app: String) -> Bool { Self.parseRule(rules[app])?.lock ?? false }

    func ruleText(_ app: String) -> String {
        guard let r = Self.parseRule(rules[app]) else { return "" }
        return (r.lock ? "Locked: " : "Starts in: ") + InputSources.name(r.source)
    }

    func setRule(_ apps: [String], _ rule: String?) {
        for app in apps {
            rules[app] = rule
            log("language rule \(app): \(rule == nil ? "none" : ruleText(app))")
        }
        UserDefaults.standard.set(rules, forKey: Self.rulesKey)
    }

    // An app was opened: its start language applies the next time it comes to the front (or now, if it already has).
    @objc func appLaunched(_ note: Notification) {
        guard let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
              let id = app.bundleIdentifier ?? app.executableURL?.lastPathComponent else { return }
        launched.insert(id)
        if app.isActive { handleActivation(app) }
    }

    // Apps tab: the menu for the selected apps. A right-click on an app that isn't selected selects just that one.
    func menuNeedsUpdate(_ menu: NSMenu) {
        guard let table = appsTable, menu === table.menu else { return }
        menu.removeAllItems()
        let clicked = table.clickedRow
        if clicked >= 0 && !table.selectedRowIndexes.contains(clicked) {
            table.selectRowIndexes(IndexSet(integer: clicked), byExtendingSelection: false)
        }
        let apps = table.selectedRowIndexes.filter { $0 < appRows.count }.map { appRows[$0].app }
        if apps.isEmpty { return }
        for lock in [true, false] {
            let item = NSMenuItem(title: lock ? "Lock to (always this input source)" : "Start in (this input source when opened)",
                                  action: nil, keyEquivalent: "")
            let sub = NSMenu()
            for layout in SmartSwitch.keyboardLayouts() {
                let choice = NSMenuItem(title: InputSources.name(layout.id), action: #selector(ruleChosen(_:)), keyEquivalent: "")
                choice.target = self
                choice.representedObject = (lock ? "lock:" : "start:") + layout.id
                sub.addItem(choice)
            }
            item.submenu = sub
            menu.addItem(item)
        }
        let none = NSMenuItem(title: "No rule (remember the last input source)", action: #selector(ruleChosen(_:)), keyEquivalent: "")
        none.target = self
        menu.addItem(none)
        menu.addItem(.separator())
        let allOff = apps.allSatisfy { smart.isOff($0) }
        let toggle = NSMenuItem(title: allOff ? "Turn smart switch on here" : "Turn smart switch off here",
                                action: allOff ? #selector(smartOnForSelected) : #selector(smartOffForSelected), keyEquivalent: "")
        toggle.target = self
        menu.addItem(toggle)
        menu.addItem(.separator())
        let forget = NSMenuItem(title: apps.count == 1 ? "Forget this app" : "Forget these apps",
                                action: #selector(removeSelectedApps), keyEquivalent: "")
        forget.target = self
        menu.addItem(forget)
    }

    // Double-click an app: the same menu, at the pointer.
    @objc func appDoubleClicked() {
        guard let table = appsTable, let menu = table.menu, table.clickedRow >= 0, let event = NSApp.currentEvent else { return }
        NSMenu.popUpContextMenu(menu, with: event, for: table)
    }

    @objc func ruleChosen(_ item: NSMenuItem) {
        guard let table = appsTable else { return }
        setRule(table.selectedRowIndexes.filter { $0 < appRows.count }.map { appRows[$0].app }, item.representedObject as? String)
        fillApps()
    }

    static func ruleSelfTest() -> Int32 {
        var fails: Int32 = 0
        func check(_ ok: Bool, _ what: String) {
            print((ok ? "ok    " : "FAIL  ") + what)
            if !ok { fails += 1 }
        }
        let he = "com.apple.keylayout.Hebrew", en = "com.apple.keylayout.ABC"
        check(wanted(rule: "lock:" + he, launched: false, remembered: en) == he, "a locked app gets its input source even when another is remembered")
        check(wanted(rule: "start:" + he, launched: true, remembered: en) == he, "a 'start in' app gets its input source when opened")
        check(wanted(rule: "start:" + he, launched: false, remembered: en) == en, "a 'start in' app keeps the remembered input source while it runs")
        check(wanted(rule: nil, launched: true, remembered: en) == en && wanted(rule: nil, launched: false, remembered: nil) == nil,
              "no rule: the remembered input source, as before")
        check(parseRule("sometimes:" + he) == nil && parseRule("lock:") == nil, "a rule with an unknown word or no source is ignored")
        return fails
    }
}
