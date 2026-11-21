// Language rules, set per app on the Apps tab (same as LanguageRules.cs on Windows); an app can have either or both:
//   Lock to X:  the app gets X every time it comes to the front. Switching inside it is allowed but never remembered.
//   Start in Y: the app gets Y when it is opened (after being quit). Without a lock, switches while it runs are
//               remembered as usual; with one, it opens in Y and gets X each time you come back to it.
// Saved in the app's preferences: "languageLocks" and "languageStarts", bundle id -> input source id.

import Cocoa

extension AppDelegate: NSMenuDelegate {
    static let locksKey = "languageLocks", startsKey = "languageStarts"

    func loadRules() {
        locks = UserDefaults.standard.dictionary(forKey: Self.locksKey) as? [String: String] ?? [:]
        starts = UserDefaults.standard.dictionary(forKey: Self.startsKey) as? [String: String] ?? [:]
    }

    // The input source an app should get when it comes to the front; nil for none.
    static func wanted(lock: String?, start: String?, launched: Bool, remembered: String?) -> String? {
        if launched, let start = start { return start }
        return lock ?? remembered
    }

    func isLocked(_ app: String) -> Bool { locks[app] != nil }

    func ruleText(_ app: String) -> String {
        let start = starts[app].map { InputSources.name($0) }, locked = locks[app].map { InputSources.name($0) }
        if let s = start, let l = locked { return "Starts in \(s), then locked: \(l)" }
        if let s = start { return "Starts in: " + s }
        return locked.map { "Locked: " + $0 } ?? ""
    }

