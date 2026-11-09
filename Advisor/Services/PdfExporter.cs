using System.Text.RegularExpressions;
using Companion.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Companion.Services;

/// <summary>
/// QuestPDF 服务端生成报告。术语平铺（term: 解释），与网页的点击展开不同——打印页必须无需交互。
/// QuestPDF 社区版免费，启动时已声明 Community License。
/// </summary>
public class PdfExporter
{
    private const string FontFamily = "Microsoft YaHei";

    static PdfExporter()
    {
        // Windows 自带的微软雅黑，保证中文渲染
        var fontPath = @"C:\Windows\Fonts\msyh.ttc";
        if (File.Exists(fontPath))
            using (var fs = File.OpenRead(fontPath))
                QuestPDF.Drawing.FontManager.RegisterFontFromStream(fs);
    }

    private static readonly string[] Titles =
    {
        "① 这门生意赚的是不是真钱？（靠什么赚钱）",
        "② 现在这个价，买贵了没有？（贵不贵）",
        "③ 万一买错了，最坏亏多少？（最坏会怎样）"
    };

    public byte[] Export(ExportPdfRequest req)
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontFamily(FontFamily).FontSize(11).FontColor(Colors.Grey.Darken3));

                page.Header().Column(col =>
                {
                    col.Item().Text($"自询 · 买前三问 · {req.Stock.DisplayName}")
                        .FontSize(20).Bold().FontColor(Colors.Black);
                    col.Item().PaddingTop(4).Text($"{req.Stock.DisplayCode} · {date}")
                        .FontSize(10).FontColor(Colors.Grey.Medium);
                    col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingTop(14).Column(col =>
                {
                    for (var i = 0; i < req.Responses.Count && i < 3; i++)
                    {
                        var r = req.Responses[i];
                        var answer = i < req.Answers.Length ? (req.Answers[i] ?? "").Trim() : "";

                        col.Item().PaddingTop(i == 0 ? 0 : 16).Text(Titles[i]).FontSize(14).Bold();
                        if (answer.Length > 0)
                            col.Item().PaddingTop(6).Text(t =>
                            {
                                t.Span("我的回答：").Bold();
                                t.Span(answer);
                            });
                        col.Item().PaddingTop(6).Text(t =>
                        {
                            t.Span("AI 回应：").Bold();
                            t.Span(StripTermMarkers(r.Response));
                        });
                        foreach (var term in r.Terms)
                            col.Item().PaddingLeft(14).PaddingTop(3).Text(t =>
                            {
                                t.Span("· ").FontColor(Colors.Grey.Medium);
                                t.Span($"{term.Term}：").Bold();
                                t.Span(term.Plain);
                            });
                    }
                });

                page.Footer().Column(col =>
                {
                    col.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                    col.Item().PaddingTop(8).AlignCenter()
                        .Text("免责声明：本应用不提供买卖建议，仅供学习参考。")
                        .FontSize(10).FontColor(Colors.Grey.Darken1);
                });
            });
        }).GeneratePdf();
    }

    /// <summary>PDF 里去掉 [term] 的方括号标记和 ** 加粗标记，术语在下方平铺。</summary>
    private static string StripTermMarkers(string text) =>
        Regex.Replace(Regex.Replace(text, @"\[([^\[\]]+)\]", "$1"), @"\*\*([^*]+)\*\*", "$1");
}
