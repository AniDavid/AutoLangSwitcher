// Smart Keyboard Auto Switch for macOS: the same feature as Windows/SmartSwitch.cs. It notices a word
// typed with the wrong input source (e.g. "akuo" when Hebrew was meant) and suggests the fix or makes it.
// Off by default; while it is off no event tap is installed. Typed keys stay in memory until the word
// ends: never saved, never logged. Needs the Accessibility permission (to read keys and to retype).

import Cocoa
import Carbon

struct KeyStroke { var code: UInt16; var shift, option, caps: Bool }
struct TypedWord { var keys: [KeyStroke]; var source: String }   // source: input source id
struct KeyLayout { let id: String; let lang: String; let data: Data }

// Event tap callback (a C function): hands the event to the SmartSwitch on the main thread.
private func smartTapCallback(proxy: CGEventTapProxy, type: CGEventType, event: CGEvent,
                              info: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    guard let info = info else { return Unmanaged.passUnretained(event) }
    let me = Unmanaged<SmartSwitch>.fromOpaque(info).takeUnretainedValue()
    let swallow = MainActor.assumeIsolated { me.handle(type, event) }   // the tap runs on the main run loop
    return swallow ? nil : Unmanaged.passUnretained(event)
}

@MainActor
final class SmartSwitch {
    // There is no Pause/Break key on a Mac. Z is matched by key position (the Z of a US keyboard).
    static let fixKeyName = "Control+Option+Z"
    static let marker: Int64 = 0x414C53   // tags the key events we post ourselves
    static let lookBack = 3               // earlier words of the same run that a fix may also change
    enum Verdict { case no, ambiguous, clear }

    unowned let app: AppDelegate
    var on = false, auto = false
    var tap: CFMachPort?
    var askedForAccess = false
    var layouts: [KeyLayout] = []
    var word: [KeyStroke] = []
    var history: [TypedWord] = []   // the words before it, separated by single spaces ("a run")
    var keyCount = 0                // key presses so far; a change means the user typed on after a word ended

    // The fix for the last words, waiting for the fix key (suggest mode), or the undo of a fix just made.
    var fixText: String?
    var fixBack = "", fixSource = "", fixBackSource = ""
    var fixErase = 0, fixPid: pid_t = 0, fixIsUndo = false
    var fixUntil = Date.distantPast
    var keepWords = Set<String>()   // words whose fix the user undid: left alone until the app quits

    init(app: AppDelegate) { self.app = app }

    // MARK: Settings

    func load() {
        on = UserDefaults.standard.bool(forKey: "smartSwitch")
        auto = UserDefaults.standard.bool(forKey: "smartSwitchAuto")
        if on { start() }
    }

    func set(on newOn: Bool, auto newAuto: Bool) {
        auto = newAuto
        if newOn != on {
            on = newOn
            if on { start() } else { stop() }
        }
        UserDefaults.standard.set(on, forKey: "smartSwitch")
        UserDefaults.standard.set(auto, forKey: "smartSwitchAuto")
    }