    // Sets the lock (lock true) or the start input source of these apps; nil removes it. Saves.
    func setRule(_ apps: [String], lock: Bool, _ source: String?) {
        for app in apps {
            if lock { locks[app] = source } else { starts[app] = source }
            log("language rule \(app): \(ruleText(app).isEmpty ? "none" : ruleText(app))")
        }
        UserDefaults.standard.set(locks, forKey: Self.locksKey)
        UserDefaults.standard.set(starts, forKey: Self.startsKey)
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
        let apps = selectedApps()
        if apps.isEmpty { return }
        // Lock to > and Start in >, each with the enabled input sources and None; ticks show what the apps have now.
        for lock in [true, false] {
            let rules = lock ? locks : starts
            let values = Set(apps.map { rules[$0] ?? "" })   // "" for none
            let now = values.count == 1 ? values.first : nil
            let item = NSMenuItem(title: lock ? "Lock to (every time it comes to the front)" : "Start in (when it opens)",
                                  action: nil, keyEquivalent: "")
            let sub = NSMenu()
            for layout in SmartSwitch.keyboardLayouts() {
                let choice = NSMenuItem(title: InputSources.name(layout.id), action: #selector(ruleChosen(_:)), keyEquivalent: "")
                choice.target = self
                choice.representedObject = (lock ? "lock:" : "start:") + layout.id
                choice.state = now == layout.id ? .on : .off
                sub.addItem(choice)
            }
            sub.addItem(.separator())
            let none = NSMenuItem(title: lock ? "No lock" : "No start input source", action: #selector(ruleChosen(_:)), keyEquivalent: "")
            none.target = self
            none.representedObject = lock ? "lock:" : "start:"
            none.state = now == "" ? .on : .off
            sub.addItem(none)
            item.submenu = sub
            menu.addItem(item)
        }
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

    // The Start in / Locked to popup in the Apps table: None, then the enabled input sources. A cell with a rule gets a
    // pale tint (blue for Start in, orange for Locked to) and "None" is grey; the colour only repeats what the text says.
    func rulePopup(app: String, lock: Bool, row: Int) -> NSView {
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        popup.controlSize = .small
        popup.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        popup.isBordered = false
        popup.addItem(withTitle: "None")
        popup.lastItem?.attributedTitle = NSAttributedString(string: "None", attributes: [
            .foregroundColor: NSColor.secondaryLabelColor, .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)])
        let current = lock ? locks[app] : starts[app]
        var ids = SmartSwitch.keyboardLayouts().map { $0.id }
        if let c = current, !ids.contains(c) { ids.append(c) }   // an input source that isn't enabled any more
        for id in ids {
            popup.addItem(withTitle: InputSources.name(id))
            popup.lastItem?.representedObject = id
            if id == current { popup.select(popup.lastItem) }
        }
        popup.tag = row
        popup.identifier = NSUserInterfaceItemIdentifier(lock ? "lock" : "start")
        popup.target = self
        popup.action = #selector(rulePopupChanged(_:))
        let cell = NSView()
        cell.wantsLayer = true
        cell.layer?.cornerRadius = 4
        popup.translatesAutoresizingMaskIntoConstraints = false
        cell.addSubview(popup)
        NSLayoutConstraint.activate([popup.leadingAnchor.constraint(equalTo: cell.leadingAnchor),
                                     popup.trailingAnchor.constraint(equalTo: cell.trailingAnchor),
                                     popup.centerYAnchor.constraint(equalTo: cell.centerYAnchor)])
        tintRuleCell(popup)
        return cell
    }

    func tintRuleCell(_ popup: NSPopUpButton) {
        let set = popup.indexOfSelectedItem > 0
        let color: NSColor = popup.identifier?.rawValue == "lock" ? .systemOrange : .systemBlue
        popup.superview?.layer?.backgroundColor = set ? color.withAlphaComponent(0.15).cgColor : nil
    }

    @objc func rulePopupChanged(_ popup: NSPopUpButton) {
        guard popup.tag < appRows.count else { return }
        setRule([appRows[popup.tag].app], lock: popup.identifier?.rawValue == "lock", popup.selectedItem?.representedObject as? String)
        tintRuleCell(popup)
    }

    @objc func smartBoxClicked(_ box: NSButton) {
        guard box.tag < appRows.count else { return }
        smart.setAppsOff([appRows[box.tag].app], box.state == .off)
    }

    func selectedApps() -> [String] {
        guard let table = appsTable else { return [] }
        return table.selectedRowIndexes.filter { $0 < appRows.count }.map { appRows[$0].app }
    }

    // Double-click an app: the same menu, at the pointer.
    @objc func appDoubleClicked() {
        guard let table = appsTable, let menu = table.menu, table.clickedRow >= 0, let event = NSApp.currentEvent else { return }
        NSMenu.popUpContextMenu(menu, with: event, for: table)
    }

    // representedObject: "lock:<source>" or "start:<source>"; an empty source removes that rule.
    @objc func ruleChosen(_ item: NSMenuItem) {
        guard let text = item.representedObject as? String, let colon = text.firstIndex(of: ":") else { return }
        let source = String(text[text.index(after: colon)...])
        setRule(selectedApps(), lock: text.hasPrefix("lock:"), source.isEmpty ? nil : source)
        fillApps()
    }

    static func ruleSelfTest() -> Int32 {
        var fails: Int32 = 0
        func check(_ ok: Bool, _ what: String) {
            print((ok ? "ok    " : "FAIL  ") + what)
            if !ok { fails += 1 }
        }
        let he = "com.apple.keylayout.Hebrew", en = "com.apple.keylayout.ABC"
        check(wanted(lock: he, start: nil, launched: false, remembered: en) == he, "a locked app gets its input source even when another is remembered")
        check(wanted(lock: nil, start: he, launched: true, remembered: en) == he, "a 'start in' app gets its input source when opened")
        check(wanted(lock: nil, start: he, launched: false, remembered: en) == en, "a 'start in' app keeps the remembered input source while it runs")
        check(wanted(lock: en, start: he, launched: true, remembered: nil) == he, "with both rules, an app opens in its start input source")
        check(wanted(lock: en, start: he, launched: false, remembered: he) == en, "with both rules, back in front it gets the locked one")
        check(wanted(lock: nil, start: nil, launched: true, remembered: en) == en && wanted(lock: nil, start: nil, launched: false, remembered: nil) == nil,
              "no rule: the remembered input source, as before")
        return fails
    }
}
