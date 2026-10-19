# yj-transit-mcp

Yahoo!乗換案内で電車・バス・飛行機の経路を検索する、ローカル用の MCP サーバー（stdio）。
[tysonwu/norikae-mcp](https://github.com/tysonwu/norikae-mcp)（TypeScript）を C# に移植し、検索結果を構造化して返すようにしたもの。

> [!IMPORTANT]
> **非公式**のツールです。LINEヤフー株式会社とは一切関係がなく、承認や支援も受けていません。
> **無保証**です。取得した時刻・運賃が正確である保証はありません。運行状況や運賃は各事業者の発表で確認してください。
> このツールの利用によって生じたいかなる損害についても、作者は責任を負いません。

## ツール

| ツール | 内容 |
|---|---|
| `search_route` | 駅から駅（施設名も可）への経路を検索する。経路ごとに出発/到着時刻、所要時間、乗換回数、運賃（区間ごとの内訳・特急料金）、定期代と、区間ごとの路線・行き先・発着番線・乗車位置を返す |
| `search_stations` | 駅・バス停・施設を名前の前方一致で検索する。ローマ字・かなでも引ける |

`search_route` の主な引数:

| 引数 | 既定 | 内容 |
|---|---|---|
| `from` / `to` | （必須） | 駅名。日本語（漢字・かな）で指定する。施設名（`六本木ヒルズ` など）も使える |
| `via` | なし | 経由駅（最大 3 駅） |
| `date` / `time` | 今（日本時間） | `YYYY-MM-DD` / `HH:MM` |
| `time_type` | `departure` | `departure`（出発）, `arrival`（到着）, `first_train`（始発）, `last_train`（終電）, `unspecified`（指定なし） |
| `ticket` | `ic` | `ic`（IC カード）, `cash`（きっぷ） |
| `seat` | `non_reserved` | `non_reserved`（自由席）, `reserved`（指定席）, `green`（グリーン車） |
| `walk_speed` | `slightly_slow` | `fast`, `slightly_fast`, `slightly_slow`, `slow` |
| `sort` | `time` | `time`（到着が早い順）, `fare`（運賃が安い順）, `transfer`（乗換回数順） |
| `use_airline` など | `true` | 飛行機・新幹線・有料特急・高速バス・路線バス・フェリーを使うか |
| `include_stops` | `false` | 途中の停車駅と時刻も返す |

同名の駅（`府中`、`中野` など）はサイトが 1 つに決めるので、そのときは結果に `from_candidates` / `to_candidates` として他の候補も入る。
駅名が見つからないときは、`search_stations` と同じ入力補完で探した候補をエラーメッセージに入れて返す。

### norikae-mcp からの変更点

- 検索結果ページの HTML 断片をそのまま返すのをやめ、経路・区間・運賃を JSON にして返す
- 日時の指定を `year` / `month` / `day` / `hour` / `minute` から `date` / `time` に、引数名を snake_case にした
- 駅名が見つからない・出発地と目的地が同じなど、検索できなかった理由を返す
- `search_stations` を追加し、使い方のプロンプト（`norikae-usage`）はツールの説明に含めた

## インストール

[Releases](https://github.com/shao1555/yj-transit-mcp/releases) から使う環境のアーカイブを落として展開する。
.NET ランタイムを含む単一バイナリなので、ほかに必要なものはない。

| 環境 | ファイル |
|---|---|
| macOS（Apple Silicon） | `yj-transit-mcp-osx-arm64.tar.gz` |
| Linux（x64） | `yj-transit-mcp-linux-x64.tar.gz` |
| Windows（x64） | `yj-transit-mcp-win-x64.zip` |

macOS でブラウザから落とした場合は、Gatekeeper に止められないよう隔離属性を外す。

```sh
xattr -d com.apple.quarantine yj-transit-mcp
```

Linux では libicu と tzdata が要る（多くのディストリビューションでは最初から入っている）。

## ビルド

ソースからビルドする場合:

```sh
mise install          # .NET SDK 10
mise run publish      # ./dist/yj-transit-mcp（自己完結型の単一バイナリ）を作る
```

`dist/yj-transit-mcp` は .NET ランタイムがなくても起動するので、MCP クライアントにはこのバイナリを登録する。
mise で入れた dotnet は `DOTNET_ROOT` を設定しないため、`bin/` 配下の apphost を直接指定すると起動しない。

`v*` のタグを push すると、GitHub Actions が 3 環境向けにビルドし、スモークテスト（MCP の initialize と tools/list）を通してから Release を作る。

## 登録

### Claude Code

```sh
claude mcp add yj-transit --scope user -- "$PWD/dist/yj-transit-mcp"
```

### Codex

```sh
codex mcp add yj-transit -- "$PWD/dist/yj-transit-mcp"
```

`codex mcp add` を使わない場合は、`~/.codex/config.toml` に直接書いてもよい。

```toml
[mcp_servers.yj-transit]
command = "/absolute/path/to/yj-transit-mcp/dist/yj-transit-mcp"
```

## しくみ

公開 API はないので、PC 版のページを解析している。

| 用途 | ページ |
|---|---|
| 経路検索 | `GET /search/result?from=…&to=…&y=…&m=…&d=…&hh=…&m1=…&m2=…&type=…` |
| 駅名の入力補完 | `GET /api/suggest?value={キーワード}` |

検索結果ページは Next.js で作られていて、表示内容はすべて `<script id="__NEXT_DATA__">` の JSON に入っているので、HTML ではなくそれを読む。
経路の `edgeInfoList` は経路上の地点（出発地・乗換駅・到着地）の並びで、各地点がそこから次の地点までに乗る路線・番線・運賃を持っている。

検索できなかったときはトップページへ 302 でリダイレクトされ、`Location` の `cmd` に理由のコードが入る（`4011` = 出発地が見つからない、`4018` / `same` = 出発地と目的地が同じ、など）。

同じページは 2 分間メモリにキャッシュし、同時リクエスト数は 2 に制限している。

## 注意

- サイトのページを解析しているので、サイトが改修されると動かなくなる可能性がある。
- 個人がローカルで使う前提で作っている。短い間隔で繰り返し問い合わせるような使い方はしないこと。
- 公式サイトの利用規約を守るのは利用者の責任とする。

## ライセンス

[MIT License](LICENSE)
