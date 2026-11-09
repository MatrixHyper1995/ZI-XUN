using System.Text;
using Companion.Models;

namespace Companion.Services;

/// <summary>
/// 读 Data/stocks.csv 建内存索引（按市场分组）。
/// /api/suggest 和 名称→代码解析共用同一份索引；前缀匹配优先。
/// CSV 带引号（美股名称内含逗号），用正规引号解析。
/// </summary>
public class StockResolver
{
    private readonly Dictionary<string, List<StockInfo>> _byMarket;

    public StockResolver(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "Data", "stocks.csv");
        _byMarket = new Dictionary<string, List<StockInfo>>
        {
            ["cn"] = new(), ["hk"] = new(), ["us"] = new()
        };
        foreach (var row in ParseCsv(path))
        {
            if (row.Count < 3) continue;
            var code = row[0].Trim();
            var name = row[1].Trim();
            var market = row[2].Trim().ToLowerInvariant();
            var pinyin = row.Count > 3 ? row[3].Trim().ToLowerInvariant() : "";
            if (code.Length == 0 || name.Length == 0) continue;
            if (!_byMarket.TryGetValue(market, out var list)) continue;
            list.Add(new IndexedStock(code, name, market, pinyin));
        }
    }

    /// <summary>带拼音的内部记录；对外仍暴露 StockInfo。</summary>
    private sealed record IndexedStock(string Code, string Name, string Market, string Pinyin)
        : StockInfo(Code, Name, Market);

    /// <summary>自动补全：前缀匹配优先，取前 N 条。</summary>
    public List<SuggestItem> Suggest(string query, string market, int limit = 8)
    {
        if (!_byMarket.TryGetValue(market.ToLowerInvariant(), out var list))
            return new List<SuggestItem>();
        var q = query.Trim();
        if (q.Length == 0) return new List<SuggestItem>();
        var qUpper = q.ToUpperInvariant();
        var qLower = q.ToLowerInvariant();
        var qCode = NormalizeCode(q, market);   // 0700 → 00700，与 CSV 原始代码对齐

        var scored = new List<(int score, int nameLen, StockInfo s)>();
        foreach (var s in list.Cast<IndexedStock>())
        {
            int score;
            if (s.Code.Equals(qCode, StringComparison.OrdinalIgnoreCase) || s.Name == q)
                score = 0;                       // 精确命中
            else if (s.Code.StartsWith(qCode, StringComparison.OrdinalIgnoreCase))
                score = 1;                       // 代码前缀（已归一化）
            else if (s.Name.StartsWith(q, StringComparison.Ordinal))
                score = 2;                       // 名称前缀
            else if (s.Pinyin.Length > 0 && s.Pinyin.StartsWith(qLower, StringComparison.Ordinal))
                score = 3;                       // 拼音前缀
            else if (s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                score = 4;                       // 名称包含
            else if (s.Pinyin.Contains(qLower, StringComparison.Ordinal))
                score = 5;                       // 拼音包含
            else
                continue;
            scored.Add((score, s.Name.Length, s));
        }
        return scored
            .OrderBy(x => x.score).ThenBy(x => x.nameLen)
            .Take(limit)
            .Select(x => new SuggestItem(x.s.DisplayCode, x.s.Name, x.s.Market))
            .ToList();
    }

    /// <summary>名称/代码 → 股票。找不到返回 null。</summary>
    public StockInfo? Resolve(string query, string market)
    {
        if (!_byMarket.TryGetValue(market.ToLowerInvariant(), out var list))
            return null;
        var q = query.Trim();
        if (q.Length == 0) return null;

        // 归一化代码：0700.HK → 00700；600519.SH → 600519；aapl → AAPL
        var codeKey = NormalizeCode(q, market);
        var hit = list.FirstOrDefault(s =>
            s.Code.Equals(codeKey, StringComparison.OrdinalIgnoreCase) || s.Name == q);
        if (hit != null) return hit;

        // 退化：前缀 → 包含
        hit = list.FirstOrDefault(s =>
            s.Code.StartsWith(codeKey, StringComparison.OrdinalIgnoreCase) ||
            s.Name.StartsWith(q, StringComparison.Ordinal));
        hit ??= list.FirstOrDefault(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        return hit;
    }

    private static string NormalizeCode(string input, string market)
    {
        var c = input.Trim();
        var dot = c.IndexOf('.');
        if (dot >= 0) c = c[..dot];
        c = c.ToUpperInvariant();
        if (market == "hk" && c.All(char.IsDigit)) c = c.PadLeft(5, '0');
        if (market == "cn" && c.All(char.IsDigit)) c = c.PadLeft(6, '0');
        return c;
    }

    /// <summary>正规 CSV 解析：支持引号字段、内嵌逗号、"" 转义、字段内换行。</summary>
    private static IEnumerable<List<string>> ParseCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { /* skip */ }
            else if (ch == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        // 跳过 header
        if (rows.Count > 0 && rows[0].Count > 0 && rows[0][0].Trim().Equals("code", StringComparison.OrdinalIgnoreCase))
            rows.RemoveAt(0);
        return rows;
    }
}
