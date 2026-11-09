using System.Text;
using System.Text.Json;
using Companion.Models;

namespace Companion.Services;

/// <summary>
/// 调 ARTi API（OpenAI 兼容 chat/completions）。
/// Phase 1 占位：ARTI_API_KEY 未配置时走内置 mock，返回与真实 API 同构的 JSON 字符串，
/// 由 ResponseParser 走真实解析路径。Phase 2 只需补 RealCallAsync 的实现。
/// </summary>
public class ArtiClient
{
    private readonly HttpClient _http = new();
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;

    public ArtiClient(IConfiguration config)
    {
        _endpoint = config["Arti:Endpoint"]
            ?? "https://api.artifin.ai/functions/v1/public-api/v1/chat/completions";
        _model = config["Arti:Model"] ?? "arti-chat";
        // Key 优先级：环境变量 ARTI_API_KEY > appsettings.Local.json（随包发布）
        var fromEnv = Environment.GetEnvironmentVariable("ARTI_API_KEY");
        var fromConfig = config["Arti:ApiKey"];
        _apiKey = !string.IsNullOrEmpty(fromEnv) ? fromEnv
            : !string.IsNullOrEmpty(fromConfig) ? fromConfig
            : null;
    }

    public bool IsMock => string.IsNullOrEmpty(_apiKey);

    /// <summary>返回 ARTi message content（JSON 字符串）。</summary>
    public async Task<string> GetResponsesJsonAsync(string prompt, StockInfo stock, string[] answers)
    {
        if (IsMock)
        {
            // 模拟网络延迟，让 loading 态可感知
            await Task.Delay(600);
            return MockResponses.BuildJson(stock, answers);
        }
        return await RealCallAsync(prompt);
    }

    /// <summary>Phase 2：真实 ARTi 调用（OpenAI 兼容）。模板里的 System:/User: 拆成两条消息。</summary>
    private async Task<string> RealCallAsync(string prompt)
    {
        object[] messages;
        const string marker = "\nUser:";
        var idx = prompt.IndexOf(marker, StringComparison.Ordinal);
        if (prompt.StartsWith("System:") && idx > 0)
        {
            var system = prompt["System:".Length..idx].Trim();
            var user = prompt[(idx + marker.Length)..].Trim();
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            };
        }
        else
        {
            messages = new object[] { new { role = "user", content = prompt } };
        }

