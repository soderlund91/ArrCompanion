using System;
using System.Collections.Generic;
using System.Linq;

namespace ArrCompanion
{
    /// <summary>
    /// Languages, countries and genres in one form, whatever gave them: languages as ISO 639-1
    /// codes (TMDb's original_language; a file's audio language in ISO 639-2 is turned into one),
    /// countries as ISO 3166-1 codes, genres split and trimmed. English names come from small
    /// tables, not from CultureInfo: many Emby installs run without culture data.
    /// </summary>
    internal static class Languages
    {
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["af"] = "Afrikaans", ["am"] = "Amharic", ["ar"] = "Arabic", ["az"] = "Azerbaijani", ["be"] = "Belarusian",
            ["bg"] = "Bulgarian", ["bn"] = "Bengali", ["bo"] = "Tibetan", ["bs"] = "Bosnian", ["ca"] = "Catalan",
            ["cn"] = "Cantonese", ["cs"] = "Czech", ["cy"] = "Welsh", ["da"] = "Danish", ["de"] = "German",
            ["el"] = "Greek", ["en"] = "English", ["eo"] = "Esperanto", ["es"] = "Spanish", ["et"] = "Estonian",
            ["eu"] = "Basque", ["fa"] = "Persian", ["fi"] = "Finnish", ["fil"] = "Filipino", ["fr"] = "French",
            ["ga"] = "Irish", ["gl"] = "Galician", ["gu"] = "Gujarati", ["ha"] = "Hausa", ["he"] = "Hebrew",
            ["hi"] = "Hindi", ["hr"] = "Croatian", ["hu"] = "Hungarian", ["hy"] = "Armenian", ["id"] = "Indonesian",
            ["ig"] = "Igbo", ["is"] = "Icelandic", ["it"] = "Italian", ["iu"] = "Inuktitut", ["ja"] = "Japanese",
            ["ka"] = "Georgian", ["kk"] = "Kazakh", ["km"] = "Khmer", ["kn"] = "Kannada", ["ko"] = "Korean",
            ["ku"] = "Kurdish", ["ky"] = "Kyrgyz", ["la"] = "Latin", ["lb"] = "Luxembourgish", ["lo"] = "Lao",
            ["lt"] = "Lithuanian", ["lv"] = "Latvian", ["mi"] = "Maori", ["mk"] = "Macedonian", ["ml"] = "Malayalam",
            ["mn"] = "Mongolian", ["mr"] = "Marathi", ["ms"] = "Malay", ["mt"] = "Maltese", ["my"] = "Burmese",
            ["nb"] = "Norwegian", ["ne"] = "Nepali", ["nl"] = "Dutch", ["nn"] = "Norwegian", ["no"] = "Norwegian",
            ["pa"] = "Punjabi", ["pl"] = "Polish", ["ps"] = "Pashto", ["pt"] = "Portuguese", ["ro"] = "Romanian",
            ["ru"] = "Russian", ["rw"] = "Kinyarwanda", ["si"] = "Sinhala", ["sk"] = "Slovak", ["sl"] = "Slovenian",
            ["sn"] = "Shona", ["so"] = "Somali", ["sq"] = "Albanian", ["sr"] = "Serbian", ["st"] = "Sesotho",
            ["sv"] = "Swedish", ["sw"] = "Swahili", ["ta"] = "Tamil", ["te"] = "Telugu", ["tg"] = "Tajik",
            ["th"] = "Thai", ["tl"] = "Tagalog", ["tn"] = "Setswana", ["tr"] = "Turkish", ["uk"] = "Ukrainian",
            ["ur"] = "Urdu", ["uz"] = "Uzbek", ["vi"] = "Vietnamese", ["wo"] = "Wolof", ["xh"] = "Xhosa",
            ["yi"] = "Yiddish", ["yo"] = "Yoruba", ["zh"] = "Chinese", ["zu"] = "Zulu"
        };

