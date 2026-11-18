# Builds Windows\LetterStatsData.cs and macOS\LetterStatsData.swift: letter-pair statistics for the languages Smart Keyboard Auto Switch
# knows without a Windows dictionary. Run once on a developer PC (needs internet); users never download anything.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-letterstats.ps1
#
# Source: plain text of random Wikipedia article intros. Only letter-pair counts are kept.

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# a-z plus the language's accented letters, given as code points.
function Latin([int[]]$extra) { '[a-z' + (-join ($extra | ForEach-Object { [char]$_ })) + ']' }

# Language -> regex class of its letters (lower case), built from code points so this file stays ASCII.
$langs = [ordered]@{
    en = '[a-z]'
    he = ('[{0}-{1}]' -f [char]0x05D0, [char]0x05EA)
    ru = ('[{0}-{1}{2}]' -f [char]0x0430, [char]0x044F, [char]0x0451)
    uk = ('[{0}-{1}{2}{3}{4}{5}{6}{7}{8}]' -f [char]0x0430, [char]0x0449, [char]0x044C, [char]0x044E, [char]0x044F,
          [char]0x0454, [char]0x0456, [char]0x0457, [char]0x0491)   # without the Russian-only letters
    ar = ('[{0}-{1}]' -f [char]0x0621, [char]0x064A)
    el = ('[{0}-{1}]' -f [char]0x03AC, [char]0x03CE)
    fr = Latin 0xE0,0xE2,0xE6,0xE7,0xE8,0xE9,0xEA,0xEB,0xEE,0xEF,0xF4,0x153,0xF9,0xFB,0xFC,0xFF
    de = Latin 0xE4,0xF6,0xFC,0xDF
    it = Latin 0xE0,0xE8,0xE9,0xEC,0xED,0xEE,0xF2,0xF3,0xF9,0xFA
    es = Latin 0xE1,0xE9,0xED,0xF1,0xF3,0xFA,0xFC
}
$minLetters = 150000
$out = Join-Path $PSScriptRoot '..\Windows\LetterStatsData.cs'

# Counting is done in C# because a PowerShell loop over a million letters is slow.
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Linq; using System.Text;
public static class Counter
{
    // Returns alphabet, cost table (one char per pair, 0-9a-z = 0..35 half-bits), threshold, samples.
    public static string[] Build(List<string> words)
    {
        var freq = new Dictionary<char, int>();
        long total = 0;
        foreach (var w in words) foreach (var c in w) { int n; freq.TryGetValue(c, out n); freq[c] = n + 1; total++; }
        // Every letter of the language's class that occurs at all, even rare ones like Italian "o" with a grave accent.
        string alpha = new string(freq.Keys.OrderBy(c => c).ToArray());
        int size = alpha.Length + 1;   // index 0 = word start / end
        var count = new double[size, size];
        foreach (var w in words)
        {
            if (w.Any(c => alpha.IndexOf(c) < 0)) continue;
            int prev = 0;
            foreach (var c in w) { int i = alpha.IndexOf(c) + 1; count[prev, i]++; prev = i; }
            count[prev, 0]++;
        }
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var table = new StringBuilder();
        var cost = new int[size, size];
        for (int a = 0; a < size; a++)
        {
            double row = 0; for (int b = 0; b < size; b++) row += count[a, b];
            for (int b = 0; b < size; b++)
            {
                double p = (count[a, b] + 0.5) / (row + 0.5 * size);
                cost[a, b] = Math.Min(35, (int)Math.Round(-2 * Math.Log(p, 2)));
                table.Append(digits[cost[a, b]]);
            }
        }
        // Threshold: 95% of real word occurrences (3+ letters) score at or below it.
        var scores = new List<double>();
        foreach (var w in words)
        {
            if (w.Length < 3 || w.Any(c => alpha.IndexOf(c) < 0)) continue;
            int prev = 0, sum = 0;
            foreach (var c in w) { int i = alpha.IndexOf(c) + 1; sum += cost[prev, i]; prev = i; }
            sum += cost[prev, 0];
            scores.Add((double)sum / (w.Length + 1));
        }
        scores.Sort();
        double threshold = scores[(int)(scores.Count * 0.95)];
        var samples = words.Where(w => w.Length >= 4).GroupBy(w => w).OrderByDescending(g => g.Count())
                           .Take(20).Select(g => g.Key);
        return new[] { alpha, table.ToString(), threshold.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                       string.Join(",", samples) };
    }
}
'@

