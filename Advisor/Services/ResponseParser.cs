using System.Text.Json;
using System.Text.RegularExpressions;
using Companion.Models;

namespace Companion.Services;

/// <summary>
/// 解析 ARTi 返回的 JSON → 三问回应。
/// 容错：剥 ```json 代码栅栏、截取第一个 { 到最后一个 }、补齐/截断到 3 条。
/// </summary>
public static class ResponseParser
{
    public static List<QuestionResponse> Parse(string content)
    {
        var json = ExtractJson(content);
        using var doc = JsonDocument.Parse(json);
        var list = new List<QuestionResponse>();

        if (doc.RootElement.TryGetProperty("responses", out var arr))
        {
            var id = 0;
            foreach (var item in arr.EnumerateArray())
            {
                id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt32()
                    : id + 1;
                var response = item.TryGetProperty("response", out var rEl) ? rEl.GetString() ?? "" : "";
                var terms = new List<TermEntry>();
                if (item.TryGetProperty("terms", out var tArr))
                {
                    foreach (var t in tArr.EnumerateArray())
                    {
                        var term = t.TryGetProperty("term", out var te) ? te.GetString() ?? "" : "";
                        var plain = t.TryGetProperty("plain", out var pl) ? pl.GetString() ?? "" : "";
                        if (term.Length > 0) terms.Add(new TermEntry(term, plain));
                    }
                }
                list.Add(new QuestionResponse(id, response, terms));
            }
        }

        // 硬保证：恰好 3 条，id 1..3；JSON schema 里没有"建议/结论"字段可填
        list = list.OrderBy(r => r.Id).Take(3).ToList();
        while (list.Count < 3)
            list.Add(new QuestionResponse(list.Count + 1, "（本条回应解析失败，请重试。）", new List<TermEntry>()));

        // 兜底：模型常把术语解释直接写在正文 [术语]（解释）里而留空 terms 表——从正文推导
        return list.Select(DeriveTermsFromText).ToList();
    }

    private static readonly Regex InlineTerm =
        new(@"\[([^\[\]]{1,30})\][（(]([^（）()]{1,120})[）)]", RegexOptions.Compiled);

    private static QuestionResponse DeriveTermsFromText(QuestionResponse r)
    {
        var known = new HashSet<string>(r.Terms.Select(t => t.Term));
        var terms = new List<TermEntry>(r.Terms);
        var text = InlineTerm.Replace(r.Response, m =>
        {
            if (known.Add(m.Groups[1].Value))
                terms.Add(new TermEntry(m.Groups[1].Value, m.Groups[2].Value));
            return $"[{m.Groups[1].Value}]";   // 摘掉紧跟的内联解释，交给点击展开
        });
        return r with { Response = text, Terms = terms };
    }

    private static string ExtractJson(string content)
    {
        var s = content.Trim();
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl >= 0) s = s[(firstNl + 1)..];
            if (s.TrimEnd().EndsWith("```")) s = s.TrimEnd()[..^3];
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start >= 0 && end > start) s = s[start..(end + 1)];
        return s;
    }
}
