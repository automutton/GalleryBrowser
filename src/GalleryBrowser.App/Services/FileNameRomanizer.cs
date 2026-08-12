using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace GalleryBrowser.Services;

public static class FileNameRomanizer
{
    private const int MaximumCachedValues = 32_768;
    private static readonly ConcurrentDictionary<string, string> RomanizedValues =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> RomanizedValueOrder = new();
    private static int _romanizedValueCount;
    private static readonly IReadOnlyDictionary<string, string> CompoundSyllables = new Dictionary<string, string>
    {
        ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo",
        ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
        ["しゃ"] = "sha", ["しゅ"] = "shu", ["しょ"] = "sho",
        ["じゃ"] = "ja", ["じゅ"] = "ju", ["じょ"] = "jo",
        ["ちゃ"] = "cha", ["ちゅ"] = "chu", ["ちょ"] = "cho",
        ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo",
        ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
        ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo",
        ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
        ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo",
        ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
        ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo",
        ["てぃ"] = "ti", ["でぃ"] = "di", ["とぅ"] = "tu", ["どぅ"] = "du",
        ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo",
        ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo"
    };
    private static readonly IReadOnlyDictionary<int, string> CompoundSyllablePairs =
        CompoundSyllables.ToDictionary(
            entry => CreateCompoundSyllableKey(entry.Key[0], entry.Key[1]),
            entry => entry.Value);

    private static readonly IReadOnlyDictionary<char, string> BasicSyllables = new Dictionary<char, string>
    {
        ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
        ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['を'] = "wo", ['ん'] = "n", ['ゔ'] = "vu",
        ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o",
        ['ゎ'] = "wa", ['ゐ'] = "wi", ['ゑ'] = "we"
    };

    public static string Romanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        if (RomanizedValues.TryGetValue(value, out var cached))
        {
            return cached;
        }
        if (IsAscii(value))
        {
            return RememberRomanized(value, RomanizeAscii(value));
        }

        var normalized = value.IsNormalized(NormalizationForm.FormKC)
            ? value
            : value.Normalize(NormalizationForm.FormKC);
        var source = ToHiragana(normalized);
        var result = new StringBuilder(source.Length * 2);
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            if (current == 'っ')
            {
                var next = GetSyllable(source, index + 1, out _);
                if (!string.IsNullOrEmpty(next) && char.IsLetter(next[0]) && next[0] is not 'a' and not 'e' and not 'i' and not 'o' and not 'u')
                {
                    result.Append(next[0]);
                }
                continue;
            }

            if (current == 'ー')
            {
                var vowel = LastVowel(result);
                if (vowel is not null)
                {
                    result.Append(vowel.Value);
                }
                continue;
            }

            var syllable = GetSyllable(source, index, out var consumed);
            if (syllable is not null)
            {
                result.Append(syllable);
                index += consumed - 1;
            }
            else if (char.IsLetterOrDigit(current))
            {
                result.Append(char.ToLowerInvariant(current));
            }
            else if (char.IsWhiteSpace(current) && (result.Length == 0 || result[^1] != ' '))
            {
                result.Append(' ');
            }
        }

        return RememberRomanized(value, result.ToString().Trim());
    }

    private static bool IsAscii(string value)
    {
        foreach (var character in value)
        {
            if (character > '\u007f')
            {
                return false;
            }
        }
        return true;
    }

    private static string RomanizeAscii(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z')
            {
                result.Append((char)(character + ('a' - 'A')));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                result.Append(character);
            }
            else if (char.IsWhiteSpace(character) && (result.Length == 0 || result[^1] != ' '))
            {
                result.Append(' ');
            }
        }
        return result.ToString().Trim();
    }

    private static string RememberRomanized(string source, string romanized)
    {
        if (!RomanizedValues.TryAdd(source, romanized))
        {
            return RomanizedValues.TryGetValue(source, out var existing) ? existing : romanized;
        }

        RomanizedValueOrder.Enqueue(source);
        Interlocked.Increment(ref _romanizedValueCount);
        while (Volatile.Read(ref _romanizedValueCount) > MaximumCachedValues &&
               RomanizedValueOrder.TryDequeue(out var oldest))
        {
            if (RomanizedValues.TryRemove(oldest, out _))
            {
                Interlocked.Decrement(ref _romanizedValueCount);
            }
        }
        return romanized;
    }

    private static string ToHiragana(string value)
    {
        var firstKatakanaIndex = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is >= '\u30A1' and <= '\u30F6')
            {
                firstKatakanaIndex = index;
                break;
            }
        }
        if (firstKatakanaIndex < 0)
        {
            return value;
        }

        return string.Create(value.Length, (value, firstKatakanaIndex), static (destination, state) =>
        {
            var (source, firstIndex) = state;
            source.AsSpan(0, firstIndex).CopyTo(destination);
            for (var index = firstIndex; index < source.Length; index++)
            {
                var character = source[index];
                destination[index] = character is >= '\u30A1' and <= '\u30F6'
                    ? (char)(character - 0x60)
                    : character;
            }
        });
    }

    private static string? GetSyllable(string value, int index, out int consumed)
    {
        consumed = 1;
        if (index >= value.Length)
        {
            return null;
        }

        if (index + 1 < value.Length &&
            CompoundSyllablePairs.TryGetValue(
                CreateCompoundSyllableKey(value[index], value[index + 1]),
                out var compound))
        {
            consumed = 2;
            return compound;
        }

        return BasicSyllables.TryGetValue(value[index], out var basic) ? basic : null;
    }

    private static int CreateCompoundSyllableKey(char first, char second) =>
        (first << 16) | second;

    private static char? LastVowel(StringBuilder value)
    {
        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (value[index] is 'a' or 'e' or 'i' or 'o' or 'u')
            {
                return value[index];
            }
        }

        return null;
    }
}