        // ISO 639-2 (B and T) as audio streams carry them -> ISO 639-1.
        private static readonly Dictionary<string, string> ThreeToTwo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["afr"] = "af", ["amh"] = "am", ["ara"] = "ar", ["aze"] = "az", ["bel"] = "be", ["bul"] = "bg", ["ben"] = "bn",
            ["bos"] = "bs", ["cat"] = "ca", ["yue"] = "cn", ["cze"] = "cs", ["ces"] = "cs", ["wel"] = "cy", ["cym"] = "cy",
            ["dan"] = "da", ["ger"] = "de", ["deu"] = "de", ["gre"] = "el", ["ell"] = "el", ["eng"] = "en", ["spa"] = "es",
            ["est"] = "et", ["baq"] = "eu", ["eus"] = "eu", ["per"] = "fa", ["fas"] = "fa", ["fin"] = "fi", ["fil"] = "fil",
            ["fre"] = "fr", ["fra"] = "fr", ["gle"] = "ga", ["glg"] = "gl", ["guj"] = "gu", ["heb"] = "he", ["hin"] = "hi",
            ["hrv"] = "hr", ["hun"] = "hu", ["arm"] = "hy", ["hye"] = "hy", ["ind"] = "id", ["ice"] = "is", ["isl"] = "is",
            ["ita"] = "it", ["jpn"] = "ja", ["geo"] = "ka", ["kat"] = "ka", ["kaz"] = "kk", ["khm"] = "km", ["kan"] = "kn",
            ["kor"] = "ko", ["lao"] = "lo", ["lit"] = "lt", ["lav"] = "lv", ["mac"] = "mk", ["mkd"] = "mk", ["mal"] = "ml",
            ["mon"] = "mn", ["mar"] = "mr", ["may"] = "ms", ["msa"] = "ms", ["bur"] = "my", ["mya"] = "my", ["nob"] = "no",
            ["nno"] = "no", ["nor"] = "no", ["nep"] = "ne", ["dut"] = "nl", ["nld"] = "nl", ["pan"] = "pa", ["pol"] = "pl",
            ["por"] = "pt", ["rum"] = "ro", ["ron"] = "ro", ["rus"] = "ru", ["sin"] = "si", ["slo"] = "sk", ["slk"] = "sk",
            ["slv"] = "sl", ["som"] = "so", ["alb"] = "sq", ["sqi"] = "sq", ["srp"] = "sr", ["sot"] = "st", ["swe"] = "sv",
            ["swa"] = "sw", ["tam"] = "ta", ["tel"] = "te", ["tha"] = "th", ["tgl"] = "tl", ["tsn"] = "tn", ["tur"] = "tr",
            ["ukr"] = "uk", ["urd"] = "ur", ["uzb"] = "uz", ["vie"] = "vi", ["xho"] = "xh", ["yid"] = "yi", ["yor"] = "yo",
            ["chi"] = "zh", ["zho"] = "zh", ["cmn"] = "zh", ["zul"] = "zu"
        };

        // Country codes and the names TMDb (and so Emby's metadata) gives them, for the countries
        // most titles come from; others keep their own name as their key.
        private static readonly Dictionary<string, string> Countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AR"] = "Argentina", ["AT"] = "Austria", ["AU"] = "Australia", ["BE"] = "Belgium", ["BR"] = "Brazil",
            ["CA"] = "Canada", ["CH"] = "Switzerland", ["CL"] = "Chile", ["CN"] = "China", ["CO"] = "Colombia",
            ["CZ"] = "Czech Republic", ["DE"] = "Germany", ["DK"] = "Denmark", ["EG"] = "Egypt", ["ES"] = "Spain",
            ["FI"] = "Finland", ["FR"] = "France", ["GB"] = "United Kingdom", ["GR"] = "Greece", ["HK"] = "Hong Kong",
            ["HU"] = "Hungary", ["ID"] = "Indonesia", ["IE"] = "Ireland", ["IL"] = "Israel", ["IN"] = "India",
            ["IR"] = "Iran", ["IS"] = "Iceland", ["IT"] = "Italy", ["JP"] = "Japan", ["KR"] = "South Korea",
            ["MX"] = "Mexico", ["MY"] = "Malaysia", ["NG"] = "Nigeria", ["NL"] = "Netherlands", ["NO"] = "Norway",
            ["NZ"] = "New Zealand", ["PH"] = "Philippines", ["PL"] = "Poland", ["PT"] = "Portugal", ["RO"] = "Romania",
            ["RU"] = "Russia", ["SA"] = "Saudi Arabia", ["SE"] = "Sweden", ["SG"] = "Singapore", ["TH"] = "Thailand",
            ["TR"] = "Turkey", ["TW"] = "Taiwan", ["UA"] = "Ukraine", ["US"] = "United States of America",
            ["VN"] = "Vietnam", ["ZA"] = "South Africa"
        };
        private static readonly Dictionary<string, string> CountryCodes = Countries
            .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> CountryAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USA"] = "US", ["United States"] = "US", ["UK"] = "GB", ["Great Britain"] = "GB", ["Korea"] = "KR",
            ["Republic of Korea"] = "KR", ["Korea, Republic of"] = "KR", ["Czechia"] = "CZ", ["Türkiye"] = "TR",
            ["Russian Federation"] = "RU", ["Viet Nam"] = "VN"
        };

        /// <summary>A language code as one form (ISO 639-1, "cn" for Cantonese); "" for none or unknown ("xx", "und").</summary>
        internal static string Code(string? raw)
        {
            var c = (raw ?? "").Trim();
            int dash = c.IndexOfAny(new[] { '-', '_' });
            if (dash > 0) c = c.Substring(0, dash);
            if (c.Length == 3 && ThreeToTwo.TryGetValue(c, out var two)) return two;
            if (c.Length < 2 || c.Length > 3 || c.Equals("xx", StringComparison.OrdinalIgnoreCase) || c.Equals("und", StringComparison.OrdinalIgnoreCase)) return "";
            c = c.ToLowerInvariant();
            return c == "nb" || c == "nn" ? "no" : c;
        }

        /// <summary>Every language with a name, by code (Norwegian once).</summary>
        internal static IEnumerable<KeyValuePair<string, string>> All() =>
            Names.Where(kv => kv.Key != "nb" && kv.Key != "nn").OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        /// <summary>The language's English name ("Chinese"); the code itself when it is not in the table.</summary>
        internal static string Name(string? code)
        {
            var c = Code(code);
            return c.Length == 0 ? "" : Names.TryGetValue(c, out var n) ? n : c.ToUpperInvariant();
        }

        /// <summary>A country as one form: its ISO code, else its own name.</summary>
        internal static string Country(string? raw)
        {
            var c = (raw ?? "").Trim();
            if (c.Length == 0) return "";
            if (c.Length == 2) return c.ToUpperInvariant();
            if (CountryCodes.TryGetValue(c, out var code) || CountryAliases.TryGetValue(c, out code)) return code;
            return c;
        }

        internal static string CountryName(string? key)
        {
            var c = (key ?? "").Trim();
            return c.Length == 2 && Countries.TryGetValue(c, out var n) ? n : c;
        }

        /// <summary>
        /// Genres as single words: TMDb's TV genres join two ("Action &amp; Adventure", "Sci-Fi &amp;
        /// Fantasy"), its movie genres do not ("Science Fiction"); both become the movie form.
        /// </summary>
        internal static IEnumerable<string> Genres(IEnumerable<string>? raw)
        {
            foreach (var g in raw ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                foreach (var part in g.Split(new[] { " & ", "&" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = part.Trim();
                    if (p.Length == 0) continue;
                    if (p.Equals("Sci-Fi", StringComparison.OrdinalIgnoreCase) || p.Equals("SciFi", StringComparison.OrdinalIgnoreCase)
                        || p.Equals("Science-Fiction", StringComparison.OrdinalIgnoreCase)) p = "Science Fiction";
                    yield return p;
                }
            }
        }

        /// <summary>
        /// Without TMDb's original_language (Emby's metadata search has no such field): the
        /// language a title's original title is written in, when its script tells it. Latin
        /// script tells nothing, so "".
        /// </summary>
        internal static string FromScript(string? title, IEnumerable<string>? countries)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            int han = 0, kana = 0, hangul = 0, thai = 0, arabic = 0, hebrew = 0, greek = 0, cyrillic = 0, devanagari = 0, tamil = 0, telugu = 0, letters = 0;
            foreach (var ch in title!)
            {
                if (!char.IsLetter(ch)) continue;
                letters++;
                if (ch >= 0x4E00 && ch <= 0x9FFF || ch >= 0x3400 && ch <= 0x4DBF) han++;
                else if (ch >= 0x3040 && ch <= 0x30FF) kana++;
                else if (ch >= 0xAC00 && ch <= 0xD7AF || ch >= 0x1100 && ch <= 0x11FF) hangul++;
                else if (ch >= 0x0E00 && ch <= 0x0E7F) thai++;
                else if (ch >= 0x0600 && ch <= 0x06FF) arabic++;
                else if (ch >= 0x0590 && ch <= 0x05FF) hebrew++;
                else if (ch >= 0x0370 && ch <= 0x03FF) greek++;
                else if (ch >= 0x0400 && ch <= 0x04FF) cyrillic++;
                else if (ch >= 0x0900 && ch <= 0x097F) devanagari++;
                else if (ch >= 0x0B80 && ch <= 0x0BFF) tamil++;
                else if (ch >= 0x0C00 && ch <= 0x0C7F) telugu++;
            }
            if (letters == 0) return "";
            var places = new HashSet<string>((countries ?? Enumerable.Empty<string>()).Select(Country), StringComparer.OrdinalIgnoreCase);
            if (kana > 0) return "ja";
            if (hangul > 0) return "ko";
            if (han > 0) return places.Contains("JP") && !places.Contains("CN") && !places.Contains("HK") && !places.Contains("TW") ? "ja" : "zh";
            if (thai > 0) return "th";
            if (hebrew > 0) return "he";
            if (greek > 0) return "el";
            if (devanagari > 0) return "hi";
            if (tamil > 0) return "ta";
            if (telugu > 0) return "te";
            if (arabic > 0) return places.Contains("IR") ? "fa" : "ar";
            if (cyrillic > 0) return places.Contains("UA") && !places.Contains("RU") ? "uk" : places.Contains("BG") ? "bg" : places.Contains("RS") ? "sr" : "ru";
            return "";
        }
    }
}