        var body = new { model = _model, messages };
        var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");
        // SSE 流式读：响应头一到就开始读，逐行拼接 delta.content，到 [DONE] 为止
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"ARTi {(int)resp.StatusCode}: {errBody[..Math.Min(300, errBody.Length)]}");
        }
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var sb = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;
            if (payload.Length == 0) continue;
            using var chunk = JsonDocument.Parse(payload);
            if (chunk.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content))
            {
                sb.Append(content.GetString());
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Phase 1 mock 数据：按【三步骨架】（主观先行 → 数据纠偏 → 落到纸面）组织，
/// 每条必引具体数字、术语用 [term] 包裹、以追问收尾、绝无买卖结论。
/// </summary>
internal static class MockResponses
{
    public static string BuildJson(StockInfo stock, string[] answers)
    {
        var pack = Lookup(stock);
        var responses = new List<object>();
        for (var i = 0; i < 3; i++)
        {
            var userAnswer = i < answers.Length ? (answers[i] ?? "").Trim() : "";
            var (text, terms) = pack[i];
            var body = string.IsNullOrEmpty(userAnswer)
                ? $"这题你留空了，我把完整分析讲一遍。{text}"
                : $"你先说了「{userAnswer}」——{pack.Openers[i]}{text}";
            responses.Add(new
            {
                id = i + 1,
                response = body,
                terms = terms.Select(t => new { term = t.Term, plain = t.Plain }).ToList()
            });
        }
        return JsonSerializer.Serialize(new { responses });
    }

    private class Pack : List<(string Text, List<TermEntry> Terms)>
    {
        public string[] Openers { get; set; } = new string[3];
    }

    /// <summary>组装一个 Pack：三句"主观先行"开场白 + 三条（回应文本, 术语表）</summary>
    private static Pack MakePack(string[] openers, params (string Text, List<TermEntry> Terms)[] items)
    {
        var p = new Pack { Openers = openers };
        p.AddRange(items);
        return p;
    }

    private static Pack Lookup(StockInfo stock) => stock.Code switch
    {
        "00700" => Tencent,
        "600519" => Moutai,
        "AAPL" => Apple,
        "TSLA" => Tesla,
        _ => Generic(stock)
    };

    private static readonly Pack Tencent = MakePack(
        new[]
        {
            "游戏确实是它的招牌，这个大方向抓得准。不过咱们拿财报数字对一下——",
            "「贵不贵」你已经有自己的直觉了，这很好。直觉要落到数字上才算数——",
            "能先想到「什么情况下会亏钱」，这个习惯比多数新手强。咱们把最坏情形拆细——"
        },
        (
            "先看事实：腾讯 2024 年营收 6603 亿元，其中增值服务（主要是[游戏]）占约 49%，营销服务（[广告]）占约 18%，[金融科技与企业服务]占约 32%。所以「靠游戏赚钱」只对了一半——真正的结构是三条腿。再看赚的是不是真钱：全年[毛利率]约 53%，非国际准则净利润 2227 亿元，[净利率]约 34%，经营现金流超 2000 亿元，是实打实的现金。一句话落到纸面：「腾讯是社交地基上的三引擎现金机器，利润率高、现金流厚」。追问一句：如果明天游戏收入零增长，另外两条腿撑不撑得起 6600 亿的盘子？",
            Terms(
                T("游戏", "网络游戏业务，腾讯现金流最稳的板块"),
                T("广告", "微信、视频号里的商业广告，按曝光/点击收费"),
                T("金融科技与企业服务", "支付、理财、云计算等面向企业的业务"),
                T("毛利率", "（收入-成本）÷收入，反映生意本身多赚钱"),
                T("净利率", "净利润÷收入，反映公司整体赚钱效率"))
        ),
        (
            "纠正一个常见错觉：「大公司一定贵」不成立。以近期约 400～600 港元区间计，腾讯[市盈率]（PE）约 20 倍上下，低于自身过去 5 年约 30 倍的均值；[股息率]约 1%，加上每年数百亿港元的[回购]。换句话说，当前价格里「提前定价」的预期并不算激进。落到纸面：「估值处于自身历史中枢偏下，市场给它的预期不算贵」——注意这句话可以用 PE 历史分位自己去验证。追问：PE 低一定代表便宜吗？如果利润明年下滑，同样的价格 PE 会变成多少？",
            Terms(
                T("市盈率", "股价÷每股盈利，最常用的「贵不贵」标尺"),
                T("股息率", "每股分红÷股价，衡量分红的直接回报"),
                T("回购", "公司用现金买回自己的股票，提升每股含金量"))
        ),
        (
            "最坏情形拆三层。第一层花钱：AI 相关[资本开支]大幅上调，若 AI 收入兑现不及预期，[自由现金流]会被拖累。第二层结构：游戏占收入近一半，受[版号]审批和竞品影响；广告又跟着宏观消费走。第三层情绪：现在约 20 倍 PE 里含「AI 能兑现」的期望，若落空，估值可能向历史低位（约 12～15 倍）回撤，对应三成左右的下行空间。缓冲是经营现金流超 2000 亿元、负债健康，最坏更像「估值回撤+增速放缓」，而不是「现金流断裂」。落到纸面：「最坏情形≈估值砍三成，而不是生意垮掉」。追问：三成的回撤，你的心态和资金计划扛得住吗？",
            Terms(
                T("资本开支", "为未来扩张（买芯片、建数据中心）花的钱"),
                T("自由现金流", "赚到的现金扣掉必要开支后可自由支配的钱"),
                T("版号", "游戏在中国上线必须拿到的政府审批牌照"))
        )
    );

    private static readonly Pack Moutai = MakePack(
        new[]
        {
            "「卖酒赚钱」说到点子上了。这门生意的质地，看数字更直观——",
            "你对「贵不贵」有感觉，咱们用数字校准一下这个感觉——",
            "愿意先想最坏情形，这是成熟投资者的习惯。具体到茅台——"
        },
        (
            "茅台的模式极简：把高端白酒卖给经销商和消费者，核心是[飞天茅台]这一个单品。数字说话：2024 年营收约 1741 亿元，同比增长约 15%；[毛利率]常年在 91% 以上，[净利率]约 52%——卖 100 块钱的酒，净赚 52 块。更难得的是[预收款]模式：经销商先打款后提货，经营现金流常年高于净利润，是教科书级的[现金牛]。落到纸面：「这是一门先收钱后发货、净利率过半的生意」。追问：一瓶酒毛利率 91%，靠的是什么别人学不走的东西？",
            Terms(
                T("飞天茅台", "茅台最核心的高端白酒单品，贡献绝大部分利润"),
                T("毛利率", "（收入-成本）÷收入，反映产品本身多赚钱"),
                T("净利率", "净利润÷收入，反映公司整体赚钱效率"),
                T("预收款", "客户先打款、公司后发货，等于客户帮公司垫钱"),
                T("现金牛", "能持续稳定产生大量现金的业务"))
        ),
        (
            "纠个偏：「好公司=任何时候都便宜」不成立。茅台[市盈率]（PE）高点时超 70 倍，当前约 20 倍出头，处于自身历史低位区间；[股息率]约 3%～4%，分红比例还在提高。也就是说，现在的价格比过去几年大多数时候都便宜——但「比过去便宜」和「绝对便宜」是两回事，20 倍 PE 意味着市场仍预期它能长期增长。落到纸面：「当前估值在自身历史低位，但隐含了持续增长的假设」。追问：如果增速从 15% 降到 5%，20 倍 PE 还撑得住吗？",
            Terms(
                T("市盈率", "股价÷每股盈利，最常用的「贵不贵」标尺"),
                T("股息率", "每股分红÷股价，衡量分红的直接回报"))
        ),
        (
            "最坏情形看三条线。第一条需求线：白酒整体[去库存]，商务宴请和送礼需求走弱，飞天茅台批价从高点回落——若批价持续下行，说明真实需求在变软。第二条政策线：限制公款消费、消费税改革等政策变化会直接压估值。第三条人群线：年轻人白酒消费意愿下降是慢变量，但十年维度上是真问题。缓冲在于：91% 的[毛利率]和先款后货的模式让它几乎没有资金链风险，最坏是「增长失速+估值下移」，不是「公司出事」。落到纸面：「茅台的最坏情形是赚得变慢，而不是亏钱倒闭」。追问：你能接受一只「公司没事、但股价阴跌三年」的股票吗？",
            Terms(
                T("去库存", "渠道里积压的货被慢慢消化，库存高会压价格"),
                T("毛利率", "（收入-成本）÷收入，反映产品本身多赚钱"))
        )
    );

    private static readonly Pack Apple = MakePack(
        new[]
        {
            "「卖 iPhone 的」这个印象没错，但只看到了一半。看数字——",
            "「贵不贵」不能凭感觉，咱们把它拆成数字——",
            "先想最坏情形，这个习惯很好。苹果的最坏情形长这样——"
        },
        (
            "苹果两条腿走路：硬件（iPhone、Mac、iPad）和[服务业务]（App Store 分成、iCloud、订阅）。2024 财年营收约 3910 亿美元，其中 iPhone 约占 51%，服务约占 25%。关键在利润结构：硬件[毛利率]约 37%，服务毛利率约 74%——服务收入只占四分之一，却贡献了毛利的大头。整体净利率约 24%，年[自由现金流]约 1000 亿美元。落到纸面：「硬件是入口，服务才是利润的奶牛」。追问：如果 iPhone 销量五年不涨，服务收入还能不能继续长大？",
            Terms(
                T("服务业务", "App Store、iCloud、Apple Music 等订阅和分成收入"),
                T("毛利率", "（收入-成本）÷收入，反映业务本身多赚钱"),
                T("自由现金流", "赚到的现金扣掉必要开支后可自由支配的钱"))
        ),
        (
            "数字摆在这：苹果[市盈率]（PE）约 30 倍上下，高于自身十年均值（约 20 倍），也高于多数硬件同行。市场愿意给[溢价]，买的是生态锁定和每年上千亿美元的[回购]。但要注意：30 倍 PE 对应的是「增长会持续」的预期，而苹果近年营收增速只有个位数——价格和预期之间的缝，就是「贵不贵」的核心。落到纸面：「当前价格不便宜，买的是确定性和回购，不是高增长」。追问：一家增速 5% 的公司，凭什么值 30 倍 PE？",
            Terms(
                T("市盈率", "股价÷每股盈利，最常用的「贵不贵」标尺"),
                T("溢价", "价格高出「合理值」的部分"),
                T("回购", "公司用现金买回自己的股票，提升每股含金量"))
        ),
        (
            "最坏情形有三层。第一层集中度：iPhone 占收入过半，一代新品翻车就直接打在营收上。第二层地缘：中国市场收入约占 19%，面临本土品牌竞争和监管双重压力。第三层估值：约 30 倍 PE 建在「服务持续增长」的预期上，一旦增速证伪，估值向 20 倍历史均值回归就意味着三成左右的回撤。缓冲是每年约 1000 亿美元的[自由现金流]和巨额[回购]。落到纸面：「最坏是估值均值回归，不是生意崩盘」。追问：三成回撤发生在苹果身上时，市场会讲一个什么故事？你会信吗？",
            Terms(
                T("自由现金流", "赚到的现金扣掉必要开支后可自由支配的钱"),
                T("回购", "公司用现金买回自己的股票，提升每股含金量"))
        )
    );

    private static readonly Pack Tesla = MakePack(
        new[]
        {
            "「卖电动车的」是事实，但市场给它讲的故事远不止卖车。拆开看——",
            "「贵不贵」在特斯拉身上是最难答的一题。先把数字摆出来——",
            "愿意想最坏情形很好——特斯拉恰好是最需要想这题的股票之一——"
        },
        (
            "先看真实生意：2024 年营收约 977 亿美元，其中汽车业务占约 82%，能源和储能占约 10%。但[毛利率]只有约 18%，[净利率]约 7%——和丰田处在同一量级，远低于科技公司。市场给它科技公司的估值，赌的是[自动驾驶]和机器人这些还没贡献主要收入的业务。落到纸面：「当下的特斯拉是一门利润率普通的车生意，估值里装的是未来的故事」。追问：如果把自动驾驶的想象全部去掉，只按车企看，它该值多少钱？",
            Terms(
                T("毛利率", "（收入-成本）÷收入，反映生意本身多赚钱"),
                T("净利率", "净利润÷收入，反映公司整体赚钱效率"),
                T("自动驾驶", "让汽车自己开的技术，特斯拉最大的想象空间"))
        ),
        (
            "这题的数字很极端：特斯拉[市盈率]（PE）常年在 50 倍以上，是丰田（约 7 倍）的七八倍。这意味着价格里「提前定价」了大量未来增长——买的不是现在的利润，是[自动驾驶]、机器人出租车这些期权的兑现。历史经验是：一旦交付数据或毛利率不及预期，股价单季度回撤 30% 并不罕见。落到纸面：「估值高度依赖故事兑现，波动本身就是它的估值特征」。追问：你买它的理由，是基于财报里的数字，还是基于对未来的想象？这两者要分的钱不一样。",
            Terms(
                T("市盈率", "股价÷每股盈利，最常用的「贵不贵」标尺"),
                T("自动驾驶", "让汽车自己开的技术，特斯拉最大的想象空间"))
        ),
        (
            "特斯拉的最坏情形要单独认真对待。第一层竞争：全球电动车价格战直接压[毛利率]，已从 25% 以上降到 18% 左右。第二层兑现风险：50 倍以上的[市盈率]全靠故事撑着，[自动驾驶]落地一旦再推迟，估值可以向传统车企方向剧烈回归——历史上它有过单年腰斩再腰斩的记录。第三层关键人：管理层个人言行和精力分散是这家公司独有的风险项。缓冲是账上现金充裕、几乎无有息负债。落到纸面：「最坏情形是估值砍半以上，且这种事在历史上真实发生过」。追问：如果买入后跌 50%，你的计划是什么——写不出具体答案，说明还没准备好。",
            Terms(
                T("毛利率", "（收入-成本）÷收入，反映生意本身多赚钱"),
                T("市盈率", "股价÷每股盈利，最常用的「贵不贵」标尺"),
                T("自动驾驶", "让汽车自己开的技术，特斯拉最大的想象空间"))
        )
    );

    /// <summary>未收录股票的通用 mock：仍按三步骨架，数字为明确标注的示例。</summary>
    private static Pack Generic(StockInfo stock) => MakePack(
        new[]
        {
            "你能先用自己的话描述它的生意，这一步就比多数新手强。对照示例数字校准一下——",
            "对「贵不贵」有直觉是好事，直觉要用数字检验——",
            "能先想「什么情况下亏钱」，这个习惯值得保持。展开看——"
        },
        (
            $"（以下为演示用示例数据）以「{stock.Name}」为例，看懂一门生意抓三个数字：收入从哪来（各业务占比）、[毛利率]和[净利率]是多少、经营现金流是否为正。一家健康的公司，比如年营收 500 亿元、净利率 15%、经营现金流 60 亿元，说明它每收 100 元能净落 15 元、且利润有现金支撑。落到纸面：「先把营收结构、净利率、现金流三个数字找出来，再说自己看没看懂这门生意」。追问：它的收入里，最大的一块来自哪条业务线？",
            Terms(
                T("毛利率", "（收入-成本）÷收入，反映生意本身多赚钱"),
                T("净利率", "净利润÷收入，反映公司整体赚钱效率"))
        ),
        (
            $"（以下为演示用示例数据）判断贵不贵，先把「{stock.Name}」的[市盈率]（PE）找出来，再做两个对比：和自身过去 5 年的 PE 区间比，和同行业对手比。比如 PE 25 倍、自身历史区间 15～35 倍、同行均值 30 倍——说明处于自身中枢、比同行略便宜。再看[股息率]和[回购]有没有持续。落到纸面：「贵不贵=当前 PE 在历史区间里的位置+和同行的差」。追问：这个 PE 数字背后，市场假设了它未来几年增长多少？",
            Terms(
                T("市盈率", "股价÷每股盈利，最常用的「贵不贵」标尺"),
                T("股息率", "每股分红÷股价，衡量分红的直接回报"),
                T("回购", "公司用现金买回自己的股票，提升每股含金量"))
        ),
        (
            $"（以下为演示用示例数据）最坏情形分三层想：钱（[自由现金流]是否持续为正、有息负债高不高）、结构（收入是否依赖单一客户或产品）、情绪（当前[市盈率]里含了多少乐观预期）。举个具体情形：若 PE 从 30 倍回到行业均值 20 倍，即使利润不变，股价也要回撤约三分之一。落到纸面：「最坏能亏多少=估值回撤幅度×基本面恶化幅度，先算出来再决定买多少」。追问：跌三成时，你的应对动作是什么？写下来。",
            Terms(
                T("自由现金流", "赚到的现金扣掉必要开支后可自由支配的钱"),
                T("市盈率", "股价÷每股盈利，最常用的「贵不贵」标尺"))
        )
    );

    private static TermEntry T(string term, string plain) => new(term, plain);
    private static List<TermEntry> Terms(params TermEntry[] ts) => ts.ToList();
}
