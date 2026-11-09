using System.Text.RegularExpressions;

namespace Companion.Models;

/// <summary>股票（CSV 原始代码 + 展示代码）</summary>
public record StockInfo(string Code, string Name, string Market)
{
    /// <summary>展示用代码：600519.SH / 0700.HK / AAPL</summary>
    public string DisplayCode => Market switch
    {
        "cn" => Code + (Code.StartsWith('6') ? ".SH" : ".SZ"),
        "hk" => Code.TrimStart('0').PadLeft(4, '0') + ".HK",
        _ => Code
    };

    /// <summary>展示用名称：美股剥掉 " - Common Stock" 之类的证券类型尾巴</summary>
    public string DisplayName => Market == "us" ? CleanUsName(Name) : Name;

    private static string CleanUsName(string name) =>
        Regex.Replace(name,
            @"[\s,–-]+(Class\s+[A-Z]\s+)?(Common\s+Stock|Common\s+Shares|Ordinary\s+Shares?|American\s+Depositary\s+Shares|ADS|Warrants?|Units?|Capital\s+Stock)\s*\.?\s*$",
            "", RegexOptions.IgnoreCase).Trim();
}

public record TermEntry(string Term, string Plain);

public record QuestionResponse(int Id, string Response, List<TermEntry> Terms);

public record AnalyzeRequest(string Query, string Market, string[] Answers);

public record AnalyzeResponse(StockInfo Stock, List<QuestionResponse> Responses, string Disclaimer);

public record SuggestItem(string Code, string Name, string Market);

public record ExportPdfRequest(StockInfo Stock, string[] Answers, List<QuestionResponse> Responses);
