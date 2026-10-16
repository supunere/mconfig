# 組み合わせと値のシンプル化

画面を「値の確認と修正」に集中させるため、次を反映した。

- 組み合わせ表は標準DataGridViewの1段ヘッダーに戻した。GroupedHeaderGridを削除し、上段の結合描画・スクロール追従・座標と罫線の補正をなくした。列幅・行選択・カテゴリ選択・フィルターでの条件列省略は維持。
- 値ペインの「選んだ組み合わせ」説明を削除。カテゴリ名と「値を隠す」は維持。説明のためだけに渡していたSelectedCombinationLabelとComparisonDisplay.SelectedCombinationも削除。差分や共有編集の判定に使う選択条件そのものは保持。
- 追加・複製・名前変更・削除を「プリセットの管理」メニューへ移した。表示対象の選択は従来どおり直接操作できる。操作先は値表で選んだ列のプリセット。値の列を選んでいなければ追加以外は無効。読み取り専用・保存中などの編集制限も維持。
- 共用しなくなった文言を日本語・英語のリソースから除去し、管理メニューの文言を追加した。

管理メニューは専用管理画面までの段階的な配置。追加しても現在の組み合わせには自動で割り当てられないため、将来的にはカテゴリごとのプリセット一覧・使用先確認と管理操作を専用画面に集め、「割り当て」から移動できる構成が自然。今回は新しい管理画面や割り当て機能は追加していない。

値の編集はプリセット自体の修正であり、それを共有する組み合わせへも影響する。既存の共有編集の確認はそのまま残す。

## 確認

- Releaseビルド: 警告・エラー0。
- Presentation回帰テスト: 10件成功。列順、表示対象、選択中プリセット基準の差分を維持。
- 実WinForms検査: 15条件・通常幅／最小幅・16pt・日本語／英語・タグ表示時の収まり、標準1段ヘッダー、項目名列の幅を確認。
- 管理メニュー: 各項目が正しい操作とプリセット名を通知すること、値の列が未選択の場合と読み取り専用時の制限、読み取り専用でも表示対象は選べることを確認。実データの作成・削除は行っていない。
- Computer Use: 1段ヘッダー、説明の削除、選択中見出しと差分の維持、管理メニューの表示を確認。

確認用アプリ: `artifacts/ui-native-winforms/simple/preview/Mconfig.UiBenchmark.exe`

データは `artifacts/ui-native-winforms/simple/preview-workspace` のコピーを使用。通常起動と同じく最後に開いたワークスペースとして記憶される。

画面画像: `artifacts/ui-native-winforms/simple.png`、メニュー画像: `artifacts/ui-native-winforms/simple-menu.png`。

ブランチは `codex/native-winforms-trial` のまま。コミット・pushは行っていない。

## 絞り込みのシンプル化

- バッジと専用の行を削除。標準Label・ComboBoxを並べ、条件を初期表示する。
- 「条件を隠す」でのみ折りたたみ、「条件は非表示（N条件を指定）」と「条件を表示」を表示する。条件は維持し、「すべて解除」も利用できる。
- コンボボックス下端の必要高を確保する既存修正を維持。独自描画や新しいコントロールは追加していない。
- Releaseビルド、15条件・760/1266px・16pt・日英表示、開閉時の選択維持とイベント非発火を検査して成功。言語切替で絞り込み変更イベントが通知されないように保護した。
- Computer Useで初期表示、1条件の選択、折りたたみ中も9/27件の絞り込みを維持すること、再表示で選択が残ることを確認。

最新の確認用アプリ: `artifacts/ui-native-winforms/plain-filters/preview/Mconfig.UiBenchmark.exe`

使用データ: `artifacts/ui-native-winforms/plain-filters/preview-workspace`（元データのコピー）。
画像: `artifacts/ui-native-winforms/plain-filters-expanded.png`、`artifacts/ui-native-winforms/plain-filters-collapsed.png`。

## 状態と操作の分かりやすさ

- 件数を「全27件中9件」に変更。解除ボタンは条件の開閉ボタンの隣に配置。
- 折りたたみ中は通常のLabelで最初の条件と残りの条件数を1行表示。長い表示はAutoEllipsisで省略し、標準ToolTipに全条件を表示する。開いている間は重複表示しない。
- 凡例には「凡例：」を付けて枠を削除。意味を持つ背景色・未保存の太字と*は維持。
- 編集可能な値セルに「ダブルクリックまたはF2で編集できます」の標準ツールチップを付ける。読み取り専用・使用しない等のセルには編集案内を表示しない。
- 配置検査（760/1266px、16pt、日英）、条件保持、要約と全条件のツールチップ、編集可能時だけの案内、管理メニューの既存検査が成功。Computer Useで条件選択、件数、折りたたみ時の要約と凡例を確認。

