# Harmony 連携

Tabstep は任意で [Harmony](https://github.com/pardeike/Harmony) を利用し、レイアウトをさらに整理できます。あくまで任意の機能であり、Harmony がなくても Tabstep は完全に動作します。

## 何が変わるか

プロジェクトに Harmony があると、Tabstep はホストしているブラウザ独自のツールバー（作成ボタン＋検索欄）と「Assets > ...」パスヘッダーを完全に取り除き、その機能を Tabstep 自身のナビゲーションバーへ統合します。

- 作成ボタンと検索欄がナビゲーションバーへ移動する
- それらが占めていた 2 行がフォルダ表示のために解放される
- 結果として、**3 本のバーが 1 本にまとまります**

Harmony がない場合、ブラウザは独自のツールバーを維持し（作成＋検索はそのまま残ります）、重複するパスヘッダーは削除される代わりにその場で空白化されます — その行は空のスペースとして残ります。どちらの場合でも Tabstep は完全に使用できます。Harmony はレイアウトをより引き締めるだけです。

標準の Project ウィンドウはまったく影響を受けません — パッチは Tabstep ウィンドウ内でホストされているブラウザのみを対象とし、[ナビゲーションバー](/guide/settings) の設定が有効な間のみ適用されます。

## 読み込まれ方

Harmony は**実行時にリフレクションで読み込まれます**。ハードな依存関係もスクリプティング定義シンボルもビルド時のセットアップも不要です。Tabstep は単に `0Harmony.dll` が存在し利用可能かどうかを確認し、それに応じてレイアウトを調整します。

## プロジェクトへの導入方法

- **VRChat プロジェクト** — VRChat SDK がすでに `0Harmony.dll` を同梱しているため、特に何もする必要はありません。
- **その他のプロジェクト** — [Lib.Harmony NuGet パッケージ](https://www.nuget.org/packages/Lib.Harmony) をダウンロードし、`.nupkg` を `.zip` にリネームして展開、`lib/net48/0Harmony.dll` をプロジェクト（例: `Assets/Plugins/`）にコピーします。[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) など、DLL をプロジェクトに取り込める他の手段でも構いません。

## 関連ページ

- [画面構成](/guide/interface) — 統合後のナビゲーションバーの位置
- [設定 (Preferences)](/guide/settings) — この連携が前提とする Navigation Bar の設定
