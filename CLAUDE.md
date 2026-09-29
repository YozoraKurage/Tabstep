# Tabstep — リポジトリルール

Unity のエディタ拡張パッケージ (`net.yozolab.tabstep`, Unity 2022.3)。内部クラス
`UnityEditor.ProjectBrowser` を自前の EditorWindow の中に埋め込んで、タブ・履歴・
パンくず・型カラムビューを足している。内部 API にはリフレクションで触り、Harmony が
あれば追加で埋め込みブラウザのツールバーを畳む。

## Unity はこのコンテナの中にある

`.devcontainer` は game-ci の Editor イメージ (Unity 2022.3.22f1) の上に建っている。
EditMode テストとエディタ上の検証はコンテナ内で完結する。テスト用プロジェクトは
`/home/node/unity-testproject`（名前付きボリューム）で、このリポジトリを
`file:/workspace` のローカルパッケージとして参照している — リポジトリ側に `Library/`
も `Assets/` も生成されない。

```
.devcontainer/unity/test-daemon.sh start      # 常駐 Unity を立てる（まずこれ）
.devcontainer/unity/unity-do.sh compile       # 再コンパイルしてエラー本文を出す
.devcontainer/unity/unity-do.sh run -e 'return 1 + 1;'
.devcontainer/unity/run-tests.sh              # EditMode テスト全件
.devcontainer/unity/run-tests.sh --filter 'Yozolab.Tabstep.Tests.TabSessionTests'
.devcontainer/unity/test-daemon.sh shot out.png   # 仮想画面を PNG で見る
```

- **コンパイルの確認は `unity-do.sh compile`**。このパッケージは大半が
  `Selection` や内部 API に触る GUI コードで、テストが通る範囲は限られている。
  まず「コンパイルが通るか」を CS エラー本文で確かめること。
- **常駐は GUI モードが既定**（xvfb の仮想画面上、`-batchmode` 無し）。EditorWindow を
  開いて描かせられるのはこのモードだけなので、Tabstep の検証はほぼ常に GUI で行う。
  `start --batch` は GL が動かない環境向け。
- コンパイルエラーがある状態で `start` すると GUI は「Enter Safe Mode?」で主スレッドが
  止まる。`start` は検知して xdotool で Ignore を押し、そのまま起動する（本文は
  `unity-do.sh compile` で読める）。押せなければ終了コード 3 で止まる。
- `unity-do.sh run` は Unity 同梱の Roslyn で外部コンパイルした DLL を常駐エディタへ
  読み込む（ドメインリロード無し・2〜3 秒）。スニペットのアセンブリ名は
  `TabstepSnippet` で、`Editor/AssemblyInfo.cs` がそれに `InternalsVisibleTo` を出して
  いる → `internal` な型（`TabstepProjectWindow`, `AssetColumnView`,
  `TabstepSettings` …）にそのまま触れる。
- 終了コード: 0 / 1=実行時エラー・テスト失敗 / 3=コンパイルエラー / 4=ライセンス未設定
  / 5=デーモンが止まっていた。手がかりは `Logs/daemon.log`、`Logs/daemon.prev.log`、
  `TestDaemon/trace.log`。生ログは数万行あるので丸ごと読まないこと。
- **初回だけ Unity Personal ライセンスの有効化が要る**。
  `.devcontainer/unity/activate-license.sh --status` で状態が見られる。未設定なら手順が
  出るが、ブラウザ操作を含むのでユーザーに依頼すること。
- VRChat SDK（= `0Harmony.dll`）の出し入れは `.devcontainer/unity/add-vpm.sh`。
  Harmony 有り / 無しで挙動が変わる箇所（ナビゲーションバーへの畳み込み）を両方
  確かめたいときに使う。パッケージを出し入れしたら `test-daemon.sh restart`。

## 内部 API は UnityCsReference で裏を取る

`ProjectBrowser` / `ObjectListArea` / `DragAndDrop` などの内部実装に依存する変更は、
推測で書かずに [UnityCsReference](https://github.com/Unity-Technologies/UnityCsReference)
の **2022.3 ブランチ**で該当箇所を読んでから書く。リフレクションで掴む名前は必ず
null ガードし、見つからなければ機能を諦める（例外を投げない）のがこのリポジトリの作法。

## meta ファイルと改行コード

- `.cs` を追加したら `.cs.meta` も一緒にコミットする（`fileFormatVersion: 2` +
  32 桁の guid + `MonoImporter` ブロック）。Unity 側で生成させると guid が変わる。
- 改行は LF。`.gitattributes` が `* text=auto eol=lf` で正規化する。

## リリース版は PR ラベルで決まる

`.github/workflows/release.yml` が PR のラベル (`major` / `minor` / `patch`、
`no-bump` で据え置き) を見て `package.json` の版を上げる。ブランチ名は整理のためだけ。
