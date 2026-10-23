using System.Text;
using System.Text.RegularExpressions;

namespace AM.Core.Rules;

/// <summary>
/// 源值规整（18 号"通用规整放代码里，不进规则表"）。规则行和源值都先过 <see cref="Normalize"/> 再比较，
/// 所以规则里写"S"能命中"ｓ"、"S "。
/// <see cref="Candidates"/> 再从源值里剥出可能的核心值，按从严到宽分三档，用 2026-10-23 的真实 1688 数据定的：
/// <list type="number">
/// <item>原值：规整后的整个源值。</item>
/// <item>去修饰：去掉括号内容（体重说明"(建议95-110斤)"）、前导编号（"1绿色"）、"有现货"、尺码后的"码"。</item>
/// <item>拆分：按"-"、"/"、"、"等拆成几段（"包边-粉色"），以及开头的尺码字母（"M80-95斤" 里的 M）。</item>
/// </list>
/// 规整不猜意思：剥不出东西或拆出的几段指向不同的值时，按未匹配处理。
/// </summary>
public static partial class SourceNormalizer
{
    /// <summary>全角转半角、统一大写、合并空白。</summary>
    public static string Normalize(string value)
    {
        var text = value.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        return Whitespace().Replace(text, " ");
    }

    /// <summary>三档候选，每档去重且不含前面档已有的值。第 3 档可能有多个。</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Candidates(string value)
    {
        var whole = Normalize(value);
        var cleaned = Clean(whole);

        var seen = new HashSet<string> { whole };
        var tier2 = cleaned.Length > 0 && seen.Add(cleaned) ? new[] { cleaned } : [];

        var tier3 = new List<string>();
        foreach (var piece in Separators().Split(cleaned).Select(p => p.Trim()))
        {
            AddIfNew(piece);
            if (SizeToken().Match(piece) is { Success: true } size)
            {
                AddIfNew(size.Value);
            }
        }

        if (SizeToken().Match(cleaned) is { Success: true } leading)
        {
            AddIfNew(leading.Value);
        }

        return [[whole], tier2, tier3];

        void AddIfNew(string candidate)
        {
            // 拆出来的纯数字是说明里的体重、裙长（"M 80-110斤"的 80），不是尺码或编码。整值是数字时在第 1 档已经试过。
            if (candidate.Length > 0 && !candidate.All(char.IsAsciiDigit) && seen.Add(candidate))
            {
                tier3.Add(candidate);
            }
        }
    }

    private static string Clean(string normalized)
    {
        var text = Brackets().Replace(normalized, "");
        text = LeadingNumber().Replace(text, "");
        text = text.Replace("有现货", "").Replace("现货", "");
        text = SizeSuffix().Replace(text, "");
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // 未闭合的括号也去到末尾，如"S(建议95斤内"
    [GeneratedRegex(@"[\(\[【][^\)\]】]*[\)\]】]?")]
    private static partial Regex Brackets();

    // 只去紧跟中文的编号，"2XL"不动
    [GeneratedRegex(@"^\d+(?=\p{IsCJKUnifiedIdeographs})")]
    private static partial Regex LeadingNumber();

    // "M码"→"M"，"均码"不动
    [GeneratedRegex(@"(?<=[A-Z0-9])码")]
    private static partial Regex SizeSuffix();

    [GeneratedRegex(@"[-_/、,;\s]+")]
    private static partial Regex Separators();

    // 开头的服装尺码字母：S、M、L、XS、XL、XXL、2XL、3XL……后面不能紧跟字母，免得"SRY"被当成 S
    [GeneratedRegex(@"^\d?X*[SML](?![A-Z])")]
    private static partial Regex SizeToken();
}