最新確認用アプリ: `artifacts/ui-native-winforms/usability/preview/Mconfig.UiBenchmark.exe`
データは同階層の `preview-workspace` のコピー。画像: `artifacts/ui-native-winforms/usability-collapsed.png`。

## 見出しの縦線と背景による領域分け

参考画像に合わせ、絞り込み・組み合わせ・値の見出し左に細い緑線を追加。線は標準Panel、見出しは同じ本文サイズの太字Labelとし、TableLayoutPanelで配置する。青の選択色と別の役割にする。高コントラストではシステム文字色。

絞り込み領域全体だけを薄いグレー（244,244,244）にし、組み合わせ・値の領域と操作部分はシステムの白背景に変更。標準ボタン・入力欄・表、分割ペイン、差分・選択色は維持。既存の他画面の外観には適用しない。

Releaseビルドは警告・エラー0。既存の配置・開閉・選択保持・コンボ下端・日英・文字拡大・値編集案内・管理メニューの検査に成功。Computer Useで初期表示と折りたたみを確認。

最新確認用アプリ: `artifacts/ui-native-winforms/section-markers/preview/Mconfig.UiBenchmark.exe`
使用データ: `artifacts/ui-native-winforms/section-markers/preview-workspace`（元データのコピー）。
画像: `artifacts/ui-native-winforms/section-markers-expanded.png`、`artifacts/ui-native-winforms/section-markers-collapsed.png`。

## 開閉操作を見出しに固定

- 条件の表示／非表示ボタンを絞り込み見出し行の右端に固定。条件数や要約の長さによって横位置が動かないようにした。
- 値は下側ペイン全体を消さず、カテゴリ名と開閉ボタンを残して本文だけ折りたたむ。閉じている間は見出し高だけ確保し、分割線のドラッグを止める。再表示では保存していた分割比率に戻す。
- 組み合わせ見出しに移動していた「値を表示」ボタンと、その専用イベント・表示制御を削除。F6と表示メニューは同じ開閉操作を維持。
- 条件選択・値の列順・選択基準の差分・共有編集の確認・編集中の確定処理は維持。
- Releaseビルドは警告・エラー0。従来の配置検査に加え、3回の開閉、閉じたまま760pxへ縮小と復元、見出しの常時表示、分割比率の維持、条件開閉ボタンの横位置を検査して成功。Computer Useでも開閉と元の表示への復帰を確認。

最新確認用アプリ: `artifacts/ui-native-winforms/accordion/preview/Mconfig.UiBenchmark.exe`
使用データ: `artifacts/ui-native-winforms/accordion/preview-workspace`（コピー）。
画像: `artifacts/ui-native-winforms/accordion-collapsed.png`。

## タイトルから開閉するアコーディオンの試作

採用前の比較用試作。右端ボタン版は `artifacts/ui-native-winforms/accordion/preview/Mconfig.UiBenchmark.exe` に残す。今回変更した3ファイルの直前コピーは `artifacts/ui-native-winforms/accordion-title/before`。

- 絞り込みと値の見出しを標準Buttonに変更。枠を外し、開いているときは▼、閉じたときは▶とする。緑線と通常サイズの太字見出しを維持。
- アイコンとタイトルの範囲で開閉する。マウスの反応と標準キーボードフォーカスを残し、Enter／Spaceで操作できる。AccessibleNameには見出しと開閉操作を設定。
- 右端の開閉ボタンを外し、既存の折りたたみ処理を再利用。独自描画・アニメーション・新しいライブラリは追加していない。
- Releaseビルドは警告・エラー0。配置・文字拡大・日英・選択保持・値の再表示位置の検査が成功。Computer Useでタイトルクリック、Enter／Spaceでの条件開閉と値の開閉を確認。

試作アプリ: `artifacts/ui-native-winforms/accordion-title/preview/Mconfig.UiBenchmark.exe`
データ: `artifacts/ui-native-winforms/accordion-title/preview-workspace`（コピー）。
画像: `artifacts/ui-native-winforms/accordion-title-expanded.png`、`artifacts/ui-native-winforms/accordion-title-collapsed.png`。

タイトル開閉の試作は、操作は良いが外観が馴染まないというユーザー判断で不採用。試作前の3ファイルを復元し、専用文言を除去した。現在は右端の開閉ボタン版。緑線、背景色、値の見出しを残す折りたたみは維持。比較用の試作ビルド・画像は履歴として残す。

## 折りたたみ時の条件を全文表示

