using System;
using System.Collections.Generic;
using System.Globalization;

// Letter-pair statistics: how much a word looks like a language, for languages Windows has no
// dictionary for (and as a second opinion when it has). The tables are in LetterStatsData.cs,
// made by tools/make-letterstats.ps1.
static partial class LetterStats
{
    const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";   // cost of a letter pair, in half-bits
    const int Foreign = 36;   // cost of a character outside the language's alphabet

    class Table { public string Alphabet, Costs; public double Threshold; public string[] Samples; }
    static Dictionary<string, Table> tables;

    static Table Get(string lang)
    {
        if (tables == null)
        {
            tables = new Dictionary<string, Table>();
            for (int i = 0; i + 4 < Data.Length; i += 5)
            {
                Table t = new Table();
                t.Alphabet = Data[i + 1];
                t.Threshold = double.Parse(Data[i + 2], CultureInfo.InvariantCulture);
                t.Samples = Data[i + 3].Split(',');
                t.Costs = Data[i + 4];
                tables[Data[i]] = t;
            }
        }
        Table found;
        return lang != null && tables.TryGetValue(lang, out found) ? found : null;
    }

    public static bool Has(string lang) { return Get(lang) != null; }

    public static string[] Samples(string lang) { return Get(lang).Samples; }

    public static IEnumerable<string> Languages { get { Get(null); return tables.Keys; } }

    // Average cost per letter pair (word start and end included) minus the language's threshold.
    // Negative: looks like a word of this language. Two-letter language code, e.g. "he".
    public static double Margin(string lang, string word)
    {
        Table t = Get(lang);
        int size = t.Alphabet.Length + 1, prev = 0, sum = 0;
        foreach (char ch in word.ToLowerInvariant())
        {
            int i = t.Alphabet.IndexOf(ch) + 1;
            if (i == 0) { sum += Foreign; prev = 0; continue; }
            sum += Digits.IndexOf(t.Costs[prev * size + i]);
            prev = i;
        }
        sum += Digits.IndexOf(t.Costs[prev * size]);
        return (double)sum / (word.Length + 1) - t.Threshold;
    }
}
