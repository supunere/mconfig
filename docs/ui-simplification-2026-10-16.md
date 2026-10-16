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

## 割り当てルールとプリセット管理のタブ

- 「割り当て」右側を標準TabControlの「割り当てルール」「プリセット管理」に分け、左カテゴリは共通にした。カテゴリ変更時は管理側も追従し、タブの選択は維持する。組み合わせ表や検証結果からルールへ移動するときはルールタブを開く。
- プリセット管理は全プリセットの値を横に並べる。追加・複製・名前変更・削除は標準ToolStripButtonで直接配置し、対象は値表の選択列とする。①の管理メニューは削除。①の表示対象選択と値編集は維持。
- 表の表示・編集、貼り付け、名前の入力、使用中削除の制限、共有編集の確認は既存処理を共通化。管理側には独立したViewとPresenterインスタンスを接続し、組み合わせの選択・比較基準を渡さない。選択中マーカー・差分凡例・表示対象選択・値の開閉は管理タブに出さない。
- 非表示の管理表はQueryを停止し、表示時に最新状態を読み直す。タブ／カテゴリ変更と画面移動・保存時に編集を確定する。管理側のセル情報もステータスバーに表示する。
- Releaseビルドは警告・エラー0。Presentationテスト12件成功（管理カテゴリの全件表示・基準なし・複製→名前変更→削除・カテゴリ切り替えを追加）。既存WinForms検査も成功。Computer Useで日常画面の管理メニュー削除、タブ切り替え、カテゴリ連動、管理操作の有効化と複製ダイアログを確認。実画面の複製はキャンセルし、元データは変更していない。

最新確認用アプリ: `artifacts/ui-native-winforms/preset-management-tabs/preview/Mconfig.UiBenchmark.exe`
使用データ: 同階層の `preview-workspace`（コピー）。ブランチは `codex/native-winforms-trial`。今回はコミット・pushしていない。

## 割り当て画面の上下配置を試作

タブの階層と配置の違いを減らすため、内側のTabControlを外し、右側を標準SplitContainerで上下に配置した。上は「カテゴリの割り当てルール」、下は「カテゴリのプリセット管理」。左のカテゴリ一覧は共通。両方の見出し・ボタン・表の左端と余白を揃え、管理側も標準Buttonにした。高さは中央の分割バーで調整できる。追加の開閉機能は付けない。

既存の条件編集・管理操作・共有編集確認・画面切り替え時の入力確定は維持。上の説明は下端の既存行数表示と重複するため、見出し横には置かない。下部に以前廃止した影響一覧や対象件数は追加していない。

Releaseビルドは警告・エラー0。既存検査に加え、760/1280pxでの上下表の同時表示・表と操作の左端一致・ボタン高の一致・分割バー操作を確認。Computer Useで上下配置とカテゴリ連動を確認。

試作アプリ: `artifacts/ui-native-winforms/stacked-presets/preview/Mconfig.UiBenchmark.exe`
データ: 同階層の `preview-workspace`（コピー）。タブ版は `artifacts/ui-native-winforms/preset-management-tabs/preview/Mconfig.UiBenchmark.exe` に残す。試作直前のView3ファイルは `artifacts/ui-native-winforms/stacked-presets/before`。今回コミット・pushは行っていない。

カテゴリ見出しにも共通の緑線・太字を適用。カテゴリ欄の上余白をなくし、左右で同じ高さの見出し行を使って「カテゴリ」と「xxxの割り当てルール」の縦位置を統一した。Releaseビルド・既存配置検査成功、実画面でも確認。最新アプリ: `artifacts/ui-native-winforms/category-heading/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。

割り当て画面のカテゴリ側（見出し・一覧周囲）と右側の表周囲をSystemColors.Windowの白背景に統一。選択色・状態色・標準ヘッダーの描画は維持。最新確認用アプリ: `artifacts/ui-native-winforms/white-assignment/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。

背景を白に統一した際に分割バーも白くなったため、左右・上下のSplitContainerの背景だけSystemColors.Controlへ戻した。各領域の白背景と見出し位置は維持し、6pxのグレーの隙間で領域を分ける。最新確認用アプリ: `artifacts/ui-native-winforms/gray-gaps/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。

割り当てルール下端の通常行数説明と、プリセット管理下端の表示件数を非表示にした。割り当てルールの操作行の右端に「上の行ほど優先されます」を配置。データを表示できない場合やルールが見つからない場合の案内は維持。「組み合わせと値」の件数表示は変更しない。最新アプリ: `artifacts/ui-native-winforms/priority-hint/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。

