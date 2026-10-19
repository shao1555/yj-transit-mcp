using System.Text.Json;

namespace YjTransitMcp;

public enum TimeType { Departure, Arrival, FirstTrain, LastTrain, Unspecified }

public enum TicketType { Ic, Cash }

public enum SeatPreference { NonReserved, Reserved, Green }

public enum WalkSpeed { Fast, SlightlyFast, SlightlySlow, Slow }

public enum SortOrder { Time, Fare, Transfer }

/// <summary>経路検索の条件。DateTime は日本時間。</summary>
public sealed record RouteQuery(
    string From,
    string To,
    IReadOnlyList<string> Via,
    DateTime DateTime,
    TimeType TimeType = TimeType.Departure,
    TicketType Ticket = TicketType.Ic,
    SeatPreference Seat = SeatPreference.NonReserved,
    WalkSpeed WalkSpeed = WalkSpeed.SlightlySlow,
    SortOrder Sort = SortOrder.Time,
    bool UseAirline = true,
    bool UseShinkansen = true,
    bool UseExpress = true,
    bool UseHighwayBus = true,
    bool UseLocalBus = true,
    bool UseFerry = true);

/// <summary>検索結果ページの内容。From / To はサイトが解釈した地点名（例: 府中 → 府中(東京都)）。</summary>
public sealed record RoutePage(
    string From,
    string To,
    string? SearchType,
    string? SearchDate,
    string? SearchTime,
    IReadOnlyList<Route> Routes,
    IReadOnlyList<string> FromCandidates,
    IReadOnlyList<string> ToCandidates);

public sealed record Route(
    string Departure,
    string Arrival,
    string Duration,
    string? TimeOnBoard,
    int Transfers,
    int? Fare,
    string? FareDetail,
    string? Distance,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<FareSection> Fares,
    IReadOnlyDictionary<string, int>? CommuterPass);

/// <summary>Kind は train / bus / highway_bus / ship / air / walk / other。</summary>
public sealed record Segment(
    string Kind,
    string Line,
    string? Destination,
    StopPoint From,
    StopPoint To,
    IReadOnlyList<StopPoint> Stops,
    string? CarPosition,
    ExpressFee? Express,
    string? Exit,
    string? Notice,
    IReadOnlyList<JsonElement>? ServiceInfo);

public sealed record StopPoint(string Name, string? Time, string? Platform = null);

/// <summary>運賃の区間。乗車券は会社や乗り継ぎによって複数の区間にまたがる。</summary>
public sealed record FareSection(string From, string To, int Fare);

/// <summary>Type は「指定席」「特急料金」など。</summary>
public sealed record ExpressFee(string Type, int Fee);

/// <summary>Kind は station / bus_stop / landmark。</summary>
public sealed record Suggestion(string Kind, string Name, string? Yomi, string? Address);