「条件は非表示」を削除し、見出しの状態は指定数／絞り込みなしのみ表示。折りたたみ時は指定した条件をすべて「条件名: 値 / 条件名: 値」で表示し、幅に合わせて標準Labelを折り返す。省略記号・ほかN条件・全文用ToolTipは削除。15個の長い指定条件を760/1266pxと拡大文字で省略せず表示できること、および既存の配置・開閉検査を確認。Releaseビルドは警告・エラー0。

最新アプリ: `artifacts/ui-native-winforms/full-filter-summary/preview/Mconfig.UiBenchmark.exe`。使用データは同階層の`preview-workspace`のコピー。

## 組み合わせ表のクリックで値を自動表示

値を閉じている間に組み合わせ表の本文セルをクリックすると値を開く。選択済みセルの再クリックにも対応するため、選択変更ではなく標準DataGridViewのCellClickを使用。見出し・空白・スクロール操作・キーボードの選択変更では開かない。元の分割比率を維持。クリック／再クリック、選択変更、列見出し、分割比率の回帰検査に成功。

最新確認用アプリ: `artifacts/ui-native-winforms/auto-show-values/preview/Mconfig.UiBenchmark.exe`。使用データは同階層の`preview-workspace`のコピー。

## 凡例のシンプル化

「凡例：」「未保存 *」「未入力」「使用しない」の説明ラベルを削除し、淡い黄色の「選択中のプリセットとの差分」だけを残した。セル内の状態文言・未保存の*・状態色・編集操作は維持。日本語と英語の未使用リソースも削除。Releaseビルドは警告・エラー0、既存の配置・開閉・編集案内・自動表示検査に成功。

最新確認用アプリ: `artifacts/ui-native-winforms/simple-legend/preview/Mconfig.UiBenchmark.exe`。使用データは同階層の`preview-workspace`のコピー。

## 差分説明を表の右下へ移動

差分説明は値の表の上から下へ移し、表示プリセット数と同じ行の右端に配置。標準TableLayoutPanelで左は表示数、右は差分説明とし、表のスクロールとは独立させた。値の折りたたみ時は両方隠れる。Releaseビルドと既存の配置・開閉検査に成功。

最新確認用アプリ: `artifacts/ui-native-winforms/footer-diff/preview/Mconfig.UiBenchmark.exe`。データは同階層の`preview-workspace`のコピー。

差分説明の位置は再調整し、値の表の右上（表示対象・管理操作と同じ行の右端）へ移動。表示プリセット数は従来どおり左下に残す。最新確認用アプリ: `artifacts/ui-native-winforms/top-right-diff/preview/Mconfig.UiBenchmark.exe`。データは同階層の`preview-workspace`のコピー。

## 共有プリセットの変更確認を簡素化

見出しを「～は、次の組み合わせで使われています。」へ変更。影響の重複説明、「これも変わります／選んでいる組み合わせ」の列、割り当てを分ける案内、確認を一時保存まで省略するチェックボックスを削除。使用先の条件列、複数プリセット時の名前列、件数省略時の案内、確認文、変更／キャンセルは維持。キャンセルが既定。

SkipUntilSavedとその保存・解除処理を削除し、共有プリセットの編集開始ごとに確認する。同じ編集の開始と確定では二重に尋ねない既存処理を維持。毎回の確認とキャンセルを検証する回帰テストを追加し、Presentationテスト11件成功。Releaseビルドは警告・エラー0。Computer Useで実ダイアログを確認し、データ変更は確定していない。

最新確認用アプリ: `artifacts/ui-native-winforms/simple-shared-dialog/preview/Mconfig.UiBenchmark.exe`。データは同階層の`preview-workspace`のコピー。

## 右下ステータスの文字切れ修正

ワークスペース表示が160px固定で末尾を切っていたため、標準のAutoSizeへ戻した。ステータスバー右端に8pxの余白を確保。フルパスのツールチップは維持。Releaseビルドは警告・エラー0、既存の配置検査成功、Computer Useでpreview-workspace全体と右端余白を確認。最新アプリ: `artifacts/ui-native-winforms/status-spacing/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。

## 割り当て画面の下側領域を削除

「この変更で割り当て先が変わる組み合わせ」の一覧と、その右の集計・操作説明を削除。上下分割をなくし、右側は割り当て表が全高を使う。代替の対象件数表示は追加しない。左カテゴリ、条件・割り当て先の編集、行の追加・削除・移動、行ごとの適用数・問題表示は維持。変更一覧専用のコントロールと表示範囲切り替えイベントを除去。Releaseビルドは警告・エラー0、既存配置検査成功、Computer Useで割り当て画面の配置と既存行の表示を確認。

最新確認用アプリ: `artifacts/ui-native-winforms/simple-assignment/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。