「組み合わせと値」でも値の表の下端の通常件数説明（全件表示・選択した表示対象の件数）を削除。表示できない場合の案内は維持。最新アプリ: `artifacts/ui-native-winforms/no-value-count/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。

## 保存の名称整理

「一時保存」を「保存」、「履歴に保存」を「履歴に残す」に統一。ボタン、メニュー、ツールチップ、終了確認、復元、出力、結果通知を含めて日本語・英語を更新。状態表示は「保存済み」「履歴に未記録／記録済み」。内部APIと保存処理は変更せず、Ctrl+Sも維持。リソースXMLの読み取りと古い表示名の残存チェックを確認。

最新確認用アプリ: `artifacts/ui-native-winforms/save-wording/preview/Mconfig.UiBenchmark.exe`。同階層のpreview-workspaceコピーを使用。

履歴表の列表示順を「日時・作成者・変更理由・コミットID」に変更。標準ListViewのDisplayIndexだけを変更し、行データと履歴選択の対応は維持。最新確認用アプリ: `artifacts/ui-native-winforms/history-column-order/preview/Mconfig.UiBenchmark.exe`。

## 変更の確認と履歴を分離

上部の画面切り替えを「組み合わせと値」「割り当て」「変更の確認」「履歴」「出力」に分けた（Ctrl+1〜5）。変更の確認は変更一覧→検証結果→変更理由と履歴に残す操作の順。履歴は過去の版一覧と選択した版の変更、比較・復元を表示する。右上の通常保存とCtrl+Sは維持。

既存のISaveHistoryViewとPresenterは共有し、各領域を別のページControlに配置。重複した照会・操作購読は作らない。エラー件数クリック・検証は変更の確認へ、履歴に残すの入口は理由入力へ、履歴ページを開くと履歴を再取得する。処理中の進捗とキャンセルは両ページへ反映する。

Releaseビルドは警告・エラー0。通常幅・最小幅のページ表示、画面ごとの操作ルーティング、理由入力へのフォーカス、履歴領域の高さを検査して成功。確認用データ119件を入れた描画も確認。Computer Useで5ページのナビゲーションは確認できたが、ページ移動後の画面取得はツールで失敗したため、別途コントロール描画で両ページを確認した（アプリプロセスは応答あり）。

最新アプリ: `artifacts/ui-native-winforms/review-history-pages/preview/Mconfig.UiBenchmark.exe`。データは同階層のpreview-workspaceコピー。確認用コントロール画像: `artifacts/ui-native-winforms/review-page-check.png`、`artifacts/ui-native-winforms/history-page-check.png`。


### 変更の確認ページの整理

- 「履歴に残す」を最上部に移動し、その中の変更件数・保存状態・検証件数と保存／編集を破棄ボタンを削除。
- 検証が未完了・古い場合や、エラー／警告が1件以上ある場合は記録不可。記録処理でも警告を拒否し、短い理由の確認は維持。
- 画面遷移時の既存の保存／破棄／キャンセル確認は維持。
- ビルド成功。検証関連の回帰テスト5件と、通常幅・最小幅でのWinFormsレイアウトおよび記録ボタン状態の確認が成功。


### 変更の確認・履歴の外観統一

- GroupBoxの囲みを標準TableLayoutPanelへ変更し、他の業務ページと同じSectionTitleを利用。
- 見出しの緑線・白背景・領域間6pxのグレーの隙間を統一。リストの左端と余白を揃え、操作欄を各見出し直下へ移動。
- 変更の確認は「履歴に残す」「前回の記録からの変更」「検証結果」、履歴は「履歴」「選択中の版の変更」を縦に配置。
- ビルドと通常幅・最小幅のWinForms表示チェック成功。確認用ビルド: `artifacts/ui-native-winforms/unified-review-history/preview/Mconfig.UiBenchmark.exe`。

### 表の外周枠の修正

標準ListViewのFixed3D枠に立体枠を重ね描きしていたTableListViewを廃止し、標準ListViewに統一。変更の確認・履歴などの共通一覧と、同じ部品を使用していたプリセットの共有確認ダイアログで、左端が二重に見える原因を除去。


### 保存・発行・履歴への統合

- 画面を「組み合わせと値」「割り当て」「発行」「履歴」の4つに整理（Ctrl+1〜4）。旧「変更の確認」と「出力」を統合し、旧出力画面・Presenterを削除。
- 「発行」に目的、発行先、発行理由、前回の発行からの変更、検証結果を表示。履歴だけを残す操作、テスト版の選択、未完了出力の一覧・再開ボタンは廃止。
- 保存は作業途中の設定保存として維持。保存済みの未発行設定について、終了時に履歴記録を促す確認は出さない。
- 新しいPublishWorkspaceHandlerで検証・保存・発行を直列化。エラー／警告では発行せず、短い発行理由の確認は維持。
- GitPublicationStoreはHEADを進めず準備したバージョンからZIPを作成し、成功後にだけ履歴へ接続する。途中失敗時は保存済み設定と従来の履歴を維持。固定入力・操作IDの記録を内部で利用して再試行し、失敗分を履歴へ追加しない。
- 既存の履歴・既存ZIPは書き換えない。旧出力の内部APIは互換性と既存記録の解釈のため維持。新しい画面からは利用しない。
- 検証: 回帰テスト28件成功。初回／2回目の発行、連続クリック、警告・取消・理由・版競合、記録直後／ZIP作成後／タグ作成後／配置後／履歴追加直前の失敗と再起動後の再試行、Presenter経由のZIP作成と履歴追加を確認。
- WinForms通常幅・最小幅チェック成功。実画面で発行ページの構成を確認。ユーザー操作と入力が重なったため実画面からの発行は行わず、書込処理は自動テストの独立したコピーデータで検証。
- 確認用アプリ: `artifacts/ui-native-winforms/publish-workflow/preview/Mconfig.UiBenchmark.exe`。使用データは同階層の`preview-workspace`。
