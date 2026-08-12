---
name: infer-gallery-attributes
description: Galleryで指定条件に一致するTitle未付与作品を探し、作者名、ファイル名、作者フォルダ配下のサブフォルダ、フルパス、サムネイル、既存属性の利用実績、過去の手動修正例から既存Title・Character属性を推論して一括付与する。ユーザーが「属性を推論付与して」「〇〇の条件でTitleとCharacterを付けて」「未分類作品を整理して」などと依頼した場合に使用する。
---

# Gallery属性推論付与

## 手順

1. 依頼文とライブコンテキストから対象区分と条件を構造化する。区分が省略されていれば現在表示中のGallery区分を使う。区分を一意に判断できない場合だけ確認する。
2. プロンプトに「自動難易度仕分けが有効」とある場合は`includeImages: false`、それ以外は`includeImages: true`で、`gallerybrowser_get_attribute_inference_batch`を`batchSize: 20`、最初の`cursor: ""`で呼ぶ。以降も区分とconditionsを変えず、返された`nextCursor`を渡す。
3. 各作品の既存Title候補、作者内利用数、全体利用数、別名、ファイル名、`titleHintFolders`、フルパス、手動修正例を比較する。`titleHintFolders`は`【作者名】`フォルダより下のサブフォルダを階層順に抽出したもので、Title名・略称・シリーズ名を含む可能性が高い。ファイル名と同等以上の文字ヒントとして優先しつつ、候補の標準名・別名との一致を確認する。自動難易度仕分け時は次の基準で作品単位に分ける。
   - 文字情報で処理: ファイル名または`titleHintFolders`が標準名・別名・学習例と強く一致し、競合候補がなく、Titleと必要なCharacterを画像なしで判断できる。
   - 画像推論へ保留: 複数候補が競合する、文字情報が乏しい、視覚的な人物・衣装・作品判別が必要、またはCharacterだけでも画像確認が必要。
4. 文字情報で処理する作品は、選んだTitleごとに`gallerybrowser_get_character_candidates_for_inference`を呼び、既存Characterだけを選ぶ。十分な根拠がある既存Titleだけを選び、複数Titleの根拠がある場合は複数選択してよい。
5. 自動難易度仕分け時、画像確認が必要な作品は現在のターンで付与せず、区分とpathを`gallerybrowser_defer_visual_attribute_inference`へ渡す。Titleだけ先に付与すると画像ステージの対象外になるため、作品全体を保留する。
6. 文字情報で確定できた作品は`gallerybrowser_apply_inferred_attributes`で付与する。確認ダイアログは表示されないため、候補IDと対象パスを再確認してから呼ぶ。
7. 画像推論ステージでは、指定された`conditions.paths`を維持して`includeImages: true`で取得し、サムネイルを含めて再判定する。このステージでは再保留せず、根拠不足は未付与のまま残す。未付与の作品はGalleryBrowserが自動的に属性推論の要確認リストへ登録し、ユーザーが既存属性の付与または新規属性追加を行えるようにする。
8. バッチに作品がなくなるまで反復する。固定の総件数上限は設けない。ユーザーによる停止、ツールエラー、Codexの実行時間・コンテキスト上限に達した場合は、処理済み件数、画像推論へ保留した件数、未付与保留件数、残数、最後のnextCursorを報告し、同じ条件で再開できるようにする。
9. 完了時に処理済み、保留、エラーの件数を簡潔に報告する。

## 条件の変換

- 作者名: `conditions.creators`
- Tag: `conditions.tags`。いずれか一致は`tagMatch: "any"`、すべて一致は`"all"`
- パスに含む文字: `conditions.pathContains`
- ファイル名に含む文字: `conditions.fileNameContains`
- 評価値の下限・上限: `conditions.minimumRating` / `maximumRating`
- 画像枚数の下限・上限: `conditions.minimumImageCount` / `maximumImageCount`
- 現在選択中のGallery条件: ライブコンテキストから上記へ変換する。
- `conditions.paths`: 自動仕分け後の画像推論ステージだけで使う完全一致の内部条件。通常のユーザー条件へ流用しない。

条件に対応する構造化項目がない場合、勝手に近似せず、対応できない条件だけをユーザーへ伝える。

## 制約

- 対象はTitle未付与かつアーカイブされていない作品だけ。
- Creator未設定の作品は対象外。`AI生成_FANZA_DLsite販売作品`配下はCreator「その他」として確定後に対象へ含める。
- 新しいTitle・Characterは作成しない。既存属性の削除・置換も行わない。
- Titleの根拠が弱い作品は保留する。大量処理を優先しても、無関係な候補を埋めるためだけに付与しない。
- 手動修正例は正解候補として重視するが、現在の作品情報と矛盾する場合は盲目的にコピーしない。
- 同一実行でcursorを空に戻さない。保留作品を繰り返し処理する無限ループを避ける。
