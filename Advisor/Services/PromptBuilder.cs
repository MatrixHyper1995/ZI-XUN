using Companion.Models;

namespace Companion.Services;

/// <summary>
/// 读 Prompts/three-questions.txt 模板，填充 {STOCK}/{MARKET}/{ANSWER1..3}。
/// Prompt 与代码分离——模板改了不用重编译。
/// </summary>
public class PromptBuilder
{
    private readonly string _template;

    public PromptBuilder(IWebHostEnvironment env)
    {
        _template = File.ReadAllText(
            Path.Combine(env.ContentRootPath, "Prompts", "three-questions.txt"));
    }

    public string Build(StockInfo stock, string[] answers)
    {
        var marketName = stock.Market switch
        {
            "cn" => "A股",
            "hk" => "港股",
            "us" => "美股",
            _ => stock.Market
        };
        var a = answers ?? Array.Empty<string>();
        string At(int i) => i < a.Length && !string.IsNullOrWhiteSpace(a[i]) ? a[i] : "（留空）";

        return _template
            .Replace("{STOCK}", $"{stock.Name}（{stock.DisplayCode}）")
            .Replace("{MARKET}", marketName)
            .Replace("{ANSWER1}", At(0))
            .Replace("{ANSWER2}", At(1))
            .Replace("{ANSWER3}", At(2));
    }
}
