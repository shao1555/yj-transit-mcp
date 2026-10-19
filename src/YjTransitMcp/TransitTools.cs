using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace YjTransitMcp;

[McpServerToolType]
public sealed class TransitTools(TransitClient client)
{
    [McpServerTool(Name = "search_route", ReadOnly = true, Idempotent = true)]
    [Description("""
        Yahoo!乗換案内で、駅から駅への経路（電車・バス・飛行機・徒歩）を検索する。
        各経路の出発/到着時刻、所要時間、乗換回数、運賃（区間ごとの内訳と特急料金・指定席料金）、
        区間ごとの路線・行き先・発着番線・乗車位置を返す。

        駅名は必ず日本語（漢字・かな）で渡すこと。英語・中国語の駅名は日本語に直してから呼ぶ。
        - Tokyo → 東京, Shinjuku → 新宿, Shibuya → 渋谷, Ikebukuro → 池袋, Ueno → 上野, Akihabara → 秋葉原
        - Yokohama → 横浜, Osaka → 大阪, Kyoto → 京都, Narita Airport → 成田空港, Haneda Airport → 羽田空港
        - 中国語の簡体字・繁体字は日本の字体にする（涩谷/澀谷 → 渋谷, 横滨/橫濱 → 横浜, 秋叶原 → 秋葉原）
        「六本木ヒルズ」のような施設名も from / to に使える。駅名が分からない・曖昧なときは search_stations で確かめる。
        同名の駅（府中・中野など）は自動で 1 つに決まるので、結果の from / to が意図した駅か確認すること。

        例: 「東京から渋谷まで表参道経由で」→ from="東京", to="渋谷", via=["表参道"]
            「明日 9 時までに横浜に着きたい」→ to="横浜", date=明日, time="09:00", time_type="arrival"
        結果を利用者に示すときは url（Yahoo!乗換案内の検索結果ページ）をリンクとして添えること。
        """)]
    public async Task<object> SearchRoute(
        [Description("出発駅名（例: 東京, 新宿）")] string from,
        [Description("到着駅名（例: 横浜, 九段下）")] string to,
        [Description("経由駅名（最大 3 駅）")] string[]? via = null,
        [Description("日付 (YYYY-MM-DD)。省略時は今日（日本時間）")] string? date = null,
        [Description("時刻 (HH:MM, 24 時間制)。省略時は現在時刻（日本時間）")] string? time = null,
        [Description("時刻の意味: departure=出発（既定）, arrival=到着, first_train=始発, last_train=終電, unspecified=指定なし（平均的な所要時間）")] string time_type = "departure",
        [Description("運賃: ic=IC カード（既定）, cash=きっぷ")] string ticket = "ic",
        [Description("特急・新幹線の座席: non_reserved=自由席（既定）, reserved=指定席, green=グリーン車")] string seat = "non_reserved",
        [Description("歩く速度: fast=急いで, slightly_fast=少し急いで, slightly_slow=少しゆっくり（既定）, slow=ゆっくり")] string walk_speed = "slightly_slow",
        [Description("並び順: time=到着が早い順（既定）, fare=運賃が安い順, transfer=乗換回数が少ない順")] string sort = "time",
        [Description("飛行機を使う")] bool use_airline = true,
        [Description("新幹線を使う")] bool use_shinkansen = true,
        [Description("有料特急を使う")] bool use_express = true,
        [Description("高速バスを使う")] bool use_highway_bus = true,
        [Description("路線バスを使う")] bool use_local_bus = true,
        [Description("フェリーを使う")] bool use_ferry = true,
        [Description("true なら区間ごとの途中停車駅と通過時刻も返す")] bool include_stops = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from)) throw new McpException("from（出発駅）を指定してください。");
        if (string.IsNullOrWhiteSpace(to)) throw new McpException("to（到着駅）を指定してください。");
        var viaList = (via ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (viaList.Count > 3) throw new McpException("経由駅は 3 駅までです。");

        var query = new RouteQuery(
            from.Trim(),
            to.Trim(),
            viaList,
            ParseDateTime(date, time),
            ParseOption(time_type, nameof(time_type), new Dictionary<string, TimeType>
            {
                ["departure"] = TimeType.Departure,
                ["arrival"] = TimeType.Arrival,
                ["first_train"] = TimeType.FirstTrain,
                ["last_train"] = TimeType.LastTrain,
                ["unspecified"] = TimeType.Unspecified,
            }),
            ParseOption(ticket, nameof(ticket), new Dictionary<string, TicketType>
            {
                ["ic"] = TicketType.Ic,
                ["cash"] = TicketType.Cash,
            }),
            ParseOption(seat, nameof(seat), new Dictionary<string, SeatPreference>
            {
                ["non_reserved"] = SeatPreference.NonReserved,
                ["reserved"] = SeatPreference.Reserved,
                ["green"] = SeatPreference.Green,
            }),
            ParseOption(walk_speed, nameof(walk_speed), new Dictionary<string, WalkSpeed>
            {
                ["fast"] = WalkSpeed.Fast,
                ["slightly_fast"] = WalkSpeed.SlightlyFast,
                ["slightly_slow"] = WalkSpeed.SlightlySlow,
                ["slow"] = WalkSpeed.Slow,
            }),
            ParseOption(sort, nameof(sort), new Dictionary<string, SortOrder>
            {
                ["time"] = SortOrder.Time,
                ["fare"] = SortOrder.Fare,
                ["transfer"] = SortOrder.Transfer,
            }),
            use_airline, use_shinkansen, use_express, use_highway_bus, use_local_bus, use_ferry);

        var page = await client.SearchRouteAsync(query, ct);
        return new
        {
            from = page.From,
            to = page.To,
            via = viaList.Count > 0 ? viaList : null,
            search = string.Join(" ", new[] { page.SearchDate, page.SearchTime, page.SearchType }.Where(x => !string.IsNullOrWhiteSpace(x))),
            // 同名の駅があるときはサイトが 1 つに決めてしまうので、ほかの候補も見せる
            from_candidates = page.From != query.From ? page.FromCandidates.Take(6) : null,
            to_candidates = page.To != query.To ? page.ToCandidates.Take(6) : null,
            routes = page.Routes.Select(r => new
            {
                departure = r.Departure,
                arrival = r.Arrival,
                duration = r.Duration,
                time_on_board = r.TimeOnBoard,
                transfers = r.Transfers,
                fare = r.Fare,
                fare_detail = r.FareDetail,
                distance = r.Distance,
                tags = r.Tags.Count > 0 ? r.Tags : null,
                segments = r.Segments.Select(s => new
                {
                    kind = s.Kind,
                    line = s.Line,
                    destination = s.Destination,
                    from = s.From,
                    to = s.To,
                    stop_count = s.Kind == "walk" ? (int?)null : s.Stops.Count,
                    stops = include_stops && s.Stops.Count > 0 ? s.Stops.Select(st => new { name = st.Name, time = st.Time }) : null,
                    car_position = s.CarPosition,
                    express = s.Express,
                    exit = s.Exit,
                    notice = s.Notice,
                    service_info = s.ServiceInfo,
                }),
                fares = r.Fares,
                commuter_pass = r.CommuterPass,
            }),
            url = TransitClient.BaseUrl + TransitClient.BuildSearchPath(query),
            note = page.Routes.Count == 0
                ? "経路が見つかりませんでした。日時や交通手段の条件を変えて検索してください。"
                : "運賃は円（ticket の指定に従い IC またはきっぷ）。fare は乗車券と特急料金などの合計で、fares は乗車券の区間ごとの内訳、"
                  + "express はその区間の特急料金・指定席料金。tags の 早=到着が早い, 安=運賃が安い, 楽=乗換が少ない。"
                  + "kind は train / bus / highway_bus / ship / air / walk。stop_count は途中の停車駅の数。時刻は日本時間で、日付をまたぐ経路では到着が出発より小さい時刻になる。",
        };
    }

    [McpServerTool(Name = "search_stations", ReadOnly = true, Idempotent = true)]
    [Description("""
        Yahoo!乗換案内の入力補完で、駅・バス停・施設を名前の前方一致で検索する。ローマ字やかなでも引ける（例: shinjuku, しぶや）。
        search_route に渡す正確な駅名（例: 府中 → 府中(東京都)）を調べるとき、駅名があいまいなときに使う。
        返した name はそのまま search_route の from / to / via に使える。
        """)]
    public async Task<object> SearchStations(
        [Description("駅名・バス停名・施設名の先頭部分（例: 府中, 六本木ヒ, shinjuku）")] string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("query を指定してください。");
        var items = await client.SuggestAsync(query.Trim(), ct);
        return new
        {
            stations = items.Where(i => i.Kind == "station").Select(i => new { name = i.Name, yomi = i.Yomi, prefecture = i.Address }),
            bus_stops = items.Where(i => i.Kind == "bus_stop").Select(i => new { name = i.Name, yomi = i.Yomi, prefecture = i.Address }),
            landmarks = items.Where(i => i.Kind == "landmark").Select(i => new { name = i.Name, address = i.Address }),
            note = items.Count == 0
                ? "一致する駅・バス停・施設がありません。表記を変えて検索してください。"
                : "前方一致の候補で、件数は種類ごとに上限がある。prefecture は駅・バス停の所在都道府県。",
        };
    }

    private static DateTime ParseDateTime(string? date, string? time)
    {
        var now = TransitClient.NowJst();
        var d = DateOnly.FromDateTime(now);
        var t = new TimeOnly(now.Hour, now.Minute);
        if (!string.IsNullOrWhiteSpace(date) &&
            !DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            throw new McpException("date は YYYY-MM-DD 形式で指定してください。");
        if (!string.IsNullOrWhiteSpace(time) &&
            !TimeOnly.TryParseExact(time.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
            throw new McpException("time は HH:MM 形式（24 時間制）で指定してください。");
        return d.ToDateTime(t);
    }

    private static T ParseOption<T>(string value, string name, Dictionary<string, T> options)
    {
        if (options.TryGetValue(value.Trim().ToLowerInvariant(), out var v)) return v;
        throw new McpException($"{name} は {string.Join(" / ", options.Keys)} のいずれかで指定してください。");
    }
}