    func start() {
        guard on, tap == nil else { return }
        // Asks macOS for the Accessibility permission once; then waits for the user to give it.
        let options = ["AXTrustedCheckOptionPrompt": !askedForAccess] as CFDictionary   // kAXTrustedCheckOptionPrompt
        let trusted = AXIsProcessTrustedWithOptions(options)
        if !trusted {
            if !askedForAccess {
                app.log("smart switch is waiting for the Accessibility permission (System Settings > Privacy & Security > Accessibility)")
            }
            askedForAccess = true
            Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                self?.start()
            }
            return
        }
        var mask: CGEventMask = 0
        for type in [CGEventType.keyDown, .leftMouseDown, .rightMouseDown] { mask |= CGEventMask(1) << CGEventMask(type.rawValue) }
        guard let t = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                        eventsOfInterest: mask, callback: smartTapCallback,
                                        userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
            app.log("smart switch FAILED to start (no event tap)")
            return
        }
        CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(nil, t, 0), .commonModes)
        tap = t
        refreshLayouts()
        app.log("smart switch on")
    }

    func stop() {
        guard let t = tap else { return }
        CGEvent.tapEnable(tap: t, enable: false)
        CFMachPortInvalidate(t)
        tap = nil
        endRun()
        setFix(nil)
        app.log("smart switch off")
    }

    // MARK: Typing

    // Returns true to swallow the event: only the fix key when it applies a fix.
    func handle(_ type: CGEventType, _ event: CGEvent) -> Bool {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let t = tap { CGEvent.tapEnable(tap: t, enable: true) }
            return false
        }
        if event.getIntegerValueField(.eventSourceUserData) == Self.marker { return false }
        if type != .keyDown {
            endRun()       // a click may have moved the cursor
            setFix(nil)
            return false
        }
        let code = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        let flags = event.flags
        if fixText != nil, code == UInt16(kVK_ANSI_Z), flags.contains(.maskControl), flags.contains(.maskAlternate),
           !flags.contains(.maskCommand) {
            Task { @MainActor in self.applyFix() }
            return true
        }
        keyCount += 1
        if fixText != nil { setFix(nil) }   // typing on: the suggestion no longer applies
        if app.paused || flags.contains(.maskCommand) || flags.contains(.maskControl) || IsSecureEventInputEnabled() {
            endRun()   // paused, a shortcut, or a password field: not part of a word
            return false
        }
        switch Int(code) {
        case kVK_Delete:
            if word.isEmpty { history.removeAll() } else { word.removeLast() }   // editing an earlier word: forget the run
            return false
        case kVK_Space:
            guard !word.isEmpty, let source = InputSources.currentID(),
                  let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier else {
                endRun()   // two spaces in a row
                return false
            }
            let w = TypedWord(keys: word, source: source)
            let before = history, count = keyCount
            Task { @MainActor in self.check(w, before, pid, count) }   // after the tap returns, so typing stays fast
            history.append(w)
            if history.count > Self.lookBack { history.removeFirst() }
            word.removeAll()
            return false
        default:
            break
        }
        let s = KeyStroke(code: code, shift: flags.contains(.maskShift), option: flags.contains(.maskAlternate),
                          caps: flags.contains(.maskAlphaShift))
        // A key belongs to the word if it is a letter in any enabled layout: in English the Hebrew letter
        // tav is the comma key, for example. Return, Tab, arrows, digits... end the run without a check.
        if layouts.contains(where: { Self.char(s, $0)?.isLetter == true }) { word.append(s) } else { endRun() }
        return false
    }

    func endRun() {
        word.removeAll()
        history.removeAll()
    }

    // `before`: the earlier words of the same run, oldest first.
    func check(_ current: TypedWord, _ before: [TypedWord], _ pid: pid_t, _ count: Int) {
        guard on, count == keyCount, NSWorkspace.shared.frontmostApplication?.processIdentifier == pid,
              !IsSecureEventInputEnabled() else { return }
        refreshLayouts()
        guard let layout = layouts.first(where: { $0.id == current.source }) else { return }
        // The end of the run, current word last. A word typed with another source, or one whose fix the
        // user undid, cuts it.
        var run: [[KeyStroke]] = [], typed: [String] = []
        for w in before + [current] {
            if w.source == layout.id, let t = Self.text(w.keys, layout), !keepWords.contains(t) {
                run.append(w.keys)
                typed.append(t)
            } else {
                run.removeAll()
                typed.removeAll()
            }
        }
        if run.isEmpty { return }
        for other in layouts where Self.twoLetter(other.lang) != Self.twoLetter(layout.lang) {
            let meant = run.map { Self.text($0, other) }
            guard let first = Self.fixStart(typed, layout.lang, meant, other.lang) else { continue }
            let typedRun = typed[first...].joined(separator: " ")
            let meantRun = meant[first...].compactMap { $0 }.joined(separator: " ")
            let n = typed.count - first
            let words = n == 1 ? "word" : "\(n) words"
            let name = InputSources.name(other.id)
            app.log("smart switch \(app.currentApp ?? "?"): last \(words) looked like \(name)" +
                    (auto ? ", fixed" : ", suggested (\(Self.fixKeyName))"))
            fixBack = typedRun + " "
            fixErase = typedRun.count + 1   // the words and the space after them
            fixPid = pid
            fixSource = other.id
            fixBackSource = layout.id
            fixIsUndo = false
            if auto {
                setFix(meantRun + " ")
                applyFix()
            } else {
                setFix(meantRun + " ", hint: "Fix \u{2192} \(name): \u{2303}\u{2325}Z")
            }
            return
        }
    }

    // A pending fix for 10 seconds, with an optional hint next to the menu bar icon.
    func setFix(_ text: String?, hint: String? = nil) {
        fixText = text
        if text != nil { fixUntil = Date().addingTimeInterval(10) }
        if let button = app.statusItem?.button {
            button.title = hint.map { " " + $0 } ?? ""
            button.imagePosition = .imageLeft
        }
        guard hint != nil else { return }
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            if let self = self, Date() >= self.fixUntil { self.setFix(nil) }
        }
    }

    func applyFix() {
        guard let text = fixText else { return }
        setFix(nil)
        guard Date() < fixUntil, NSWorkspace.shared.frontmostApplication?.processIdentifier == fixPid else { return }
        InputSources.select(fixSource)   // the app then remembers it like a switch made by hand
        Self.retype(erase: fixErase, text: text)
        history.removeAll()   // the screen now differs from what was typed
        if fixIsUndo {
            keepWords.formUnion(text.split(separator: " ").map(String.init))
            app.log("smart switch \(app.currentApp ?? "?"): fix undone, those words are left alone from now on")
            return
        }
        // The fix key again, before typing anything else, puts the words back.
        fixIsUndo = true
        fixErase = text.count
        swap(&fixSource, &fixBackSource)
        let back = fixBack
        fixBack = text
        setFix(back)
    }

    // Deletes `erase` characters, then types `text`, as key events tagged with `marker`.
    static func retype(erase: Int, text: String) {
        let src = CGEventSource(stateID: .privateState)
        src?.userData = marker
        func post(_ code: Int, _ s: String?) {
            for down in [true, false] {
                guard let e = CGEvent(keyboardEventSource: src, virtualKey: CGKeyCode(code), keyDown: down) else { continue }
                e.flags = []   // the user may still be holding Control+Option
                if let s = s {
                    let u = Array(s.utf16)
                    e.keyboardSetUnicodeString(stringLength: u.count, unicodeString: u)
                }
                e.post(tap: .cghidEventTap)
            }
        }
        for _ in 0..<erase { post(kVK_Delete, nil) }
        for ch in text { post(ch == " " ? kVK_Space : 0, String(ch)) }
    }

    // MARK: Keyboard layouts

    // The enabled keyboard layouts (input methods without a key layout are skipped).
    func refreshLayouts() {
        layouts = Self.keyboardLayouts()
    }

    // all: also the installed layouts that aren't enabled (for the self-test).
    static func keyboardLayouts(all: Bool = false) -> [KeyLayout] {
        let filter: [String: Any] = [kTISPropertyInputSourceCategory as String: kTISCategoryKeyboardInputSource as String,
                                     kTISPropertyInputSourceIsSelectCapable as String: true]
        let list = TISCreateInputSourceList(filter as CFDictionary, all)?.takeRetainedValue() as? [TISInputSource] ?? []
        return list.compactMap { src in
            guard let id = InputSources.property(src, kTISPropertyInputSourceID),
                  let p = TISGetInputSourceProperty(src, kTISPropertyUnicodeKeyLayoutData) else { return nil }
            let data = Unmanaged<CFData>.fromOpaque(p).takeUnretainedValue() as Data
            var lang = ""
            if let lp = TISGetInputSourceProperty(src, kTISPropertyInputSourceLanguages),
               let langs = Unmanaged<CFArray>.fromOpaque(lp).takeUnretainedValue() as? [String],
               let primary = langs.first {
                lang = primary
            }
            return KeyLayout(id: id, lang: lang, data: data)
        }
    }

    // The character a key gives in a layout, or nil.
    static func char(_ s: KeyStroke, _ layout: KeyLayout) -> Character? {
        var mods: UInt32 = 0
        if s.shift { mods |= UInt32(shiftKey >> 8) }
        if s.option { mods |= UInt32(optionKey >> 8) }
        if s.caps { mods |= UInt32(alphaLock >> 8) }
        var dead: UInt32 = 0
        var len = 0
        var chars = [UniChar](repeating: 0, count: 4)
        let status = layout.data.withUnsafeBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return OSStatus(paramErr) }
            return UCKeyTranslate(base.assumingMemoryBound(to: UCKeyboardLayout.self), s.code, UInt16(kUCKeyActionDown),
                                  mods, UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysMask),
                                  &dead, 4, &len, &chars)
        }
        guard status == noErr, len > 0 else { return nil }
        let str = String(utf16CodeUnits: chars, count: len)
        return str.count == 1 ? str.first : nil
    }

    static func text(_ keys: [KeyStroke], _ layout: KeyLayout) -> String? {
        var out = ""
        for k in keys {
            guard let c = char(k, layout) else { return nil }
            out.append(c)
        }
        return out
    }

    static func twoLetter(_ lang: String) -> String {
        String(lang.prefix { $0 != "-" && $0 != "_" })
    }

    // MARK: Deciding (the same rules as Windows/SmartSwitch.cs)

    // A run of words, current word last, as typed and as the same keys give in another layout.
    // Returns the index of the first word to change, or nil to leave the run alone.
    //  - A word that is gibberish here but a real word there ("akuo" / shalom) is clear.
    //  - A real word in both languages ("do" / gam) is ambiguous: it is changed only together with a
    //    clear word next to it, and not when a word that is real only here comes before it
    //    ("I went to akuo": "to" stays English, like "went").
    static func fixStart(_ typed: [String], _ lang: String, _ meant: [String?], _ otherLang: String) -> Int? {
        let last = typed.count - 1
        let verdict = judge(typed[last], lang, meant[last], otherLang)
        if verdict == .no { return nil }
        var first = last, firstClear = verdict == .clear ? last : -1
        var stoppedByWord = false
        for i in stride(from: last - 1, through: 0, by: -1) {
            let v = judge(typed[i], lang, meant[i], otherLang)
            if v == .no {
                stoppedByWord = true
                break
            }
            first = i
            if v == .clear { firstClear = i }
        }
        if firstClear < 0 { return nil }   // only ambiguous words: no evidence
        return stoppedByWord ? firstClear : first
    }

    static func isWrongLanguage(_ typed: String, _ lang: String, _ meant: String?, _ otherLang: String) -> Bool {
        judge(typed, lang, meant, otherLang) == .clear
    }

    // typed: the word as it came out; meant: the same keys in another layout.
    static func judge(_ typed: String, _ lang: String, _ meant: String?, _ otherLang: String) -> Verdict {
        guard let meant = meant, meant.allSatisfy({ $0.isLetter }) else { return .no }
        let bare = typed.trimmingCharacters(in: CharacterSet(charactersIn: ",.;'\"/[]`"))
        if isWord(otherLang, meant) != true { return .no }
        guard let here = isWord(lang, bare) else { return .no }
        if here { return .ambiguous }
        // Second opinion: a typed word that still looks normal for its language is probably a name or
        // slang missing from the dictionary, so leave it alone. (Not for 1-2 letters.)
        let a = twoLetter(lang), b = twoLetter(otherLang)
        let looksNormal = bare.count >= 3 && LetterStats.has(a) && LetterStats.has(b) &&
            LetterStats.margin(b, meant) >= LetterStats.margin(a, bare)
        return looksNormal ? .no : .clear
    }

    // Acronyms and chat abbreviations people type in lower case, and "i". Dictionaries don't know most of
    // them, and letter statistics can't tell them from gibberish ("cmd" looks no more English than "akt").
    static let abbreviations: Set<String> = words("""
        i ok js ts ai ui ux vs tv pc os ip id pm am db hr pr qa eu uk
        api sdk cli gui url uri http https html css json xml yaml sql cmd exe dll msi pdf docx xlsx pptx csv txt
        zip usb ssd hdd cpu gpu ram rom ios wifi vpn dns tcp udp ssh ftp smtp git npm nuget sudo bash apt dev prod
        ceo cto cfo faq diy asap btw fyi imo imho idk lol omg thx pls plz brb afk tbh etc aka
        """)

    static func words(_ list: String) -> Set<String> {
        Set(list.split(whereSeparator: { $0.isWhitespace }).map(String.init))
    }

    // Real 1-2 letter words, for languages whose dictionary accepts almost any one or two letters.
    // Longer words go to the dictionary.
    static let shortWords: [String: Set<String>] = [
        "en": words("a i ad ah am an as at be by do eh ex go ha he hi hm if in is it me mr ms my no of oh ok on or ox pi so to uh um up us we ya ye yo"),
        "he": words("\u{05E9}\u{05DC} \u{05D0}\u{05EA} \u{05D6}\u{05D4} \u{05D6}\u{05D5} \u{05DC}\u{05D0} \u{05DB}\u{05DF} \u{05DE}\u{05D4} \u{05DE}\u{05D9} \u{05E2}\u{05DC} \u{05D0}\u{05DD} \u{05D2}\u{05DD} \u{05E8}\u{05E7} \u{05E2}\u{05DD} \u{05D9}\u{05E9} \u{05D0}\u{05D6} \u{05D3}\u{05D9} \u{05D4}\u{05DD} \u{05D4}\u{05DF} \u{05DC}\u{05D9} \u{05DC}\u{05D5} \u{05DC}\u{05DA} \u{05DC}\u{05D4} \u{05D1}\u{05D5} \u{05D1}\u{05D4} \u{05D1}\u{05D9} \u{05E4}\u{05D4} \u{05E9}\u{05DD} \u{05DB}\u{05DC} \u{05D0}\u{05DA} \u{05DB}\u{05D9} \u{05E0}\u{05D0} \u{05D0}\u{05DC} \u{05D0}\u{05E3} \u{05E2}\u{05D3} \u{05D0}\u{05D9} \u{05D0}\u{05D5} \u{05D1}\u{05DF} \u{05D1}\u{05EA} \u{05D0}\u{05D1} \u{05D0}\u{05D7} \u{05D9}\u{05D3} \u{05E4}\u{05D9} \u{05D7}\u{05D9} \u{05E7}\u{05D5} \u{05E6}\u{05D5} \u{05D2}\u{05DF} \u{05D3}\u{05D2} \u{05D7}\u{05D2} \u{05E2}\u{05E5} \u{05D2}\u{05D1} \u{05D7}\u{05DD} \u{05E7}\u{05E8} \u{05E8}\u{05E2} \u{05E2}\u{05D6} \u{05D0}\u{05E9} \u{05D9}\u{05DD} \u{05D7}\u{05DF} \u{05D2}\u{05DC} \u{05E8}\u{05D1} \u{05D4}\u{05E8} \u{05DB}\u{05E3} \u{05D3}\u{05E3} \u{05E1}\u{05DC} \u{05D2}\u{05E8} \u{05DE}\u{05E1} \u{05EA}\u{05DF} \u{05E7}\u{05D7} \u{05E9}\u{05D1} \u{05E8}\u{05D3} \u{05E6}\u{05D0} \u{05D1}\u{05D0} \u{05D6}\u{05D6} \u{05E0}\u{05E2} \u{05DB}\u{05DA} \u{05D0}\u{05D4} \u{05D3}\u{05DD} \u{05E9}\u{05DF} \u{05DC}\u{05D1} \u{05E2}\u{05D8} \u{05E6}\u{05D1} \u{05EA}\u{05D4} \u{05E1}\u{05D1}"),
    ]

    // True or false, or nil when there is neither a macOS dictionary nor letter statistics.
    static func isWord(_ lang: String, _ w: String) -> Bool? {
        if w.isEmpty || !w.allSatisfy({ $0.isLetter }) { return false }
        let lower = w.lowercased()
        if abbreviations.contains(lower) { return true }
        let two = twoLetter(lang)
        if let short = shortWords[two], w.count <= 2 { return short.contains(lower) }
        if let known = Spelling.check(lang, w) {
            // Dictionaries list acronyms and names only with their capitals: "API", "London".
            // Not for 1-2 letters: almost every pair of capitals is some code.
            return known || (w.count >= 3 && Spelling.knownWithOtherCase(lang, w))
        }
        if LetterStats.has(two) { return LetterStats.margin(two, w) <= 0 }
        return nil
    }

    // MARK: Self-test (AutoLangSwitcher.app/Contents/MacOS/AutoLangSwitcher --selftest)

    static func selfTest() -> Int32 {
        var fails: Int32 = 0
        func check(_ ok: Bool, _ what: String) {
            print((ok ? "ok    " : "FAIL  ") + what)
            if !ok { fails += 1 }
        }
        let shalom = "\u{05E9}\u{05DC}\u{05D5}\u{05DD}"
        print("spelling dictionaries: " + NSSpellChecker.shared.availableLanguages.joined(separator: " "))
        if Spelling.check("en", "hello") != nil {
            check(Spelling.check("en", "hello") == true, "English dictionary knows 'hello'")
            check(Spelling.check("en", "akuo") == false, "English dictionary rejects 'akuo'")
        }
        if let he = Spelling.check("he", shalom) { check(he, "Hebrew dictionary knows shalom") }
        for lang in LetterStats.tables.keys.sorted() {
            let samples = LetterStats.tables[lang]!.samples
            let good = samples.filter { LetterStats.margin(lang, $0) <= 0 }.count
            check(good >= samples.count * 9 / 10, "\(lang) letter statistics accept \(good)/\(samples.count) common words")
        }

        let all = keyboardLayouts(all: true)
        print("keyboard layouts: \(all.count) installed")
        guard let en = all.first(where: { $0.id == "com.apple.keylayout.US" }) ?? all.first(where: { twoLetter($0.lang) == "en" }),
              let he = all.first(where: { $0.id == "com.apple.keylayout.Hebrew" }) ?? all.first(where: { twoLetter($0.lang) == "he" }) else {
            print("skip  English/Hebrew typing tests: both keyboard layouts must be installed")
            return fails
        }
        func keys(_ text: String, _ l: KeyLayout) -> [KeyStroke]? {
            var out: [KeyStroke] = []
            for ch in text {
                var found: KeyStroke?
                search: for shift in [false, true] {
                    for code in UInt16(0)..<UInt16(128) {
                        let k = KeyStroke(code: code, shift: shift, option: false, caps: false)
                        if char(k, l) == ch { found = k; break search }
                    }
                }
                guard let k = found else { return nil }
                out.append(k)
            }
            return out
        }
        func typedOn(_ text: String, _ from: KeyLayout, _ to: KeyLayout) -> String? {
            keys(text, from).flatMap { self.text($0, to) }
        }
        check(typedOn("akuo", en, he) == shalom, "keys a-k-u-o on the Hebrew layout give shalom")
        check(isWrongLanguage("akuo", en.lang, shalom, he.lang), "'akuo' is detected as Hebrew typed on English")
        check(isWrongLanguage(typedOn("i", en, he) ?? "", he.lang, "i", en.lang), "'i' typed on the Hebrew keyboard is caught")
        for w in ["hello", "api", "london", "json", "cmd"] {
            check(!isWrongLanguage(w, en.lang, typedOn(w, en, he), he.lang), "'\(w)' is left alone")
        }
        func runCheck(_ text: String, _ expected: Int?, _ what: String) {
            let typed = text.split(separator: " ").map(String.init)
            check(fixStart(typed, en.lang, typed.map { typedOn($0, en, he) }, he.lang) == expected, what)
        }
        runCheck("do akuo", 0, "'do akuo': 'do' (gam) is changed together with shalom")
        runCheck("went to akuo", 2, "'went to akuo': 'to' stays English like 'went'")
        runCheck("akuo do", 0, "'akuo do': ambiguous 'do' after shalom is changed with it")
        runCheck("to do so", nil, "'to do so': all ambiguous, left alone")
        runCheck("hello world", nil, "'hello world' is left alone")
        for (lang, meantOn, typedWith) in [("he", he, en), ("en", en, he)] {
            var found = 0, total = 0
            for w in LetterStats.tables[lang]!.samples {
                guard let t = typedOn(w, meantOn, typedWith) else { continue }
                total += 1
                if isWrongLanguage(t, typedWith.lang, w, meantOn.lang) { found += 1 }
            }
            check(found >= total * 7 / 10, "\(lang) words typed on the wrong layout detected: \(found)/\(total)")
        }
        return fails
    }
}