# Non-ASCII letters as escapes: backslash-u plus 4 hex digits for C#, the same in braces for Swift.
function Escape([string]$s, [string]$format = '\u{0}') {
    $sb = New-Object Text.StringBuilder
    foreach ($c in $s.ToCharArray()) {
        if ([int]$c -lt 128) { [void]$sb.Append($c) } else { [void]$sb.AppendFormat($format, ([int]$c).ToString('X4')) }
    }
    $sb.ToString()
}
$swiftFormat = '\u{{{0}}}'

$lines = @(); $swiftLines = @()
foreach ($lang in $langs.Keys) {
    $words = New-Object 'Collections.Generic.List[string]'
    $letters = 0
    while ($letters -lt $minLetters) {
        # The intros of 20 random articles per request, slowly, to stay within Wikipedia's rate limits.
        $url = "https://$lang.wikipedia.org/w/api.php?action=query&format=json&generator=random&grnnamespace=0" +
               "&grnlimit=20&prop=extracts&exintro=1&explaintext=1&exlimit=20"
        try { $r = Invoke-RestMethod -Uri $url -UserAgent 'letterstats-script/1.0' }
        catch { Write-Host "  rate limited, waiting"; Start-Sleep -Seconds 30; continue }
        Start-Sleep -Seconds 3
        foreach ($p in $r.query.pages.PSObject.Properties) {
            $text = [string]$p.Value.extract
            foreach ($m in [regex]::Matches($text.ToLowerInvariant(), "$($langs[$lang])+")) {
                $words.Add($m.Value); $letters += $m.Value.Length
            }
        }
        Write-Host "$lang $letters letters"
    }
    $res = [Counter]::Build($words)
    $lines += "        `"$lang`", `"$(Escape $res[0])`", `"$($res[2])`", `"$(Escape $res[3])`",`r`n        `"$($res[1])`","
    $swiftLines += "        `"$lang`", `"$(Escape $res[0] $swiftFormat)`", `"$($res[2])`", `"$(Escape $res[3] $swiftFormat)`",`n        `"$($res[1])`","
}

$cs = @"
// Generated by tools/make-letterstats.ps1 from Wikipedia article text. Do not edit by hand.
// Per language: code, alphabet, threshold, sample words, cost table (see LetterStats.cs).
static partial class LetterStats
{
    static readonly string[] Data =
    {
$($lines -join "`r`n")
    };
}
"@
[IO.File]::WriteAllText((Resolve-Path (Split-Path $out)).Path + '\LetterStatsData.cs', $cs, (New-Object Text.UTF8Encoding $false))
Write-Host "wrote $out"

$swift = "// Generated by tools/make-letterstats.ps1 from Wikipedia article text. Do not edit by hand.`n" +
         "// The same data as Windows/LetterStatsData.cs. Per language: code, alphabet, threshold, sample words, cost table.`n" +
         "extension LetterStats {`n    static let data: [String] = [`n" + ($swiftLines -join "`n") + "`n    ]`n}`n"
$swiftOut = (Resolve-Path (Join-Path $PSScriptRoot '..\macOS')).Path + '\LetterStatsData.swift'
[IO.File]::WriteAllText($swiftOut, $swift, (New-Object Text.UTF8Encoding $false))
Write-Host "wrote $swiftOut"
