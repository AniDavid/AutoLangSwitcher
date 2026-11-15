# AutoLangSwitcher

**Windows: [Download the installer](https://github.com/AniDavid/AutoLangSwitcher/releases/latest/download/AutoLangSwitcher.msi)** · macOS: build from source (see below)

## Why

If you write in more than one language, you probably use a different keyboard language in different programs: Hebrew in WhatsApp or Word, English in your code editor or terminal. Windows and macOS both use one keyboard language for all programs by default. Every time you switch programs you have to check the language and switch it, and you often only notice after typing a line of gibberish.

AutoLangSwitcher fixes this. It runs quietly in the background and remembers the last keyboard language you used in each program. When you switch back to that program, it switches the keyboard back for you.

It does not watch what you type or fix text typed in the wrong language. It only restores each program's language.

## בעברית

<div dir="rtl">

כותבים גם בעברית וגם באנגלית? AutoLangSwitcher זוכרת את שפת המקלדת האחרונה שבחרתם בכל תוכנה, ומחזירה אותה אוטומטית כשאתם חוזרים לאותה תוכנה. למשל, עברית בוואטסאפ ובוורד, ואנגלית בעורך הקוד ובטרמינל. בלי להקליד שורה של ג'יבריש ולגלות רק אחר כך שהשפה לא נכונה.

אין שום הגדרות. פשוט בוחרים שפה בתוכנה, והיא נזכרת. התוכנה לא קוראת את מה שאתם מקלידים.

**להורדה ל-Windows:** [AutoLangSwitcher.msi](https://github.com/AniDavid/AutoLangSwitcher/releases/latest/download/AutoLangSwitcher.msi). התוכנה עדיין לא חתומה דיגיטלית, ולכן Windows תציג אזהרה. לחצו על **More info** ואז על **Run anyway**.

</div>

## How to use

There's nothing to set up. Use your computer as usual:

- Set the language you want in a program. AutoLangSwitcher remembers it.
- When you come back to that program later, even after a restart, its language comes back automatically.
- To change a program's language, just switch it again. The new choice replaces the old one.

It remembers by program, not by window or tab. All browser tabs share one language, for example.

---

## Windows

**Install**

1. Unzip and copy the `Windows` folder somewhere you'll keep it, for example `C:\Tools\AutoLangSwitcher`.
2. Double-click `build.bat`. It builds `AutoLangSwitcher.exe` with tools already included in Windows and starts it. Nothing extra needs to be installed.
3. Click **Yes** when Windows asks for admin rights. AutoLangSwitcher needs them to switch the keyboard in programs that run as administrator.
4. Double-click the blue keyboard icon with the green arrow in the tray, and on the **General** tab tick **Start with Windows**.

**Or use the installer:** download [AutoLangSwitcher.msi](https://github.com/AniDavid/AutoLangSwitcher/releases/latest/download/AutoLangSwitcher.msi) from the [latest release](https://github.com/AniDavid/AutoLangSwitcher/releases/latest) and run it. It installs to `C:\Program Files\AutoLangSwitcher`, adds a Start menu shortcut and starts the app. Uninstall it from **Settings → Apps**; this also turns off **Start with Windows**. Your remembered programs in `%APPDATA%` are kept. The installer isn't digitally signed, so Windows shows a SmartScreen warning: click **More info → Run anyway**.

Developers can also open `AutoLangSwitcher.sln` in Visual Studio. It builds a .NET Framework 4.8 version (nothing to install) and a .NET 10 version (needs the .NET 10 Desktop Runtime).

**Tray menu** (right-click the icon)

| Item | What it does |
|---|---|
| **Settings...** | Opens the Settings window (double-clicking the icon does the same). |
| **Pause** | Temporarily stops switching languages. |
| **About AutoLangSwitcher** | Shows the version and credits. |
| **Exit** | Closes AutoLangSwitcher. |

While **Caps Lock** is on, the tray keyboard turns orange and shows a capital **A**.

**Settings window**

| Tab | What it shows |
|---|---|
| **Apps** | Each remembered program and its language. Select programs and click **Remove** to forget them, or **Remove all**. |
| **General** | **Pause**, **Start with Windows**, and whether AutoLangSwitcher is running as administrator. |
| **Settings file** | The saved list, exactly as stored on disk. |
| **Log** | What AutoLangSwitcher has been doing. Useful if something doesn't work: **Copy all** and paste it into an issue. |
| **Help** | A short user manual and how AutoLangSwitcher works. |

If you clicked **No** at the admin prompt, it won't be able to switch programs that run as administrator. Use **Restart as administrator** in the tray menu or on the **General** tab. Settings are saved in `%APPDATA%\AutoLangSwitcher\layouts.txt`.

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

No special permissions are needed.

**Menu bar menu**

| Item | What it does |
|---|---|
| **Settings…** | Opens the Settings window. |
| **Pause** | Temporarily stops switching. |
| **About AutoLangSwitcher** | Shows the version and credits. |
| **Quit AutoLangSwitcher** | Closes it. |

While **Caps Lock** is on, the menu bar keyboard turns orange and shows a capital **A**.

The **Settings** window has the same tabs as on Windows: **Apps** (remove remembered apps), **General** (**Pause** and **Start at Login**), **Saved list** (the remembered list; macOS keeps it in the app's preferences instead of a file), **Log** and **Help**.

**Tip:** Turn off macOS's own **"Automatically switch to a document's input source"** setting so the two don't fight. It's in System Settings → Keyboard → Text Input → Input Sources → Edit.

**Sharing the Mac app:** Each person should run `build.sh` on their own Mac. A copy of the built app sent to someone else is unsigned, so macOS will block it the first time. They'd have to allow it under System Settings → Privacy & Security → **Open Anyway**.

---

## License

[MIT](LICENSE)
