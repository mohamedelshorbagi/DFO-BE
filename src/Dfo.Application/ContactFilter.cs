using System.Text.RegularExpressions;

namespace Dfo.Application;

public record FilterResult(string Original, string Filtered, bool HasViolation, IReadOnlyList<string> Violations, string WarningMessage);

/// <summary>Server-side port of the frontend contact-filter.service.ts (anti-circumvention).</summary>
public static class ContactFilter
{
    public const string WarningEn = "For your safety, keep communication inside DFO (Direct From Owner).";
    public const string WarningAr = "لحمايتك وأمانك، يرجى إبقاء جميع المحادثات والمعاملات داخل منصة DFO (من المالك مباشرة).";
    private const string MaskEn = "[Contact information hidden for security]";
    private const string MaskAr = "[تم حجب وسيلة الاتصال لحمايتك]";

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
        ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
        ["صفر"] = "0", ["واحد"] = "1", ["اثنين"] = "2", ["تلاتة"] = "3", ["ثلاثة"] = "3",
        ["اربعة"] = "4", ["أربعة"] = "4", ["خمسة"] = "5", ["ستة"] = "6", ["سبعة"] = "7",
        ["تمانية"] = "8", ["ثمانية"] = "8", ["تسعة"] = "9"
    };

    private static readonly Regex Email = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"(https?://)?(www\.)?(wa\.me|t\.me|telegram\.me|whatsapp\.com|instagram\.com|facebook\.com|fb\.com)/\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Handle = new(@"@[a-zA-Z0-9_]{3,}", RegexOptions.Compiled);
    private static readonly Regex[] Triggers =
    [
        new(@"\b(call\s*me|phone\s*me|reach\s*me\s*at|contact\s*me\s*on)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(w/?a|whatsapp|viber|telegram|imo)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"(اتصل\s*بيا?|كلمني|رقمي|تليفوني|موبايلي|واتس(\s*اب)?|تليجرام)", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    ];
    private static readonly Regex Phone = new(
        @"(?:(?:\+|00)?[0-9٠-٩]{1,3}[\s.-]?)?(?:\(?[0-9٠-٩]{2,4}\)?[\s.-]?)?[0-9٠-٩]{3,4}[\s.-]?[0-9٠-٩]{3,4}(?:[\s.-]?[0-9٠-٩]{1,4})?",
        RegexOptions.Compiled);

    public static string NormalizeDigits(string text)
    {
        var chars = text.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c).ToArray();
        var s = new string(chars);
        foreach (var (word, digit) in NumberWords)
            s = Regex.Replace(s, $@"\b{Regex.Escape(word)}\b", digit, RegexOptions.IgnoreCase);
        return s;
    }

    public static FilterResult Filter(string? text, string lang = "en")
    {
        var warning = lang == "ar" ? WarningAr : WarningEn;
        if (string.IsNullOrEmpty(text)) return new("", "", false, [], warning);

        var mask = lang == "ar" ? MaskAr : MaskEn;
        var violations = new List<string>();
        var f = text;

        if (Email.IsMatch(f)) { violations.Add("Email address detected"); f = Email.Replace(f, mask); }
        if (Link.IsMatch(f)) { violations.Add("External messaging link detected"); f = Link.Replace(f, mask); }
        if (Handle.IsMatch(f)) { violations.Add("Social handle detected"); f = Handle.Replace(f, mask); }
        foreach (var t in Triggers)
            if (t.IsMatch(f)) { violations.Add("Contact solicitation phrase detected"); f = t.Replace(f, mask); }

        foreach (Match m in Phone.Matches(f))
        {
            var digits = NormalizeDigits(m.Value).Count(char.IsDigit);
            if (digits >= 7)
            {
                violations.Add("Phone number detected");
                f = f.Replace(m.Value, mask);
            }
        }

        var unique = violations.Distinct().ToList();
        return new(text, f, unique.Count > 0, unique, warning);
    }

    public static string Mask(string? text, string lang = "en") => Filter(text, lang).Filtered;
}
