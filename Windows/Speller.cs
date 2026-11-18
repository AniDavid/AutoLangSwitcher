using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// Windows' own spell checker (Windows 8 and later). It has a dictionary for each installed
// language, so nothing has to be downloaded.
static class Speller
{
    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        void get_SupportedLanguages(out IntPtr value);   // not used; keeps the method order
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        void get_LanguageTag(out IntPtr value);           // not used; keeps the method order
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.IUnknown)] out object error);
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    class SpellCheckerFactory { }

    static ISpellCheckerFactory factory;
    static bool unavailable;
    static readonly Dictionary<string, ISpellChecker> checkers = new Dictionary<string, ISpellChecker>();

    // True or false, or null when Windows has no dictionary for the language (e.g. "he-IL").
    public static bool? Check(string lang, string word)
    {
        ISpellChecker checker = Get(lang);
        if (checker == null) return null;
        try
        {
            object error;
            return checker.Check(word).Next(out error) != 0;   // S_FALSE (1): no spelling errors
        }
        catch { return null; }
    }

    // True when the dictionary has the word with other capitals: "api" -> "API", "london" -> "London".
    // (Checking "API" itself proves nothing: words in capitals are never reported as misspelled.)
    public static bool KnownWithOtherCase(string lang, string word)
    {
        ISpellChecker checker = Get(lang);
        if (checker == null) return false;
        try
        {
            IEnumString suggestions = checker.Suggest(word);
            string[] one = new string[1];
            for (int i = 0; i < 10 && suggestions.Next(1, one, IntPtr.Zero) == 0; i++)
                if (string.Equals(one[0], word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    static ISpellChecker Get(string lang)
    {
        ISpellChecker checker;
        if (string.IsNullOrEmpty(lang) || unavailable) return null;
        if (checkers.TryGetValue(lang, out checker)) return checker;
        try
        {
            if (factory == null) factory = (ISpellCheckerFactory)new SpellCheckerFactory();
            checker = factory.IsSupported(lang) != 0 ? factory.CreateSpellChecker(lang) : null;
        }
        catch { unavailable = factory == null; checker = null; }
        checkers[lang] = checker;
        return checker;
    }
}
