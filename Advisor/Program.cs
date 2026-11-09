using Companion.Models;
using Companion.Services;
using QuestPDF.Infrastructure;

// QuestPDF 社区版（免费）许可声明
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
// 本地私有配置（含 ARTi key，随发布包走但被 .gitignore 排除）；环境变量优先
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Services.AddSingleton<StockResolver>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<ArtiClient>();
builder.Services.AddSingleton<PdfExporter>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

const string Disclaimer = "本应用不提供买卖建议，仅供学习参考。";

// 自动补全：前缀匹配优先
app.MapGet("/api/suggest", (string q, string market, StockResolver resolver) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.Ok(Array.Empty<SuggestItem>());
    return Results.Ok(resolver.Suggest(q, market ?? "hk"));
});

// 三问分析：输入 → 解析股票 → 构建 prompt → ARTi(mock) → 解析回应
app.MapPost("/api/analyze", async (AnalyzeRequest req, StockResolver resolver,
    PromptBuilder promptBuilder, ArtiClient arti) =>
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "请输入股票代码或名称" });

    var stock = resolver.Resolve(req.Query, req.Market ?? "hk");
    if (stock == null)
        return Results.NotFound(new { error = $"在{MarketName(req.Market)}没找到「{req.Query}」，换个写法试试" });

    var answers = req.Answers ?? Array.Empty<string>();
    var prompt = promptBuilder.Build(stock, answers);
    string json;
    try
    {
        json = await arti.GetResponsesJsonAsync(prompt, stock, answers);
    }
    catch (Exception ex)
    {
        return Results.Problem($"ARTi 调用失败：{ex.Message}", statusCode: 502);
    }
    List<QuestionResponse> responses;
    try
    {
        responses = ResponseParser.Parse(json);
    }
    catch (Exception ex)
    {
        var snippet = json.Length > 300 ? json[..300] : json;
        return Results.Problem($"ARTi 返回格式无法解析（{ex.Message}）。片段：{snippet}", statusCode: 502);
    }
    return Results.Ok(new AnalyzeResponse(stock, responses, Disclaimer));
});

// PDF 导出：术语平铺
app.MapPost("/api/export-pdf", (ExportPdfRequest req, PdfExporter exporter) =>
{
    var pdf = exporter.Export(req);
    var fileName = Uri.EscapeDataString($"自询-{req.Stock.Name}-{DateTime.Now:yyyyMMdd}.pdf");
    return Results.File(pdf, "application/pdf", fileName);
});

static string MarketName(string? m) => m switch { "cn" => "A股", "us" => "美股", _ => "港股" };

app.Run();