// macOS's spelling dictionaries (the ones TextEdit uses), one per language.
@MainActor
enum Spelling {
    static var languages: [String: String?] = [:]

    // The dictionary name for a language code ("he", "en-US"...), or nil when macOS has none.
    static func language(_ lang: String) -> String? {
        if let known = languages[lang] { return known }
        let available = NSSpellChecker.shared.availableLanguages
        let two = SmartSwitch.twoLetter(lang)
        let found = available.first { $0 == lang } ?? available.first { $0 == two } ??
            available.first { SmartSwitch.twoLetter($0) == two }
        languages[lang] = found
        return found
    }

    // True or false, or nil when there is no dictionary for the language.
    static func check(_ lang: String, _ word: String) -> Bool? {
        guard let l = language(lang) else { return nil }
        let r = NSSpellChecker.shared.checkSpelling(of: word, startingAt: 0, language: l, wrap: false,
                                                    inSpellDocumentWithTag: 0, wordCount: nil)
        return r.location == NSNotFound
    }

    // Whether the dictionary knows the word with other capitals ("api" -> "API").
    static func knownWithOtherCase(_ lang: String, _ word: String) -> Bool {
        guard let l = language(lang) else { return false }
        let guesses = NSSpellChecker.shared.guesses(forWordRange: NSRange(location: 0, length: (word as NSString).length),
                                                    in: word, language: l, inSpellDocumentWithTag: 0) ?? []
        return guesses.prefix(10).contains { $0.lowercased() == word.lowercased() }
    }
}

