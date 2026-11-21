# AutoLangSwitcher

**Windows: [Download the installer](https://github.com/AniDavid/AutoLangSwitcher/releases/latest/download/AutoLangSwitcher.msi)** or run `winget install AniDavid.AutoLangSwitcher` · macOS: build from source (see below)

## Why

If you write in more than one language, you probably use a different keyboard language in different programs: Hebrew in WhatsApp or Word, English in your code editor or terminal. Windows and macOS both use one keyboard language for all programs by default. Every time you switch programs you have to check the language and switch it, and you often only notice after typing a line of gibberish.

AutoLangSwitcher fixes this. It runs quietly in the background and remembers the last keyboard language you used in each program. When you switch back to that program, it switches the keyboard back for you.

By default it does not read what you type; it only restores each program's language. You can also turn on **Smart Keyboard Auto Switch**, which fixes words typed in the wrong language (see below).

## בעברית

<div dir="rtl">

כותבים גם בעברית וגם באנגלית? AutoLangSwitcher זוכרת את שפת המקלדת האחרונה שבחרתם בכל תוכנה, ומחזירה אותה אוטומטית כשאתם חוזרים לאותה תוכנה. למשל, עברית בוואטסאפ ובוורד, ואנגלית בעורך הקוד ובטרמינל. בלי להקליד שורה של ג'יבריש ולגלות רק אחר כך שהשפה לא נכונה.

אין שום הגדרות. פשוט בוחרים שפה בתוכנה, והיא נזכרת. רוצים שתוכנה תמיד תהיה באנגלית, או שוואטסאפ תמיד ייפתח בעברית? בלשונית Apps אפשר לנעול לתוכנה שפה (**Lock to**) או לקבוע באיזו שפה היא תיפתח (**Start in**). כברירת מחדל התוכנה לא קוראת את מה שאתם מקלידים.

אפשר גם להפעיל את **Smart Keyboard Auto Switch** (כבוי כברירת מחדל): הקלדתם "akuo" במקום "שלום"? התוכנה תציע לתקן את המילה, או תתקן אותה לבד, ותחליף את שפת המקלדת. כדי לעשות את זה היא קוראת את ההקלדה, אבל לא שומרת ולא שולחת שום דבר.

**להורדה ל-Windows:** [AutoLangSwitcher.msi](https://github.com/AniDavid/AutoLangSwitcher/releases/latest/download/AutoLangSwitcher.msi), או בשורת הפקודה: `winget install AniDavid.AutoLangSwitcher`. התוכנה בודקת פעם ביום אם יש גרסה חדשה (אפשר לכבות את זה בלשונית General), ואפשר גם לבדוק ידנית דרך **Check for updates...** בתפריט של הסמל. התוכנה עדיין לא חתומה דיגיטלית, ולכן Windows תציג אזהרה. לחצו על **More info** ואז על **Run anyway**.

</div>

## How to use

There's nothing to set up. Use your computer as usual:

- Set the language you want in a program. AutoLangSwitcher remembers it.
- When you come back to that program later, even after a restart, its language comes back automatically.
- To change a program's language, just switch it again. The new choice replaces the old one.

It remembers by program, not by window or tab. All browser tabs share one language, for example.

**Language rules** (optional): to give a program a fixed language instead of the last one you used, right-click it on the **Apps** tab of the Settings window:

- **Lock to** a language: the program gets it every time it comes to the front. You can still switch while you're in it, but that isn't remembered. For example, lock VS Code to English so a Hebrew comment doesn't stick.
- **Start in** a language: the program gets it when it's opened after being closed; while it runs, your switches are remembered as usual. For example, WhatsApp always opens in Hebrew.

---

## Windows

**Install**

1. Unzip and copy the `Windows` folder somewhere you'll keep it, for example `C:\Tools\AutoLangSwitcher`.
2. Double-click `build.bat`. It builds `AutoLangSwitcher.exe` with tools already included in Windows and starts it. Nothing extra needs to be installed.
3. Click **Yes** when Windows asks for admin rights. AutoLangSwitcher needs them to switch the keyboard in programs that run as administrator.
4. Double-click the blue keyboard icon with the green arrow in the tray, and on the **General** tab tick **Start with Windows**.

**Or use winget:** `winget install AniDavid.AutoLangSwitcher` (and `winget upgrade AniDavid.AutoLangSwitcher` for new versions).

**Or use the installer:** download [AutoLangSwitcher.msi](https://github.com/AniDavid/AutoLangSwitcher/releases/latest/download/AutoLangSwitcher.msi) from the [latest release](https://github.com/AniDavid/AutoLangSwitcher/releases/latest) and run it. It installs to `C:\Program Files\AutoLangSwitcher`, adds a Start menu shortcut and starts the app. Uninstall it from **Settings → Apps**; this also turns off **Start with Windows**. Your remembered programs in `%APPDATA%` are kept. The installer isn't digitally signed, so Windows shows a SmartScreen warning: click **More info → Run anyway**.

Developers can also open `AutoLangSwitcher.sln` in Visual Studio. It builds a .NET Framework 4.8 version (nothing to install) and a .NET 10 version (needs the .NET 10 Desktop Runtime).

**Tray menu** (right-click the icon)

| Item | What it does |
|---|---|
| **Settings...** | Opens the Settings window (double-clicking the icon does the same). |
| **Help** | Opens the Settings window on the **Help** tab: a user manual with pictures. |
| **Pause** | Temporarily stops switching languages. |
| **Check for updates...** | Asks GitHub for the latest version and offers the download page if there's a newer one. It also checks once a day by itself (untick **Check for updates once a day** on the **General** tab to stop that). |
| **About AutoLangSwitcher** | Shows the version and credits. |
| **Exit** | Closes AutoLangSwitcher. |

While **Caps Lock** is on, the tray keyboard turns orange and shows a capital **A**.

**Settings window**

| Tab | What it shows |
|---|---|
| **Apps** | Each remembered program, by name, with its language, its rule and whether Smart Keyboard Auto Switch is on there. **Right-click** a program (or double-click it) to lock its language, set the language it opens in, turn smart switch off or on there, or forget it; Ctrl+click selects several. **Remove all** forgets every program. |
| **General** | **Pause**, **Start with Windows**, **Check for updates once a day**, **Smart Keyboard Auto Switch** (mode, fix key, never-change list), and whether AutoLangSwitcher is running as administrator. |
| **Smart switch off** | The programs where Smart Keyboard Auto Switch stays off, one exe name per line. Code editors and terminals are on it to start with. |
| **Settings file** | The saved list, exactly as stored on disk. |
| **Log** | What AutoLangSwitcher has been doing. Useful if something doesn't work: **Copy all** and paste it into an issue. |
| **Help** | A short user manual and how AutoLangSwitcher works. |

**Smart Keyboard Auto Switch** (Windows and macOS, off by default; turn it on in **Settings → General**)

Typed "akuo" when you meant "שלום"? When you finish a word (with Space, punctuation like `, . ? !`, or Enter), AutoLangSwitcher checks whether the same keys make a real word in another of your installed keyboard languages. If they do, it either suggests the fix (press the **fix key** within 10 seconds to retype the word) or fixes it automatically, whichever you choose. It also switches the keyboard to that language.

- The fix key is **Pause/Break** by default; on the General tab you can choose Scroll Lock, Insert or Ctrl+Shift+Z instead.
- In **Fix automatically** mode, a word ended with Enter is fixed before the Enter goes through, so a chat message is sent already fixed. (In suggest mode, words ended with Enter aren't checked, because the message would already be sent. Shift+Enter, a new line in chat apps, is left alone on Windows.)

- It checks words with the spelling dictionaries built into Windows, plus built-in letter statistics for English, Hebrew, Russian, Ukrainian, Arabic, Greek, French, German, Italian and Spanish. Only your installed keyboard languages are used, and nothing is downloaded.
- Short words that are real in both languages (English "do" is the Hebrew word "גם" on the same keys) are decided by context: up to 3 earlier words are fixed together with a clearly wrong word, so "do akuo" becomes "גם שלום", while "I went to akuo" keeps its English "to".
- Words that aren't in any dictionary, like names, are left alone. If it changes a word you didn't want changed, press the fix key right away to put it back. That word goes on the **Never change these words** list on the General tab, where you can also add or remove words yourself.
- A word that is real in both languages, like "tbh" typed when you meant "אני", looks right, so nothing is suggested. Press the fix key right after it anyway (before typing anything else) to change it to your other keyboard language; press it again to put it back.
- It stays off in code editors and terminals (VS Code, Visual Studio, JetBrains IDEs, Xcode, Windows Terminal, PowerShell, Terminal, iTerm and others), where keys can be commands and auto-complete changes the text. You can edit that list on the **Smart switch off** tab, or right-click programs on the **Apps** tab.
- Typed "hELLO" because Caps Lock was on by mistake? It's fixed to "Hello" (or suggested) and Caps Lock is turned off. (Windows only: on a Mac, Shift with Caps Lock still types capitals.)
- To work, it has to read your typing. The letters of the current word stay in memory only until you finish it: nothing is logged or sent, and the only thing saved is the never-change list. While the feature is off, AutoLangSwitcher doesn't read your typing at all.
- Some antivirus programs are suspicious of any app that reads typing. If yours complains, turn the feature off.
- **On macOS** the fix key is **Control+Option+Z** by default (Macs have no Pause/Break key; you can choose Control+Option+X, Control+Option+F or F13), the suggestion appears next to the menu bar icon, and macOS asks you to allow AutoLangSwitcher under System Settings → Privacy & Security → **Accessibility**. After rebuilding the app, macOS may ask again: remove the old entry and allow the new one.

If you clicked **No** at the admin prompt, it won't be able to switch programs that run as administrator. Use **Restart as administrator** in the tray menu or on the **General** tab. Settings are saved in `%APPDATA%\AutoLangSwitcher`: `layouts.txt` (the programs and their languages), `language-rules.txt`, `program-names.txt`, `never-change.txt` and `smart-switch-off.txt`.

---

## macOS

> **Note:** I don't have a Mac, so the macOS version has not been tested. If you find a problem, please [open an issue](https://github.com/AniDavid/AutoLangSwitcher/issues).

Requires macOS 11 (Big Sur) or later.

**Install**

1. Unzip and open Terminal in the `macOS` folder. One way is to type `cd `, drag the folder into the Terminal window, and press Return.
2. Run:
   ```
   ./build.sh
   ```
   If it says the Swift compiler is missing, run `xcode-select --install`, accept the download, and run `./build.sh` again.
3. The app is installed in your Applications folder (`~/Applications`) and starts automatically. A keyboard icon with a small arrow badge appears in the menu bar.
4. Click the icon, choose **Settings…**, and on the **General** tab tick **Start at Login**.

No special permissions are needed, unless you turn on **Smart Keyboard Auto Switch** (described in the Windows section), which needs the Accessibility permission.

**Menu bar menu**

| Item | What it does |
|---|---|
| **Settings…** | Opens the Settings window. |
| **Help** | Opens the Settings window on the **Help** tab. |
| **Pause** | Temporarily stops switching. |
| **About AutoLangSwitcher** | Shows the version and credits. |
| **Quit AutoLangSwitcher** | Closes it. |

While **Caps Lock** is on, the menu bar keyboard turns orange and shows a capital **A**.

The **Settings** window has the same tabs as on Windows: **Apps** (right-click an app to set a language rule, turn smart switch off or on, or forget it), **General** (**Pause**, **Start at Login** and **Smart Keyboard Auto Switch**), **Smart switch off** (the apps where smart switch stays off, by bundle id; `com.jetbrains.*` matches a family), **Saved list** (the remembered list; macOS keeps it in the app's preferences instead of a file), **Log** and **Help**.

**Tip:** Turn off macOS's own **"Automatically switch to a document's input source"** setting so the two don't fight. It's in System Settings → Keyboard → Text Input → Input Sources → Edit.

**Sharing the Mac app:** Each person should run `build.sh` on their own Mac. A copy of the built app sent to someone else is unsigned, so macOS will block it the first time. They'd have to allow it under System Settings → Privacy & Security → **Open Anyway**.

---

## License

[MIT](LICENSE)
