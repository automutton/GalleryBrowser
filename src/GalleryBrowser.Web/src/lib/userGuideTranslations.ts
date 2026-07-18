export type UserGuideTranslation = [english: string, simplifiedChinese: string, traditionalChinese: string];

// User Guide copy is kept separate from the general system UI dictionary so that
// long-form help text can be reviewed and maintained as one translation unit.
export const userGuideTranslations: Record<string, UserGuideTranslation> = {
  // Index and toolbar
  'User Guide': ['User Guide', '用户指南', '使用者指南'],
  'INDEX': ['INDEX', '目录', '目錄'],
  'GALLERYBROWSER HANDBOOK': ['GALLERYBROWSER HANDBOOK', 'GALLERYBROWSER 使用手册', 'GALLERYBROWSER 使用手冊'],
  'ユーザーガイド': ['User Guide', '用户指南', '使用者指南'],
  'GalleryBrowserの基本操作と機能別の使い方を確認できます': [
    'Learn the basic workflow and features of GalleryBrowser.',
    '了解GalleryBrowser的基本操作和各项功能。',
    '瞭解GalleryBrowser的基本操作和各項功能。'
  ],
  'はじめに': ['Introduction', '简介', '簡介'],
  '初期設定と基本の流れ': ['Getting started', '初始设置与基本流程', '初始設定與基本流程'],
  'Bookmark・付箋': ['Bookmark & Sticky Notes', 'Bookmark与便笺', 'Bookmark與便箋'],
  'DB・キャッシュ・バックアップ': ['Database, cache & backup', '数据库、缓存与备份', '資料庫、快取與備份'],
  'ショートカット': ['Shortcuts', '快捷键', '快速鍵'],
  '困ったときは': ['Troubleshooting', '故障排除', '疑難排解'],
  '謝辞': ['Acknowledgements', '致谢', '致謝'],

  // 01 — Introduction
  '作品・作者・ファイルを': ['Works, creators, and files', '作品、作者与文件', '作品、作者與檔案'],
  'ひとつの流れで管理する': ['Managed in one workflow', '在一个流程中统一管理', '在一個流程中統一管理'],
  'GalleryBrowserは、ローカルの作品ファイルを中心に、属性、タグ管理、ファイル操作、作者情報、課金記録、アナリティクス、自由なメモまでも一元管理するためのアプリです。': [
    'GalleryBrowser brings attributes, tag management, file operations, creator information, payment records, analytics, and free-form notes together around your local work files.',
    'GalleryBrowser以本地作品文件为中心，统一管理属性、标签、文件操作、作者信息、付费记录、分析以及自由笔记。',
    'GalleryBrowser以本機作品檔案為中心，統一管理屬性、標籤、檔案操作、作者資訊、付費記錄、分析以及自由筆記。'
  ],
  '整理する': ['Organize', '整理', '整理'],
  'ExplorerとDB管理機能を使い、実ファイルと登録情報を揃えて管理します。': [
    'Use Explorer and database tools to keep physical files and registered information aligned.',
    '使用Explorer和数据库管理功能，使实际文件与登记信息保持一致。',
    '使用Explorer和資料庫管理功能，使實體檔案與登錄資訊保持一致。'
  ],
  '見つける': ['Find', '查找', '尋找'],
  '区分と属性を組み合わせ、目的の作品や作者へすばやく到達します。': [
    'Combine sections and attributes to quickly reach the work or creator you need.',
    '组合区分与属性，快速找到目标作品或作者。',
    '組合區分與屬性，快速找到目標作品或作者。'
  ],
  '振り返る': ['Review', '回顾', '回顧'],
  'Creator TrackingとUser Metricsで、保有状況や評価、活動記録を可視化します。': [
    'Use Creator Tracking and User Metrics to visualize your collection, ratings, and activity history.',
    '通过Creator Tracking和User Metrics可视化收藏状况、评价与活动记录。',
    '透過Creator Tracking和User Metrics視覺化收藏狀況、評價與活動記錄。'
  ],
  '最初に覚える3つの単位': ['Three concepts to learn first', '首先了解的三个概念', '首先瞭解的三個概念'],
  'はライブラリの大分類、': [' is a top-level library group; ', '是资料库的大分类；', '是資料庫的大分類；'],
  'はCreator・Title・Character・Tagなどの絞り込み情報、': [
    ' represents filter information such as Creator, Title, Character, and Tag; ',
    '表示Creator、Title、Character、Tag等筛选信息；',
    '表示Creator、Title、Character、Tag等篩選資訊；'
  ],
  'は作品ファイルを一意に識別するIDです。': [
    ' is the ID that uniquely identifies a work file.',
    '是唯一标识作品文件的ID。',
    '是唯一識別作品檔案的ID。'
  ],

  // 02 — Getting started
  'フォルダを登録してから作品を探せるようになるまで': [
    'From registering folders to finding your works',
    '从登记文件夹到可以查找作品',
    '從登錄資料夾到可以尋找作品'
  ],
  '本体DBの保存場所を決める': ['Choose the main database location', '确定主数据库的保存位置', '決定主資料庫的儲存位置'],
  'Settings ＞ Files ＞ データベース': ['Settings > Files > Database', 'Settings > Files > 数据库', 'Settings > Files > 資料庫'],
  'で本体DBとキャッシュDBの保存先を確認・設定します。特に本体DBは重要な登録情報を保存するため、遅くとも最初の走査より前に保存場所を決めることを推奨します。': [
    ': review and configure the locations of the main and cache databases. The main database contains important registered information, so choose its location before the first scan.',
    '：检查并设置主数据库和缓存数据库的保存位置。主数据库保存重要登记信息，建议最迟在首次扫描前确定保存位置。',
    '：檢查並設定主資料庫和快取資料庫的儲存位置。主資料庫保存重要登錄資訊，建議最遲在首次掃描前決定儲存位置。'
  ],
  '区分と対象フォルダを登録': ['Register sections and target folders', '登记区分与目标文件夹', '登錄區分與目標資料夾'],
  'Settings ＞ Files ＞ 区分別の設定': ['Settings > Files > Section settings', 'Settings > Files > 区分设置', 'Settings > Files > 區分設定'],
  'で区分を作り、対象ファイルの拡張子と対象ディレクトリを設定します。': [
    ': create sections and configure the target file extensions and directories.',
    '：创建区分，并设置目标文件扩展名与目录。',
    '：建立區分，並設定目標檔案副檔名與目錄。'
  ],
  'ファイルを走査': ['Scan files', '扫描文件', '掃描檔案'],
  'の「SQLiteDBの手動更新」から対象区分を選び、更新を実行します。': [
    ': choose a section under Manual SQLiteDB Update and run the update.',
    '：在“手动更新SQLiteDB”中选择目标区分并执行更新。',
    '：在「手動更新SQLiteDB」中選擇目標區分並執行更新。'
  ],
  'Galleryで内容を確認': ['Review the results in Gallery', '在Gallery中检查结果', '在Gallery中檢查結果'],
  '左のGallery配下から区分を開き、作品カード、Creator、Title、Character、Tagが意図どおり表示されるか確認します。': [
    'Open a section under Gallery on the left and confirm that work cards, Creator, Title, Character, and Tag appear as expected.',
    '从左侧Gallery下打开区分，确认作品卡片、Creator、Title、Character、Tag是否按预期显示。',
    '從左側Gallery下開啟區分，確認作品卡片、Creator、Title、Character、Tag是否如預期顯示。'
  ],
  '属性と作者情報を整備': ['Organize attributes and creator information', '整理属性与作者信息', '整理屬性與作者資訊'],
  '作品カードの右クリックメニューからフィルターやTagを登録し、必要な作者はCreator Trackingへ追加します。': [
    'Register filters and Tags from a work card context menu, then add creators you want to follow to Creator Tracking.',
    '从作品卡片的右键菜单登记筛选器和Tag，并将需要的作者添加到Creator Tracking。',
    '從作品卡片的右鍵選單登錄篩選器和Tag，並將需要的作者加入Creator Tracking。'
  ],
  'バックアップ方針を決める': ['Choose a backup policy', '确定备份策略', '決定備份策略'],
  '必要ならpCloudバックアップを設定します。キャッシュは再生成できますが、本体DBは定期的にスナップショットを保存してください。': [
    'Configure pCloud backup if needed. Caches can be regenerated, but save regular snapshots of the main database.',
    '如有需要，请设置pCloud备份。缓存可以重新生成，但主数据库应定期保存快照。',
    '如有需要，請設定pCloud備份。快取可以重新產生，但主資料庫應定期儲存快照。'
  ],
  '走査前の確認': ['Before scanning', '扫描前检查', '掃描前檢查'],
  '対象ディレクトリと拡張子が広すぎると、意図しないファイルまで登録されます。最初は小さなフォルダで確認することを推奨します。': [
    'Overly broad directories or extensions can register unintended files. Start with a small folder to verify the result.',
    '目标目录或扩展名范围过大时，可能会登记非预期文件。建议先用小文件夹确认。',
    '目標目錄或副檔名範圍過大時，可能會登錄非預期檔案。建議先用小資料夾確認。'
  ],

  // 03 — Gallery
  '属性で絞り込み、作品を評価・整理するメインビュー': [
    'The main view for filtering, rating, and organizing works',
    '用于筛选、评价和整理作品的主视图',
    '用於篩選、評價和整理作品的主檢視'
  ],
  '属性フィルタ': ['Attribute filters', '属性筛选器', '屬性篩選器'],
  'Rating、Creator、Title、Character、Tagを選んで表示作品を絞り込みます。Expandで候補を展開し、Collapseの右クリックで展開状態をピン留めできます。': [
    'Filter visible works with Rating, Creator, Title, Character, and Tag. Expand shows more candidates, and right-clicking Collapse pins the expanded state.',
    '使用Rating、Creator、Title、Character、Tag筛选显示的作品。Expand可展开候选项，右键单击Collapse可固定展开状态。',
    '使用Rating、Creator、Title、Character、Tag篩選顯示的作品。Expand可展開候選項，右鍵點擊Collapse可固定展開狀態。'
  ],
  '属性ボタンの並びをRating、Files、Abcで変更します。複数の条件を選ぶと選択順が優先順位になり、右クリックで条件を解除します。': [
    'Sort attribute buttons by Rating, Files, or Abc. When multiple rules are selected, selection order determines priority; right-click a rule to remove it.',
    '按Rating、Files或Abc排列属性按钮。选择多个条件时，选择顺序即优先级；右键单击可解除条件。',
    '按Rating、Files或Abc排列屬性按鈕。選擇多個條件時，選擇順序即優先順序；右鍵點擊可解除條件。'
  ],
  '作品カードをRating、Pics、Access date、Filepathで並べ替えます。複数条件、昇順・降順、右クリック解除の操作はFilters Sortと共通です。': [
    'Sort work cards by Rating, Pics, Access date, or Filepath. Multiple rules, ascending or descending order, and right-click removal work the same as Filters Sort.',
    '按Rating、Pics、Access date或Filepath排列作品卡片。多条件、升降序以及右键解除的操作与Filters Sort相同。',
    '按Rating、Pics、Access date或Filepath排列作品卡片。多條件、升降冪以及右鍵解除的操作與Filters Sort相同。'
  ],
  '検索、Bookmark、付箋、選択CreatorのTracking表示、登録済みストレージをExplorerで開く操作をまとめています。': [
    'Provides search, Bookmark, Sticky Notes, opening the selected Creator in Tracking, and opening registered storage in Explorer.',
    '集中提供搜索、Bookmark、便笺、打开所选Creator的Tracking，以及在Explorer中打开已登记存储位置。',
    '集中提供搜尋、Bookmark、便箋、開啟所選Creator的Tracking，以及在Explorer中開啟已登錄儲存位置。'
  ],
  'カードを右クリック': ['Right-click a card', '右键单击卡片', '右鍵點擊卡片'],
  'すると、逆引きフィルタ、フィルターの登録と解除、Tagの登録と解除、ファイル削除などを実行できます。': [
    ' to use reverse lookup filters, register or remove filters and Tags, delete files, and more.',
    '可执行反向筛选、登记或解除筛选器与Tag、删除文件等操作。',
    '可執行反向篩選、登錄或解除篩選器與Tag、刪除檔案等操作。'
  ],
  'は選択作品のTitleまたはTitle＋Characterから、Galleryの選択状態を組み直します。': [
    ' rebuilds Gallery selections from the selected work\'s Title or Title plus Character.',
    '会根据所选作品的Title或Title＋Character重新设置Gallery的选择状态。',
    '會根據所選作品的Title或Title＋Character重新設定Gallery的選擇狀態。'
  ],
  'はCreator・Title・Character・Tagの選択をまとめて解除します。': [
    ' clears all Creator, Title, Character, and Tag selections.',
    '会一次清除Creator、Title、Character、Tag的全部选择。',
    '會一次清除Creator、Title、Character、Tag的全部選擇。'
  ],
  'カードは複数選択に対応': ['Cards support multiple selection', '卡片支持多选', '卡片支援多選'],
  'しています。Ctrl＋クリックで個別に追加・解除し、Shift＋クリックで基準カードから範囲選択できます。複数作品への属性・Tag操作にも利用できます。': [
    '. Ctrl-click adds or removes individual cards; Shift-click selects a range from the anchor card. This also supports attribute and Tag operations on multiple works.',
    '。使用Ctrl＋单击可逐个添加或取消，使用Shift＋单击可从基准卡片选择范围，也可对多个作品执行属性与Tag操作。',
    '。使用Ctrl＋點擊可逐一加入或取消，使用Shift＋點擊可從基準卡片選擇範圍，也可對多個作品執行屬性與Tag操作。'
  ],
  '作品を1件選択して付箋を作ると、その作品に紐づく付箋になります。作品が表示対象外になると付箋も非表示になります。': [
    'Create a Sticky Note with one work selected to attach it to that work. The note is hidden whenever the work is not visible.',
    '选择一个作品后创建便笺，便笺会与该作品关联；作品不在显示范围内时，便笺也会隐藏。',
    '選擇一個作品後建立便箋，便箋會與該作品關聯；作品不在顯示範圍內時，便箋也會隱藏。'
  ],

  // 04 — Explorer
  'タブ・分割表示に対応したローカルファイル操作': [
    'Local file operations with tabs and split view',
    '支持标签页与分屏视图的本地文件操作',
    '支援分頁與分割檢視的本機檔案操作'
  ],
  '表示と移動': ['Viewing and navigation', '显示与导航', '顯示與導覽'],
  '＋ボタンでタブを追加し、右クリックメニューから登録済み候補を開けます。': [
    'Use the + button to add a tab, or open a registered candidate from its context menu.',
    '使用＋按钮添加标签页，或从右键菜单打开已登记的候选项。',
    '使用＋按鈕新增分頁，或從右鍵選單開啟已登錄的候選項。'
  ],
  '開いているタブを左側メインパネルのExplorerへドラッグ＆ドロップすると、そのフォルダをクイックアクセスとして登録できます。': [
    'Drag an open tab onto Explorer in the left navigation to register the folder as Quick Access.',
    '将打开的标签页拖放到左侧主面板的Explorer，可将该文件夹登记为快速访问。',
    '將開啟的分頁拖放到左側主面板的Explorer，可將該資料夾登錄為快速存取。'
  ],
  '分割表示では左右それぞれにフォルダを表示し、フォーカス中のペインへ操作を行います。': [
    'Split view shows a folder on each side; operations apply to the pane that has focus.',
    '分屏视图会在左右分别显示文件夹，操作将应用于当前获得焦点的窗格。',
    '分割檢視會在左右分別顯示資料夾，操作將套用於目前取得焦點的窗格。'
  ],
  'アドレス欄のドライブ名から、アクセス可能な別ドライブへ切り替えられます。': [
    'Use the drive name in the address bar to switch to another accessible drive.',
    '可通过地址栏中的驱动器名称切换到其他可访问的驱动器。',
    '可透過位址列中的磁碟機名稱切換到其他可存取的磁碟機。'
  ],
  '表示枚数モードやスクロール状態はBookmarkへ保存できます。': [
    'Bookmark can preserve the card-count mode and scroll position.',
    'Bookmark可保存显示数量模式与滚动位置。',
    'Bookmark可儲存顯示數量模式與捲動位置。'
  ],
  '：gid発行、作者フォルダ化などを実行します。': [': issue gids, convert folders to creator folders, and more.', '：执行gid发放、作者文件夹化等操作。', '：執行gid發放、作者資料夾化等操作。'],
  '：選択したRARを書庫構造とファイル名を保ってZIPへ変換します。': [
    ': convert selected RAR archives to ZIP while preserving archive structure and file names.',
    '：在保持压缩包结构与文件名的情况下，将所选RAR转换为ZIP。',
    '：在保留壓縮檔結構與檔名的情況下，將所選RAR轉換為ZIP。'
  ],
  '：作者フォルダから対応画面へ移動します。': [': open the corresponding view from a creator folder.', '：从作者文件夹跳转到相应页面。', '：從作者資料夾前往對應頁面。'],
  '登録した起動プログラムを使い、拡張子に応じた外部アプリで開けます。': [
    'Use registered launch programs to open files in external applications by extension.',
    '可使用已登记的启动程序，根据扩展名用外部应用打开文件。',
    '可使用已登錄的啟動程式，依副檔名用外部應用程式開啟檔案。'
  ],
  'gid発行について': ['About gid issuance', '关于gid发放', '關於gid發放'],
  '発行桁数と対象拡張子はSettingsで管理します。発行後は対象フォルダを走査し、DB登録とサムネイルキャッシュ作成が続けて行われます。': [
    'Configure gid length and target extensions in Settings. After issuance, the target folder is scanned, records are added to the database, and thumbnail caches are generated.',
    '在Settings中管理gid位数与目标扩展名。发放后会扫描目标文件夹，并继续进行数据库登记与缩略图缓存生成。',
    '在Settings中管理gid位數與目標副檔名。發放後會掃描目標資料夾，並繼續進行資料庫登錄與縮圖快取產生。'
  ],
  'RARをZIPへ変換する理由': ['Why convert RAR to ZIP', '为何将RAR转换为ZIP', '為何將RAR轉換為ZIP'],
  'RARのままでは書庫からサムネイルを取得できなかったり、書庫内の画像枚数を集計できない場合があります。ZIPへ変換することで、Galleryのサムネイル表示と画像枚数の集計を安定させます。': [
    'RAR archives may not provide thumbnails or image counts. Converting them to ZIP makes Gallery thumbnail display and image counting more reliable.',
    'RAR压缩包有时无法获取缩略图或统计包内图片数量。转换为ZIP可提高Gallery缩略图显示与图片数量统计的稳定性。',
    'RAR壓縮檔有時無法取得縮圖或統計檔案內圖片數量。轉換為ZIP可提高Gallery縮圖顯示與圖片數量統計的穩定性。'
  ],

  // 05 — Filters and Tags
  'Galleryで使用する属性とTagの定義・割り当て': [
    'Define and assign attributes and Tags used in Gallery',
    '定义并分配Gallery使用的属性与Tag',
    '定義並指派Gallery使用的屬性與Tag'
  ],
  'Category、Title、Characterの標準名・別名を管理し、Galleryの各区分へマッピングします。': [
    'Manage standard names and aliases for Category, Title, and Character, then map them to Gallery sections.',
    '管理Category、Title、Character的标准名称与别名，并映射到Gallery各区分。',
    '管理Category、Title、Character的標準名稱與別名，並對應至Gallery各區分。'
  ],
  '一覧ファイルを使って定義をまとめて確認・更新できます。Import前にIDと更新列の対応を確認してください。': [
    'Use a list file to review or update definitions in bulk. Check the ID and update-column mapping before importing.',
    '可使用列表文件批量检查或更新定义。导入前请确认ID与更新列的对应关系。',
    '可使用清單檔案批次檢查或更新定義。匯入前請確認ID與更新欄的對應關係。'
  ],
  'Tagの追加、削除、並び替えと、各区分で使用できるTagのマッピングを設定します。': [
    'Add, delete, and reorder Tags, and map available Tags to each section.',
    '添加、删除、排序Tag，并设置各区分可使用的Tag映射。',
    '新增、刪除、排序Tag，並設定各區分可使用的Tag對應。'
  ],
  '作品への登録': ['Assigning to works', '登记到作品', '登錄至作品'],
  'Galleryの作品カードから、Title・Character・Tagを登録または解除します。新規項目は各登録画面からエディタへ移動して追加できます。': [
    'Register or remove Title, Character, and Tag from Gallery work cards. Add new items by opening the editor from each registration dialog.',
    '可从Gallery作品卡片登记或解除Title、Character、Tag。新项目可从各登记窗口跳转到编辑器后添加。',
    '可從Gallery作品卡片登錄或解除Title、Character、Tag。新項目可從各登錄視窗前往編輯器後新增。'
  ],

  // 06 — Creators and Creator Tracking
  '作者単位の集計と継続的なフォローアップ': [
    'Creator-level summaries and ongoing follow-up',
    '按作者汇总并持续跟进',
    '依作者彙總並持續追蹤'
  ],
  'Creators配下の区分を選ぶと、作者カードを一覧表示します。': [
    'Choose a section under Creators to display its creator cards.',
    '选择Creators下的区分即可显示作者卡片列表。',
    '選擇Creators下的區分即可顯示作者卡片清單。'
  ],
  'Rating、Site、Core title、Core tags、作品傾向などで絞り込めます。': [
    'Filter by Rating, Site, Core title, Core tags, work tendencies, and more.',
    '可按Rating、Site、Core title、Core tags、作品倾向等进行筛选。',
    '可按Rating、Site、Core title、Core tags、作品傾向等進行篩選。'
  ],
  '総評価、最終確認日、フォロー日数、課金額などを最大3条件で複合ソートできます。': [
    'Combine up to three sort rules such as total rating, last check date, follow-up days, and spending.',
    '可组合最多三个排序条件，例如总评价、最后确认日期、跟进天数和付费金额。',
    '可組合最多三個排序條件，例如總評價、最後確認日期、追蹤天數和付費金額。'
  ],
  '作者カードの右クリックからCreator Trackingを開きます。': [
    'Right-click a creator card to open Creator Tracking.',
    '右键单击作者卡片可打开Creator Tracking。',
    '右鍵點擊作者卡片可開啟Creator Tracking。'
  ],
  '作者基本情報、作品の傾向、活動場所、ストレージ、課金・購入履歴を作者ごとに記録します。': [
    'Record basic information, work tendencies, activity sites, storage, and subscription or purchase history for each creator.',
    '按作者记录基本信息、作品倾向、活动地点、存储位置以及付费与购买记录。',
    '依作者記錄基本資訊、作品傾向、活動場所、儲存位置以及付費與購買記錄。'
  ],
  '複数作者をタブで開き、切り替え時やアプリ終了時に自動保存します。': [
    'Open multiple creators in tabs. Changes are saved automatically when switching tabs or closing the application.',
    '可在标签页中打开多个作者，并在切换标签页或关闭应用时自动保存。',
    '可在分頁中開啟多個作者，並在切換分頁或關閉應用程式時自動儲存。'
  ],
  '更新アイコンは、その作者のGallery用途フォルダを走査して最新情報を反映します。': [
    'The refresh icon scans that creator\'s Gallery folder and applies the latest information.',
    '更新图标会扫描该作者的Gallery用途文件夹并应用最新信息。',
    '更新圖示會掃描該作者的Gallery用途資料夾並套用最新資訊。'
  ],
  'SUMMARYではファイル数・画像枚数・評価・課金・構成比・書庫履歴を確認できます。': [
    'SUMMARY shows file and image counts, ratings, spending, composition, and archive history.',
    'SUMMARY可查看文件数、图片数、评价、付费、构成比例与压缩包历史。',
    'SUMMARY可查看檔案數、圖片數、評價、付費、構成比例與壓縮檔歷史。'
  ],
  'Core title・Core tagsとは': ['What are Core title and Core tags?', '什么是Core title与Core tags？', '什麼是Core title與Core tags？'],
  '作者ごとにTitle／Tag別のファイル数を集計し、その作者の総ファイル数に対して30%以上を占めるTitleをCore title、TagをCore tagsとして扱います。作者の中心的な作品傾向を素早く把握するための指標です。': [
    'For each creator, files are counted by Title and Tag. A Title or Tag representing at least 30% of that creator\'s total files is treated as a Core title or Core tag. These indicators reveal the creator\'s main tendencies at a glance.',
    '系统按作者统计各Title与Tag的文件数。占该作者总文件数30%以上的Title作为Core title，Tag作为Core tags，用于快速掌握作者的主要作品倾向。',
    '系統依作者統計各Title與Tag的檔案數。占該作者總檔案數30%以上的Title作為Core title，Tag作為Core tags，用於快速掌握作者的主要作品傾向。'
  ],
  '活動場所のフォローアップをONにして日数を設定すると、最終確認日からの経過日数に応じてCreatorsのWarning／Alertフィルタを利用できます。': [
    'Enable follow-up for an activity site and set the number of days to use the Creators Warning and Alert filters based on time since the last check.',
    '将活动地点的跟进设为ON并设置天数后，可根据距最后确认日的经过天数使用Creators的Warning／Alert筛选器。',
    '將活動場所的追蹤設為ON並設定天數後，可根據距最後確認日的經過天數使用Creators的Warning／Alert篩選器。'
  ],

  // 07 — Bookmark, Sticky Notes, and Board
  '作業状態を保存し、画面をまたいでメモを管理': [
    'Save workspace state and manage notes across views',
    '保存工作状态并跨页面管理笔记',
    '儲存工作狀態並跨畫面管理筆記'
  ],
  'Galleryのフィルタやソート、Explorerのタブと分割、Creators／Creator Trackingのタブなど、現在のビューを名前とサムネイル付きで保存します。復元できない要素がある場合は、可能な範囲を再現して内容を通知します。': [
    'Save the current view with a name and thumbnail, including Gallery filters and sorts, Explorer tabs and split view, or Creators and Creator Tracking tabs. If some elements cannot be restored, GalleryBrowser restores what it can and reports the rest.',
    '可将当前视图连同名称与缩略图一起保存，包括Gallery筛选和排序、Explorer标签页与分屏、Creators／Creator Tracking标签页等。若有无法恢复的元素，系统会尽可能重现并报告详情。',
    '可將目前檢視連同名稱與縮圖一起儲存，包括Gallery篩選和排序、Explorer分頁與分割、Creators／Creator Tracking分頁等。若有無法還原的元素，系統會盡可能重現並回報詳情。'
  ],
  '画面ごとの付箋です。ドラッグで移動、端のドラッグでサイズ変更、色変更、プレーンテキスト／Markdown切替ができます。画面や区分、フォルダ、作者に紐づいて保存されます。': [
    'Sticky Notes belong to individual views. Drag to move, drag an edge to resize, change color, or switch between plain text and Markdown. Notes are saved with their view, section, folder, or creator.',
    '便笺按页面保存。可拖动移动、拖动边缘调整大小、更改颜色，并在纯文本与Markdown之间切换。便笺会与页面、区分、文件夹或作者关联保存。',
    '便箋依畫面儲存。可拖曳移動、拖曳邊緣調整大小、變更顏色，並在純文字與Markdown之間切換。便箋會與畫面、區分、資料夾或作者關聯儲存。'
  ],
  'アプリ内とBookmarkに保存された付箋を一覧管理します。本文、色、表示モード、削除の変更は元の付箋にも反映されます。': [
    'Manage all Sticky Notes saved in the application and Bookmarks. Changes to text, color, display mode, or deletion also affect the original note.',
    '集中管理应用内及Bookmark中保存的便笺。正文、颜色、显示模式和删除操作也会同步到原便笺。',
    '集中管理應用程式內及Bookmark中儲存的便箋。內文、顏色、顯示模式和刪除操作也會同步至原便箋。'
  ],

  // 08 — User Metrics
  '区分ごとの保有状況・評価・作者傾向を横断集計': [
    'Cross-section analysis of your collection, ratings, and creator trends',
    '跨区分汇总收藏状况、评价与作者倾向',
    '跨區分彙總收藏狀況、評價與作者傾向'
  ],
  '区分切替トグルで集計対象を選び、ファイル数、画像枚数、評価、作者の作品傾向、サイト、課金額、フォロー日数をダッシュボード形式で確認します。': [
    'Choose a section with the toggle, then review file count, image count, ratings, creator work tendencies, sites, spending, and follow-up days on a dashboard.',
    '通过区分切换按钮选择统计对象，并以仪表板形式查看文件数、图片数、评价、作者作品倾向、网站、付费金额与跟进天数。',
    '透過區分切換按鈕選擇統計對象，並以儀表板形式查看檔案數、圖片數、評價、作者作品傾向、網站、付費金額與追蹤天數。'
  ],
  'Creator・Title・Character・Tagを同じ指標で比較': ['Compare Creator, Title, Character, and Tag using the same metric', '使用同一指标比较Creator、Title、Character与Tag', '使用同一指標比較Creator、Title、Character與Tag'],
  'ファイル数、画像枚数、課金額の時間変化を確認': ['Review changes in file count, image count, and spending over time', '查看文件数、图片数与付费金额随时间的变化', '查看檔案數、圖片數與付費金額隨時間的變化'],
  '関連性': ['Relationships', '相关性', '關聯性'],
  '評価、ファイル規模、特定Tagなど複数軸の関係を可視化': ['Visualize relationships among ratings, collection size, specific Tags, and other dimensions', '可视化评价、文件规模、特定Tag等多个维度之间的关系', '視覺化評價、檔案規模、特定Tag等多個維度之間的關係'],
  '再集計': ['Recalculate', '重新统计', '重新統計'],
  '右上の更新ボタンで現在のDBから最新の指標を生成': ['Generate current metrics from the database with the refresh button at top right', '使用右上角的更新按钮从当前数据库生成最新指标', '使用右上角的更新按鈕從目前資料庫產生最新指標'],

  // 09 — Settings
  '設定は目的別の4カテゴリに分類されています': ['Settings are organized into four purpose-based categories', '设置按用途分为四类', '設定依用途分為四類'],
  'テーマ、アクセントカラー、表示言語など、アプリ全体の見た目を設定します。': [
    'Configure the overall appearance, including theme, accent colors, and display language.',
    '设置应用整体外观，包括主题、强调色与显示语言。',
    '設定應用程式整體外觀，包括主題、強調色與顯示語言。'
  ],
  'Creator Trackingの既定値、新規タブ候補、マウスジェスチャ、キーボードショートカットを設定します。': [
    'Configure Creator Tracking defaults, new-tab candidates, mouse gestures, and keyboard shortcuts.',
    '设置Creator Tracking默认值、新标签页候选、鼠标手势与键盘快捷键。',
    '設定Creator Tracking預設值、新分頁候選、滑鼠手勢與鍵盤快速鍵。'
  ],
  '区分と走査対象、gid、サムネイルキャッシュ、本体DB・キャッシュDB・バックアップを管理します。': [
    'Manage sections and scan targets, gids, thumbnail cache, main and cache databases, and backups.',
    '管理区分与扫描目标、gid、缩略图缓存、主数据库与缓存数据库以及备份。',
    '管理區分與掃描目標、gid、縮圖快取、主資料庫與快取資料庫以及備份。'
  ],
  '起動プログラム、WinRAR、FFmpeg、検索エンジンなど外部機能との連携を設定します。': [
    'Configure integrations such as launch programs, WinRAR, FFmpeg, and search engines.',
    '设置启动程序、WinRAR、FFmpeg、搜索引擎等外部功能的集成。',
    '設定啟動程式、WinRAR、FFmpeg、搜尋引擎等外部功能的整合。'
  ],

  // 10 — Database, cache, and backup
  '失いたくないデータと再生成できるデータを分けて管理': [
    'Separate irreplaceable data from data that can be regenerated',
    '分别管理不可丢失的数据与可重新生成的数据',
    '分別管理不可遺失的資料與可重新產生的資料'
  ],
  'データ': ['Data', '数据', '資料'],
  '主な内容': ['Contents', '主要内容', '主要內容'],
  '扱い': ['Handling', '处理方式', '處理方式'],
  '本体DB': ['Main database', '主数据库', '主資料庫'],
  '作品、属性、Creator Tracking、Bookmark、付箋など': [
    'Works, attributes, Creator Tracking, Bookmarks, Sticky Notes, and more',
    '作品、属性、Creator Tracking、Bookmark、便笺等',
    '作品、屬性、Creator Tracking、Bookmark、便箋等'
  ],
  '定期バックアップ推奨': ['Regular backup recommended', '建议定期备份', '建議定期備份'],
  'キャッシュDB': ['Cache database', '缓存数据库', '快取資料庫'],
  '高速表示のための計算・参照キャッシュ': [
    'Calculated and lookup caches for faster display',
    '用于加快显示的计算与查询缓存',
    '用於加快顯示的計算與查詢快取'
  ],
  '再生成可能': ['Can be regenerated', '可重新生成', '可重新產生'],
  'サムネイル': ['Thumbnails', '缩略图', '縮圖'],
  'Gallery／Explorerで使用する縮小画像': ['Reduced images used by Gallery and Explorer', 'Gallery／Explorer使用的缩略图', 'Gallery／Explorer使用的縮圖'],
  '再構築可能': ['Can be rebuilt', '可重新构建', '可重新建置'],
  'JSON設定': ['JSON settings', 'JSON设置', 'JSON設定'],
  'アプリUIや外部プログラムの設定': ['Application UI and external program settings', '应用界面与外部程序设置', '應用程式介面與外部程式設定'],
  '環境ごとに保持': ['Kept per environment', '按环境保存', '依環境保存'],
  '本体DBの移動・結合': ['Moving or merging the main database', '移动或合并主数据库', '移動或合併主資料庫'],
  '移動や外部DB結合の前にはバックアップを作成します。pCloudは任意機能で、アイドル時の自動バックアップ頻度と保持世代数を設定できます。': [
    'Create a backup before moving or merging an external database. pCloud is optional and supports idle-time backup frequency and snapshot retention settings.',
    '移动数据库或合并外部数据库前请先创建备份。pCloud为可选功能，可设置空闲时自动备份频率与快照保留数量。',
    '移動資料庫或合併外部資料庫前請先建立備份。pCloud為選用功能，可設定閒置時自動備份頻率與快照保留數量。'
  ],
  'pCloud連携の準備': ['Preparing pCloud integration', '准备pCloud集成', '準備pCloud整合'],
  'pCloud DevelopersのMy AppsからGalleryBrowserへ接続するまで': [
    'From My Apps in pCloud Developers to connecting GalleryBrowser',
    '从pCloud Developers的My Apps连接到GalleryBrowser',
    '從pCloud Developers的My Apps連接至GalleryBrowser'
  ],
  'pCloud Developersへログインし、': ['Sign in to pCloud Developers and open ', '登录pCloud Developers并打开', '登入pCloud Developers並開啟'],
  'を開きます。アプリ作成が一時的に利用できない場合は、pCloudサポートへMy Appsの利用またはアプリ作成を依頼します。': [
    '. If app creation is temporarily unavailable, contact pCloud Support and request access to My Apps or app creation.',
    '。如果暂时无法创建应用，请联系pCloud支持团队申请使用My Apps或创建应用。',
    '。如果暫時無法建立應用程式，請聯絡pCloud支援團隊申請使用My Apps或建立應用程式。'
  ],
  '利用可能になったMy Appsでアプリを作成し、発行された': [
    'Create an app in My Apps once it becomes available, and copy the issued ',
    '在可用的My Apps中创建应用，并记下签发的',
    '在可用的My Apps中建立應用程式，並記下核發的'
  ],
  'を控えます。': ['.', '。', '。'],
  'GalleryBrowserの': ['In GalleryBrowser, open ', '在GalleryBrowser中打开', '在GalleryBrowser中開啟'],
  'Settings ＞ Files ＞ データベース ＞ pCloud バックアップ': [
    'Settings > Files > Database > pCloud Backup',
    'Settings > Files > 数据库 > pCloud备份',
    'Settings > Files > 資料庫 > pCloud備份'
  ],
  'でデータ保存リージョン、pCloud内の保存先、OAuth Client IDを入力して設定を保存します。': [
    ', enter the data region, destination in pCloud, and OAuth Client ID, then save the settings.',
    '，输入数据保存区域、pCloud内的保存位置与OAuth Client ID，然后保存设置。',
    '，輸入資料儲存區域、pCloud內的儲存位置與OAuth Client ID，然後儲存設定。'
  ],
  '画面に表示される': ['Register the displayed ', '将在画面上显示的', '將畫面上顯示的'],
  'をpCloud側のアプリ設定へ登録し、pCloud側でも保存します。': [
    ' in the pCloud app settings, then save the settings in pCloud.',
    '登记到pCloud侧的应用设置中，并在pCloud侧保存。',
    '登錄到pCloud端的應用程式設定中，並在pCloud端儲存。'
  ],
  'GalleryBrowserで': ['In GalleryBrowser, click ', '在GalleryBrowser中单击', '在GalleryBrowser中點擊'],
  'を押し、ブラウザでアクセスを許可します。完了表示を確認したらブラウザを閉じ、': [
    ' and allow access in the browser. After confirming completion, close the browser and run ',
    '，并在浏览器中允许访问。确认完成后关闭浏览器，然后执行',
    '，並在瀏覽器中允許存取。確認完成後關閉瀏覽器，然後執行'
  ],
  'を実行します。': ['.', '。', '。'],
  'バックアップ機能をONにし、確認頻度、実行間隔、保持件数、アイドル判定時間を設定して保存します。必要に応じて「今すぐバックアップ」で初回スナップショットを確認します。': [
    'Enable backup, configure the check frequency, execution interval, retention count, and idle threshold, then save. Use Back Up Now to verify the first snapshot if needed.',
    '启用备份，设置检查频率、执行间隔、保留数量与空闲判定时间后保存。必要时使用“立即备份”确认首个快照。',
    '啟用備份，設定檢查頻率、執行間隔、保留數量與閒置判定時間後儲存。必要時使用「立即備份」確認第一個快照。'
  ],

  // 11 — Keyboard shortcuts
  '初期設定。Settingsから割り当てを変更できます': ['Default assignments. You can change them in Settings', '默认分配，可在Settings中更改', '預設指派，可在Settings中變更'],
  'Gallery／Explorerの検索': ['Search Gallery or Explorer', '搜索Gallery或Explorer', '搜尋Gallery或Explorer'],
  'Explorerで親フォルダへ': ['Go to the parent folder in Explorer', '在Explorer中前往上级文件夹', '在Explorer中前往上層資料夾'],
  '選択項目の名前を変更': ['Rename selected item', '重命名所选项目', '重新命名所選項目'],
  'コピー／切り取り／貼り付け': ['Copy / Cut / Paste', '复制／剪切／粘贴', '複製／剪下／貼上'],
  'ExplorerとCreator Trackingのタブ切替は同じショートカットを使用します。入力欄にフォーカスがある場合は、文字編集の操作が優先されることがあります。': [
    'Explorer and Creator Tracking use the same shortcuts for tab switching. Text editing commands may take priority while an input field has focus.',
    'Explorer与Creator Tracking使用相同的标签页切换快捷键。输入框获得焦点时，文本编辑操作可能优先。',
    'Explorer與Creator Tracking使用相同的分頁切換快速鍵。輸入欄位取得焦點時，文字編輯操作可能優先。'
  ],

  // 12 — Troubleshooting
  'まず確認する場所と安全な切り分け方': ['What to check first and how to diagnose safely', '优先检查的位置与安全排查方法', '優先檢查的位置與安全排查方法'],
  'Galleryに作品が表示されない': ['Works do not appear in Gallery', 'Gallery中未显示作品', 'Gallery中未顯示作品'],
  '区分の対象ディレクトリと拡張子、フィルタ選択、検索文字列を確認します。Reset後も表示されなければ、データベースの手動更新を実行してください。': [
    'Check the section target directories and extensions, selected filters, and search text. If works remain hidden after Reset, run a manual database update.',
    '检查区分的目标目录与扩展名、所选筛选器及搜索文字。Reset后仍未显示时，请手动更新数据库。',
    '檢查區分的目標目錄與副檔名、所選篩選器及搜尋文字。Reset後仍未顯示時，請手動更新資料庫。'
  ],
  'ファイル操作後に表示が古い': ['The display is stale after a file operation', '文件操作后显示内容未更新', '檔案操作後顯示內容未更新'],
  '各画面の更新ボタンを使用します。作者フォルダの内容はCreator Tracking右上の更新から作者単位で走査できます。': [
    'Use the refresh button in the relevant view. Scan one creator folder at a time with the refresh button at the top right of Creator Tracking.',
    '请使用各页面的更新按钮。可通过Creator Tracking右上角的更新按钮按作者扫描作者文件夹内容。',
    '請使用各畫面的更新按鈕。可透過Creator Tracking右上角的更新按鈕依作者掃描作者資料夾內容。'
  ],
  'Bookmarkを完全に復元できない': ['A Bookmark cannot be restored completely', 'Bookmark无法完全恢复', 'Bookmark無法完整還原'],
  '保存後に削除・無効化されたフィルタ、存在しなくなったフォルダ、閉じられた作者データは復元できません。表示される確認内容をもとに現在の設定を確認してください。': [
    'Filters deleted or disabled after saving, missing folders, and closed creator data cannot be restored. Review the current settings using the displayed report.',
    '保存后被删除或禁用的筛选器、不再存在的文件夹以及已关闭的作者数据无法恢复。请根据显示的确认信息检查当前设置。',
    '儲存後被刪除或停用的篩選器、不再存在的資料夾以及已關閉的作者資料無法還原。請根據顯示的確認資訊檢查目前設定。'
  ],
  'サムネイルが表示されない／古い': ['Thumbnails are missing or stale', '缩略图未显示或内容过旧', '縮圖未顯示或內容過舊'],
  'Settings ＞ Files ＞ サムネイルキャッシュで保存先と対象ディレクトリを確認し、必要な範囲だけ再構築します。': [
    'Check the destination and target directories under Settings > Files > Thumbnail Cache, then rebuild only the necessary scope.',
    '在Settings > Files > 缩略图缓存中检查保存位置与目标目录，并仅重建必要范围。',
    '在Settings > Files > 縮圖快取中檢查儲存位置與目標目錄，並僅重建必要範圍。'
  ],
  'DBを安全に保ちたい': ['I want to keep the database safe', '希望安全保存数据库', '希望安全保存資料庫'],
  '本体DBの場所を確認し、クラウド同期による直接ロックを避けます。pCloudのスナップショットまたは別媒体への定期コピーを利用してください。': [
    'Verify the main database location and avoid direct locks caused by cloud synchronization. Use pCloud snapshots or regular copies to another medium.',
    '确认主数据库位置，避免云同步造成直接锁定。请使用pCloud快照或定期复制到其他介质。',
    '確認主資料庫位置，避免雲端同步造成直接鎖定。請使用pCloud快照或定期複製到其他媒體。'
  ],
  '本運用へ広げる前に': ['Before full-scale use', '正式使用前', '正式使用前'],
  '設定や機能を変更した後は、必ず小さな対象範囲で結果を確認してから本運用へ広げてください。': [
    'After changing settings or features, always verify the result on a small scope before applying it to your full library.',
    '更改设置或功能后，请务必先在小范围内确认结果，再应用到正式环境。',
    '變更設定或功能後，請務必先在小範圍內確認結果，再套用至正式環境。'
  ],

  // 13 — Acknowledgements
  'GalleryBrowserの発想と開発を支えたソフトウェア・技術へ': [
    'For the software and technologies that inspired and supported GalleryBrowser',
    '致启发并支持GalleryBrowser开发的软件与技术',
    '致啟發並支援GalleryBrowser開發的軟體與技術'
  ],
  'GalleryBrowserは、優れた先行ソフトウェアから得た着想と、公開されたソース、そしてAIを活用した開発環境が結びついて生まれました。': [
    'GalleryBrowser was born from ideas inspired by excellent earlier software, publicly available source code, and an AI-assisted development environment.',
    'GalleryBrowser源于优秀先行软件带来的灵感、公开的源代码，以及使用AI的开发环境。',
    'GalleryBrowser源自優秀先行軟體帶來的靈感、公開的原始碼，以及使用AI的開發環境。'
  ],
  'このアプリを作るきっかけとなったのは、書庫と画像を快適に扱えるZipPlaです。作品ファイルを中心に閲覧・整理する体験の出発点になりました。': [
    'ZipPla, which handles archives and images with ease, inspired the creation of this application. It was the starting point for browsing and organizing around work files.',
    '能够便捷处理压缩包与图片的ZipPla，是创建本应用的契机，也成为以作品文件为中心进行浏览与整理的起点。',
    '能夠便捷處理壓縮檔與圖片的ZipPla，是建立本應用程式的契機，也成為以作品檔案為中心進行瀏覽與整理的起點。'
  ],
  '公開されたZipPlaのソース': ['Published ZipPla source code', '公开的ZipPla源代码', '公開的ZipPla原始碼'],
  'ZipPlaのソースをforkし、Git上に公開してくださった方がいたことで、動作や設計を学び、新しいアプリとして発展させるための大切な手がかりを得られました。': [
    'The person who forked the ZipPla source and published it on Git made it possible to study its behavior and design, providing an important foundation for developing a new application.',
    '感谢有人fork ZipPla源代码并发布到Git，使我们得以学习其运行方式与设计，并获得发展为新应用的重要线索。',
    '感謝有人fork ZipPla原始碼並發布至Git，使我們得以學習其運作方式與設計，並獲得發展為新應用程式的重要線索。'
  ],
  'ファイルを実体の場所だけに縛らず整理するFenrirFSのエイリアス管理から、GalleryBrowserの属性・別名管理の考え方に大きな影響を受けています。': [
    'FenrirFS alias management, which organizes files beyond their physical locations, strongly influenced GalleryBrowser attribute and alias management.',
    'FenrirFS不受文件实际位置限制的别名管理方式，对GalleryBrowser的属性与别名管理理念产生了很大影响。',
    'FenrirFS不受檔案實體位置限制的別名管理方式，對GalleryBrowser的屬性與別名管理理念產生了很大影響。'
  ],
  'Codexによって、構想をコードへ落とし込み、実際に動くアプリとして継続的に開発することが可能になりました。': [
    'Codex made it possible to turn concepts into code and continuously develop them as a working application.',
    '借助Codex，构想得以转化为代码，并能够持续开发为实际运行的应用。',
    '藉由Codex，構想得以轉化為程式碼，並能夠持續開發成實際運作的應用程式。'
  ],
  'これらのソフトウェア、公開活動、開発技術に深く感謝します。': [
    'Deepest thanks to these applications, open-source contributions, and development technologies.',
    '衷心感谢这些软件、公开贡献与开发技术。',
    '衷心感謝這些軟體、公開貢獻與開發技術。'
  ]
};