// Letter-pair statistics: how much a word looks like a language, for languages macOS has no dictionary
// for (and as a second opinion when it has). The tables are in LetterStatsData.swift.
enum LetterStats {
    static let foreign = 36   // cost of a character outside the language's alphabet

    struct Table { let alphabet: [Character]; let costs: [Int]; let threshold: Double; let samples: [String] }

    static let tables: [String: Table] = {
        var t: [String: Table] = [:]
        var i = 0
        while i + 4 < data.count {
            t[data[i]] = Table(alphabet: Array(data[i + 1]),
                               costs: data[i + 4].map { Int(String($0), radix: 36) ?? foreign },
                               threshold: Double(data[i + 2]) ?? 0,
                               samples: data[i + 3].split(separator: ",").map(String.init))
            i += 5
        }
        return t
    }()

    static func has(_ lang: String) -> Bool { tables[lang] != nil }

    // Average cost per letter pair (word start and end included) minus the language's threshold.
    // Negative: looks like a word of this language. Two-letter language code, e.g. "he".
    static func margin(_ lang: String, _ word: String) -> Double {
        guard let t = tables[lang] else { return 0 }
        let size = t.alphabet.count + 1
        let letters = Array(word.lowercased())
        var prev = 0, sum = 0
        for ch in letters {
            guard let index = t.alphabet.firstIndex(of: ch) else {
                sum += foreign
                prev = 0
                continue
            }
            sum += t.costs[prev * size + index + 1]
            prev = index + 1
        }
        sum += t.costs[prev * size]
        return Double(sum) / Double(letters.count + 1) - t.threshold
    }
}
