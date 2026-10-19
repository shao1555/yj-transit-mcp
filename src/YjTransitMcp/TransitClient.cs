using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace YjTransitMcp;

/// <summary>
/// Yahoo!乗換案内（transit.yahoo.co.jp）の検索結果ページと駅名サジェストを取得して解析するクライアント。
/// 検索結果ページは Next.js 製で、表示内容がすべて __NEXT_DATA__ の JSON に入っているのでそれを読む。
/// </summary>
public sealed class TransitClient(IHttpClientFactory httpClientFactory, ILogger<TransitClient> logger)
{
    public const string HttpClientName = "yahoo-transit";
    public const string BaseUrl = "https://transit.yahoo.co.jp/";

    // 同じ条件の検索がすぐに繰り返されることがあるので、短時間だけキャッシュする
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim Throttle = new(2);
    private static readonly HtmlParser Parser = new();
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, string Body)> _cache = new();

    public static DateTime NowJst() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Jst).DateTime;

    // ---- 経路検索 ----

    public async Task<RoutePage> SearchRouteAsync(RouteQuery query, CancellationToken ct)
    {
        string html;
        try
        {
            html = await GetStringAsync(BuildSearchPath(query), ct);
        }
        catch (SearchRejectedException ex)
        {
            throw new McpException(await DescribeRejectionAsync(ex.Code, query, ct));
        }
        return ParseRoutePage(html);
    }

    internal static string BuildSearchPath(RouteQuery q)
    {
        var p = new List<(string Key, string Value)>
        {
            ("from", q.From),
            ("to", q.To),
        };
        p.AddRange(q.Via.Select(v => ("via", v)));
        p.AddRange(
        [
            ("y", q.DateTime.Year.ToString(CultureInfo.InvariantCulture)),
            ("m", q.DateTime.Month.ToString("00", CultureInfo.InvariantCulture)),
            ("d", q.DateTime.Day.ToString("00", CultureInfo.InvariantCulture)),
            ("hh", q.DateTime.Hour.ToString(CultureInfo.InvariantCulture)),
            // 分は十の位（m1）と一の位（m2）に分けて渡す
            ("m1", (q.DateTime.Minute / 10).ToString(CultureInfo.InvariantCulture)),
            ("m2", (q.DateTime.Minute % 10).ToString(CultureInfo.InvariantCulture)),
            ("type", q.TimeType switch
            {
                TimeType.Departure => "1",
                TimeType.LastTrain => "2",
                TimeType.FirstTrain => "3",
                TimeType.Arrival => "4",
                _ => "5",
            }),
            ("ticket", q.Ticket == TicketType.Cash ? "normal" : "ic"),
            ("expkind", q.Seat switch { SeatPreference.Reserved => "2", SeatPreference.Green => "3", _ => "1" }),
            ("ws", q.WalkSpeed switch { WalkSpeed.Fast => "1", WalkSpeed.SlightlyFast => "2", WalkSpeed.Slow => "4", _ => "3" }),
            ("s", q.Sort switch { SortOrder.Fare => "1", SortOrder.Transfer => "2", _ => "0" }),
            ("al", Flag(q.UseAirline)),
            ("shin", Flag(q.UseShinkansen)),
            ("ex", Flag(q.UseExpress)),
            ("hb", Flag(q.UseHighwayBus)),
            ("lb", Flag(q.UseLocalBus)),
            ("sr", Flag(q.UseFerry)),
        ]);
        return "search/result?" + string.Join("&", p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        static string Flag(bool b) => b ? "1" : "0";
    }

    internal static RoutePage ParseRoutePage(string html)
    {
        var json = Parser.ParseDocument(html).GetElementById("__NEXT_DATA__")?.TextContent;
        var navi = json is null ? null : JsonSerializer.Deserialize<NextData>(json, JsonOptions)?.Props?.PageProps?.NaviSearchParam;
        if (navi?.DisplayInfo is null)
            throw new McpException("検索結果を読み取れませんでした（Yahoo!乗換案内のページ構成が変わった可能性があります）。");

        var routes = (navi.FeatureInfoList ?? [])
            .Where(f => f.SummaryInfo is not null && f.EdgeInfoList is { Count: >= 2 })
            .Select(f => ToRoute(f.SummaryInfo!, f.EdgeInfoList!))
            .ToList();

        return new RoutePage(
            navi.DisplayInfo.FromName ?? "",
            navi.DisplayInfo.ToName ?? "",
            navi.AroundInfo?.SearchTypeName,
            navi.AroundInfo?.SearchYMDWText,
            navi.AroundInfo?.SearchHMText,
            routes,
            Candidates(navi.OtherQueryInfo?.FromList),
            Candidates(navi.OtherQueryInfo?.ToList));

        static IReadOnlyList<string> Candidates(List<CandidateDto>? list) =>
            list?.Select(c => c.Name).OfType<string>().Where(n => n.Length > 0).ToList() ?? [];
    }

    private static Route ToRoute(SummaryDto s, List<EdgeDto> edges)
    {
        // edge は経路上の地点（出発地・乗換駅・到着地）で、その地点から次の地点までに乗る路線を持っている
        var segments = new List<Segment>();
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var e = edges[i];
            var next = edges[i + 1];
            var ride = e.RidingPositionInfo;
            var price = e.PriceInfo;
            var kind = SegmentKind(e);
            var exit = string.Join(" ", new[] { e.ExitName?.From, e.ExitName?.To }.Where(x => !string.IsNullOrWhiteSpace(x)));
            segments.Add(new Segment(
                Kind: kind,
                Line: NullIfEmpty(e.RailNameExcludingDestination) ?? e.RailName ?? "",
                Destination: NullIfEmpty(e.Destination),
                From: new StopPoint(e.StationName ?? "", TimeOf(e, departure: true), Platform(ride?.Departure)),
                To: new StopPoint(next.StationName ?? "", TimeOf(next, departure: false), Platform(ride?.Arrival)),
                Stops: (e.StopStationList ?? []).Select(st => new StopPoint(st.Name ?? "", NullIfEmpty(st.DepartureTime))).ToList(),
                CarPosition: ride?.Position is { Count: > 0 } pos ? string.Join(" / ", pos) : null,
                Express: price is not null && ParsePrice(price.ExpPrice) is int fee and > 0
                    ? new ExpressFee(NullIfEmpty(price.ExpType) ?? "特別料金", fee)
                    : null,
                Exit: NullIfEmpty(exit),
                Notice: NullIfEmpty(e.PreCautionalComment),
                ServiceInfo: e.DiaInfoStatus is { Count: > 0 } dia ? dia : null));
        }

        // 乗車券の運賃は edgeGroup（"{最初の区間}_{最後の区間}"）ごとに、その範囲の各 edge へ同じ値が入っている
        var fares = new List<FareSection>();
        var seen = new HashSet<string>();
        foreach (var e in edges)
        {
            var group = e.PriceInfo?.EdgeGroup;
            if (group is null || !seen.Add(group) || ParsePrice(e.PriceInfo!.Price) is not { } fare || fare == 0) continue;
            var range = group.Split('_');
            if (range.Length != 2 || !int.TryParse(range[0], out var first) || !int.TryParse(range[1], out var last)) continue;
            if (first < 0 || last + 1 >= edges.Count) continue;
            fares.Add(new FareSection(edges[first].StationName ?? "", edges[last + 1].StationName ?? "", fare));
        }

        var tags = new List<string>();
        if (s.IsFast) tags.Add("早");
        if (s.IsCheap) tags.Add("安");
        if (s.IsEasy) tags.Add("楽");

        var pass = (s.Teiki ?? [])
            .Select(t => (Month: t.Month, Price: ParsePrice(t.Price)))
            .Where(t => t.Month is not null && t.Price is > 0)
            .ToDictionary(t => t.Month + "か月", t => t.Price!.Value);

        return new Route(
            s.DepartureTime ?? "",
            s.ArrivalTime ?? "",
            s.TotalTime ?? "",
            NullIfEmpty(s.TimeOnBoard),
            int.TryParse(s.TransferCount, out var transfers) ? transfers : 0,
            ParsePrice(s.TotalPrice),
            // 「（乗車券8,910円　特別料金5,810円）」→「乗車券8,910円 特別料金5,810円」
            NullIfEmpty(s.TotalPriceDetail?.Trim('（', '）', '(', ')').Replace('　', ' ')),
            NullIfEmpty(s.Distance),
            tags,
            segments,
            fares,
            pass.Count > 0 ? pass : null);
    }

    private static string SegmentKind(EdgeDto e)
    {
        var classes = e.AccessInfo?.ClassNameList ?? [];
        if (classes.Contains("walk") || (e.RailName ?? "").StartsWith("徒歩", StringComparison.Ordinal)) return "walk";
        if (classes.Contains("air")) return "air";
        if (classes.Contains("ship")) return "ship";
        return e.AccessInfo?.Type switch
        {
            0 => "train",
            2 => "bus",
            3 => "highway_bus",
            4 => "ship",
            5 => "air",
            _ => "other",
        };
    }

    /// <summary>timeInfo の type は 1 = 発、2 = 着、3 = 出発地・到着地の時刻。</summary>
    private static string? TimeOf(EdgeDto e, bool departure)
    {
        var times = e.TimeInfo ?? [];
        var t = times.FirstOrDefault(x => x.Type == (departure ? 1 : 2)) ?? times.FirstOrDefault(x => x.Type == 3);
        return NullIfEmpty(t?.Time);
    }

    /// <summary>["10", "番線"] → "10番線"。番号がないもの（["", "情報なし"] など）は null。</summary>
    private static string? Platform(List<string?>? p) =>
        p is { Count: >= 2 } && !string.IsNullOrWhiteSpace(p[0]) && p[0] != "0" ? p[0] + p[1] : null;

    // ---- 検索できなかったとき ----

    private sealed class SearchRejectedException(string code) : Exception($"search rejected: {code}")
    {
        public string Code { get; } = code;
    }

    /// <summary>トップページへのリダイレクトに付く cmd の値を、サイトと同じ文言の説明にする。地点が見つからないときは候補を添える。</summary>
    private async Task<string> DescribeRejectionAsync(string code, RouteQuery q, CancellationToken ct)
    {
        var via = string.Join("、", q.Via);
        (string Message, string[] SuggestFor) r = code switch
        {
            "4011" => ($"「{q.From}」に該当する出発地はありませんでした。", [q.From]),
            "4012" => ($"「{q.To}」に該当する目的地はありませんでした。", [q.To]),
            "4013" => ($"特定できない経由駅がありました（{via}）。", [.. q.Via]),
            "4014" => ($"「{q.From}」または「{q.To}」に該当する地点がありませんでした。", [q.From, q.To]),
            "4019" => ($"「{q.From}」と経由駅（{via}）に該当する地点がありませんでした。", [q.From, .. q.Via]),
            "4020" => ($"「{q.To}」と経由駅（{via}）に該当する地点がありませんでした。", [q.To, .. q.Via]),
            "4021" => ("出発地・目的地・経由駅のいずれにも該当する地点がありませんでした。", [q.From, q.To, .. q.Via]),
            "4015" => ($"出発地「{q.From}」を特定できませんでした。", [q.From]),
            "4016" => ($"目的地「{q.To}」を特定できませんでした。", [q.To]),
            "4017" => ($"出発地「{q.From}」と目的地「{q.To}」を特定できませんでした。", [q.From, q.To]),
            "4018" or "same" => ("出発地と目的地が同じ地点です。", []),
            "4030" => ("ご指定の条件では、時刻表情報がありません。time_type を unspecified にすると平均的な所要時間で検索します。", []),
            "4036" => ("駅を指定してください。", []),
            "4001" => ("経路検索ができませんでした。日時（時刻表のある範囲か）や、使う交通手段の条件を見直してください。", []),
            _ => ($"経路検索ができませんでした（コード {code}）。", []),
        };

        var lines = new List<string> { r.Message };
        foreach (var name in r.SuggestFor.Distinct())
        {
            try
            {
                var names = (await SuggestAsync(name, ct)).Where(s => s.Kind != "landmark").Select(s => s.Name).Take(8).ToList();
                lines.Add(names.Count > 0
                    ? $"「{name}」の候補: {string.Join("、", names)}"
                    : $"「{name}」に近い駅名も見つかりません。駅名を日本語（漢字・かな）で指定してください。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "「{Name}」の候補の取得に失敗", name);
            }
        }
        return string.Join("\n", lines);
    }

    // ---- 駅名サジェスト ----

    /// <summary>入力補完 API。駅・バス停・施設（ランドマーク）を前方一致で返す。ローマ字・かなでも引ける。</summary>
    public async Task<IReadOnlyList<Suggestion>> SuggestAsync(string value, CancellationToken ct)
    {
        var body = await GetStringAsync($"api/suggest?value={Uri.EscapeDataString(value)}", ct);
        var res = JsonSerializer.Deserialize<SuggestResponse>(body, JsonOptions);
        return (res?.Result ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Suggest))
            .Select(r => new Suggestion(
                r.Id switch { "st" => "station", "bu" => "bus_stop", "la" => "landmark", var other => other ?? "other" },
                r.Suggest!,
                NullIfEmpty(r.Yomi),
                NullIfEmpty(r.Address)))
            .ToList();
    }

    // ---- 共通 ----

    private async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        if (_cache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.FetchedAt < CacheTtl) return hit.Body;

        await Throttle.WaitAsync(ct);
        try
        {
            using var res = await httpClientFactory.CreateClient(HttpClientName).GetAsync(path, ct);
            if ((int)res.StatusCode is >= 300 and < 400)
            {
                var location = res.Headers.Location?.OriginalString ?? "";
                var cmd = location.Split('?', 2) is [_, var qs]
                    ? qs.Split('&').Select(kv => kv.Split('=', 2)).FirstOrDefault(kv => kv[0] == "cmd") is [_, var v] ? Uri.UnescapeDataString(v) : null
                    : null;
                logger.LogInformation("{Path} は {Location} にリダイレクトされました", path, location);
                throw new SearchRejectedException(cmd ?? "unknown");
            }
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("{Path} が {Status} を返しました", path, (int)res.StatusCode);
                throw new McpException($"Yahoo!乗換案内への問い合わせに失敗しました（HTTP {(int)res.StatusCode}）。");
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            _cache[path] = (DateTime.UtcNow, body);
            return body;
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static int? ParsePrice(string? s) =>
        int.TryParse(s?.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---- __NEXT_DATA__ と サジェスト API の JSON（使う項目だけ） ----

    private sealed record NextData(PropsDto? Props);
    private sealed record PropsDto(PagePropsDto? PageProps);
    private sealed record PagePropsDto(NaviSearchParamDto? NaviSearchParam);

    private sealed record NaviSearchParamDto(
        List<FeatureDto>? FeatureInfoList,
        DisplayInfoDto? DisplayInfo,
        AroundInfoDto? AroundInfo,
        OtherQueryInfoDto? OtherQueryInfo);

    private sealed record FeatureDto(SummaryDto? SummaryInfo, List<EdgeDto>? EdgeInfoList);

    private sealed record SummaryDto(
        string? DepartureTime,
        string? ArrivalTime,
        string? TotalTime,
        string? TotalPrice,
        string? TotalPriceDetail,
        string? TimeOnBoard,
        string? TransferCount,
        bool IsFast,
        bool IsEasy,
        bool IsCheap,
        string? Distance,
        List<TeikiDto>? Teiki);

    private sealed record TeikiDto(string? Month, string? Price);

    private sealed record EdgeDto(
        string? StationName,
        string? RailName,
        string? RailNameExcludingDestination,
        string? Destination,
        List<TimeInfoDto>? TimeInfo,
        PriceInfoDto? PriceInfo,
        RidingPositionDto? RidingPositionInfo,
        ExitNameDto? ExitName,
        string? PreCautionalComment,
        List<JsonElement>? DiaInfoStatus,
        List<StopStationDto>? StopStationList,
        AccessInfoDto? AccessInfo);

    private sealed record TimeInfoDto(string? Time, int Type);
    private sealed record PriceInfoDto(string? EdgeGroup, string? Price, string? ExpPrice, string? ExpType);
    private sealed record RidingPositionDto(List<string?>? Departure, List<string?>? Arrival, List<string>? Position);
    private sealed record ExitNameDto(string? From, string? To);
    private sealed record StopStationDto(string? Name, string? DepartureTime);
    private sealed record AccessInfoDto(int Type, List<string>? ClassNameList);

    private sealed record DisplayInfoDto(string? FromName, string? ToName);
    private sealed record AroundInfoDto(string? SearchTypeName, string? SearchYMDWText, string? SearchHMText);
    private sealed record OtherQueryInfoDto(List<CandidateDto>? FromList, List<CandidateDto>? ToList);
    private sealed record CandidateDto(string? Name);

    private sealed record SuggestResponse(List<SuggestItem>? Result);
    private sealed record SuggestItem(string? Id, string? Suggest, string? Yomi, string? Address);
}
