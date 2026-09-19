namespace BetterVirtualDesktop.App.Localization;

/// <summary>Every row contains English, Korean, Simplified/Traditional Chinese and Japanese.</summary>
internal static class UiTextResources
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Languages = Build();

    private static Dictionary<string, Dictionary<string, string>> Build()
    {
        string[] codes = ["en", "ko", "zh-Hans", "zh-Hant", "ja"];
        var languages = codes.ToDictionary(code => code, _ => new Dictionary<string, string>(StringComparer.Ordinal));
        void Add(string key, string en, string ko, string hans, string hant, string ja)
        {
            string[] values = [en, ko, hans, hant, ja];
            for (int i = 0; i < codes.Length; i++) languages[codes[i]].Add(key, values[i]);
        }

        Add("Close", "Close ×", "닫기 ×", "关闭 ×", "關閉 ×", "閉じる ×");
        Add("NewDesktop", "New desktop", "새 데스크톱", "新建桌面", "新增桌面", "新しいデスクトップ");
        Add("MoveNewDesktop", "Create a desktop and move here", "새 데스크톱을 만들고 이동", "新建桌面并移入", "新增桌面並移入", "新しいデスクトップを作成して移動");
        Add("MoveFailed", "Could not move the window. Check the window and destination display.", "창을 이동할 수 없습니다. 창과 대상 디스플레이를 확인해 주세요.", "无法移动窗口。请检查窗口和目标显示器。", "無法移動視窗。請檢查視窗和目標顯示器。", "ウィンドウを移動できません。ウィンドウと移動先のディスプレイを確認してください。");
        Add("MoveSucceeded", "Window moved", "창을 이동했습니다", "窗口已移动", "視窗已移動", "ウィンドウを移動しました");
        Add("NewDesktopMoveFailed", "Could not move the window to a new desktop. Check the window and destination display.", "새 데스크톱으로 창을 이동할 수 없습니다. 창과 대상 디스플레이를 확인해 주세요.", "无法将窗口移至新桌面。请检查窗口和目标显示器。", "無法將視窗移至新桌面。請檢查視窗和目標顯示器。", "新しいデスクトップにウィンドウを移動できません。ウィンドウと移動先のディスプレイを確認してください。");
        Add("NewDesktopCreatedTitle", "{0} created", "{0} 생성 완료", "已创建{0}", "已建立{0}", "{0}を作成しました");
        Add("WindowMovedTitle", "Window moved to {0}", "{0}(으)로 창을 옮겼습니다", "窗口已移至{0}", "視窗已移至{0}", "{0}にウィンドウを移動しました");
        Add("NewDesktopPreviewHint", "Preview only · Click or Enter to open", "미리보기 중 · 클릭하거나 Enter를 눌러 열기", "仅预览 · 单击或按 Enter 打开", "僅預覽 · 按一下或按 Enter 開啟", "プレビュー中 · クリックまたは Enter で開く");
        Add("Previewing", "Previewing", "미리보기", "预览中", "預覽中", "プレビュー中");
        Add("ActiveDesktop", "Active desktop", "현재 데스크톱", "当前桌面", "目前桌面", "現在のデスクトップ");
        Add("DesktopDefault", "Desktop {0}", "데스크톱 {0}", "桌面 {0}", "桌面 {0}", "デスクトップ {0}");
        Add("PreviewHeader", "{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}", "{0} · {1}");
        Add("OverlayAccessible", "Desktop task view for {0}", "{0} 데스크톱 작업 보기", "{0}的桌面任务视图", "{0}的桌面工作檢視", "{0}のデスクトップタスクビュー");
        Add("OverlayHelp", "← → Select · Enter Switch · Esc Close   |   Drag a window to a desktop or right-click to move", "← → 선택 · Enter 전환 · Esc 닫기   |   창을 데스크톱으로 끌어 놓거나 우클릭하여 이동", "← → 选择 · Enter 切换 · Esc 关闭   |   拖动窗口至桌面，或右键单击以移动", "← → 選取 · Enter 切換 · Esc 關閉   |   將視窗拖曳至桌面，或按右鍵移動", "← → 選択 · Enter 切り替え · Esc 閉じる   |   ウィンドウをデスクトップにドラッグ、または右クリックで移動");
        Add("MoreWindows", "   |   Scroll for more windows ({0}–{1})", "   |   휠: 창 더 보기 ({0}–{1})", "   |   滚动查看更多窗口（{0}–{1}）", "   |   捲動以查看更多視窗（{0}–{1}）", "   |   スクロールで他のウィンドウを表示（{0}–{1}）");
        Add("EmptyDesktop", "Empty desktop", "빈 데스크톱", "空桌面", "空白桌面", "空のデスクトップ");
        Add("NoWindows", "No windows on this desktop", "이 데스크톱에는 열린 창이 없습니다", "此桌面没有打开的窗口", "此桌面沒有開啟的視窗", "このデスクトップにはウィンドウがありません");
        Add("Dialog", "Dialog", "대화상자", "对话框", "對話方塊", "ダイアログ");
        Add("MinimizedWindow", "Minimized window", "최소화된 창", "已最小化的窗口", "已最小化的視窗", "最小化されたウィンドウ");
        Add("PreviewUnavailable", "Preview unavailable", "미리보기를 사용할 수 없습니다", "预览不可用", "無法預覽", "プレビューを表示できません");
        Add("WindowHint", "{0} · Click to open · Drag / right-click to move to another desktop or display", "{0} · 클릭: 열기 · 드래그 / 우클릭: 다른 데스크톱·디스플레이로 이동", "{0} · 单击打开 · 拖动或右键单击，移至其他桌面或显示器", "{0} · 按一下開啟 · 拖曳或按右鍵，移至其他桌面或顯示器", "{0} · クリックで開く · ドラッグ / 右クリックで別のデスクトップやディスプレイへ移動");
        Add("DropToMove", "Drop to move", "놓아서 이동", "松开以移动", "放開以移動", "ドロップして移動");
        Add("NewDesktopHint", "Click to create a desktop · Drop a window to create a desktop and move it there", "클릭: 새 데스크톱 · 창을 끌어 놓기: 새 데스크톱을 만들고 창 이동", "单击新建桌面 · 拖入窗口以新建桌面并移入", "按一下新增桌面 · 拖入視窗以新增桌面並移入", "クリックで新規作成 · ウィンドウをドロップすると新しいデスクトップを作成して移動");
        Add("DesktopHint", "{0} · {1} windows · Click to switch · Right-click to rename / delete · Drop a window to move", "{0} · {1}개 창 · 클릭: 전환 · 우클릭: 이름 / 삭제 · 창을 끌어 놓아 이동", "{0} · {1}个窗口 · 单击切换 · 右键单击重命名或删除 · 拖入窗口以移动", "{0} · {1}個視窗 · 按一下切換 · 按右鍵重新命名或刪除 · 拖入視窗以移動", "{0} · ウィンドウ{1}個 · クリックで切り替え · 右クリックで名前変更 / 削除 · ドロップで移動");
        Add("DesktopMenu", "Desktop menu", "데스크톱 메뉴", "桌面菜单", "桌面選單", "デスクトップメニュー");
        Add("Rename", "Rename", "이름 변경", "重命名", "重新命名", "名前を変更");
        Add("RemoveDesktop", "Delete desktop · Move windows to an adjacent desktop", "데스크톱 삭제 · 창은 인접 데스크톱으로 이동", "删除桌面 · 将窗口移至相邻桌面", "刪除桌面 · 將視窗移至相鄰桌面", "デスクトップを削除 · ウィンドウは隣のデスクトップへ移動");
        Add("ThisDisplaySuffix", " · This display", " · 이 디스플레이", " · 此显示器", " · 此顯示器", " · このディスプレイ");
        Add("CurrentSuffix", " · Current", " · 현재", " · 当前", " · 目前", " · 現在");
        Add("DesktopNameTitle", "Desktop name", "데스크톱 이름", "桌面名称", "桌面名稱", "デスクトップ名");
        Add("Save", "Save", "저장", "保存", "儲存", "保存");
        Add("DisplayDefault", "Display {0}", "디스플레이 {0}", "显示器 {0}", "顯示器 {0}", "ディスプレイ {0}");
        Add("PrimaryDisplaySuffix", " · Primary", " · 주 모니터", " · 主显示器", " · 主要顯示器", " · メイン");
        Add("SystemLanguage", "Use system language", "시스템 언어 사용", "使用系统语言", "使用系統語言", "システムの言語を使用");
        Add("Language", "Language", "언어", "语言", "語言", "言語");
        Add("OpenDesktop", "Open desktop · Enter", "데스크톱 열기 · Enter", "打开桌面 · Enter", "開啟桌面 · Enter", "デスクトップを開く · Enter");
        Add("CurrentDesktopHint", "Currently active: {0}", "현재 데스크톱: {0}", "当前桌面：{0}", "目前桌面：{0}", "現在のデスクトップ：{0}");
        Add("CreationComplete", "Created", "생성 완료", "已创建", "已建立", "作成完了");
        Add("WindowMovedNotice", "Window moved", "창 이동 완료", "窗口已移动", "視窗已移動", "ウィンドウの移動完了");
        Add("DesktopCreatedNotice", "Desktop created", "데스크톱 생성 완료", "桌面已创建", "桌面已建立", "デスクトップの作成完了");
        Add("LanguageSaveFailed", "Could not save the language setting. {0}", "언어 설정을 저장하지 못했습니다. {0}", "无法保存语言设置。{0}", "無法儲存語言設定。{0}", "言語設定を保存できませんでした。{0}");
        Add("Error.LanguageSave", "Could not save the language setting.", "언어 설정을 저장하지 못했습니다.", "无法保存语言设置。", "無法儲存語言設定。", "言語設定を保存できませんでした。");

        Add("Tray.Overlays", "Overlay on all displays ({0})", "모든 화면에 오버레이 ({0})", "在所有显示器显示叠加视图（{0}）", "在所有顯示器顯示覆蓋檢視（{0}）", "すべての画面にオーバーレイ（{0}）");
        Add("Tray.Management", "Manage desktops", "관리 창", "管理桌面", "管理桌面", "デスクトップの管理");
        Add("Tray.NamePosition", "Desktop name position", "데스크톱 이름 표시 위치", "桌面名称显示位置", "桌面名稱顯示位置", "デスクトップ名の表示位置");
        Add("Tray.TopRight", "Top right", "오른쪽 위", "右上角", "右上角", "右上");
        Add("Tray.TopLeft", "Top left", "왼쪽 위", "左上角", "左上角", "左上");
        Add("Tray.PauseResume", "Pause / Resume", "일시 정지 / 다시 시작", "暂停 / 恢复", "暫停 / 繼續", "一時停止 / 再開");
        Add("Tray.RestorePause", "Restore all windows and pause", "모든 창 복구 후 일시 정지", "恢复所有窗口并暂停", "還原所有視窗並暫停", "すべてのウィンドウを復元して一時停止");
        Add("Tray.Exit", "Exit · Restore all windows", "종료 · 모든 창 복구", "退出 · 恢复所有窗口", "結束 · 還原所有視窗", "終了 · すべてのウィンドウを復元");
        Add("Tray.Language", "Language", "언어", "语言", "語言", "言語");
        Add("Tray.SystemLanguage", "Use system language", "시스템 언어 사용", "使用系统语言", "使用系統語言", "システムの言語を使用");
        Add("Guardian.StoppedTitle", "Window recovery monitor stopped", "창 복구 감시 중단", "窗口恢复监控已停止", "視窗還原監控已停止", "ウィンドウ復元の監視が停止しました");
        Add("Guardian.StoppedBody", "All windows were restored and desktop switching was paused. Please restart the app.", "모든 창을 복구하고 일시 정지했습니다. 앱을 다시 시작해 주세요.", "已恢复所有窗口并暂停桌面切换。请重新启动应用。", "已還原所有視窗並暫停桌面切換。請重新啟動應用程式。", "すべてのウィンドウを復元し、切り替えを一時停止しました。アプリを再起動してください。");
        Add("Error.PausedTitle", "Desktop switching paused", "작업 전환 일시 정지", "桌面切换已暂停", "桌面切換已暫停", "デスクトップの切り替えを一時停止しました");
        Add("Error.PausedBody", "An error occurred and window recovery was requested. See the app log for details.", "오류가 발생하여 창 복구를 요청했습니다. 앱 로그에서 내용을 확인할 수 있습니다.", "发生错误，已请求恢复窗口。详情请查看应用日志。", "發生錯誤，已要求還原視窗。詳情請查看應用程式記錄。", "エラーが発生したため、ウィンドウの復元を要求しました。詳細はアプリのログを確認してください。");
        Add("Startup.AlreadyRunning", "BetterVirtualDesktop is already running. Open the overlay from its notification-area icon.", "이미 실행 중입니다. 알림 영역 아이콘에서 오버레이를 여세요.", "应用已在运行。请使用通知区域图标打开叠加视图。", "應用程式已在執行。請使用通知區域圖示開啟覆蓋檢視。", "すでに実行中です。通知領域のアイコンからオーバーレイを開いてください。");
        Add("Startup.NoExecutable", "Could not locate the executable.", "실행 파일 경로를 찾을 수 없습니다.", "找不到可执行文件。", "找不到執行檔。", "実行ファイルが見つかりません。");
        Add("Startup.HelperFailed", "Could not start the helper process.", "보조 프로세스를 시작할 수 없습니다.", "无法启动辅助进程。", "無法啟動輔助處理程序。", "補助プロセスを起動できません。");
        Add("Startup.GuardianFailed", "Could not start the window recovery monitor.", "창 복구 감시 프로세스를 시작하지 못했습니다.", "无法启动窗口恢复监控进程。", "無法啟動視窗還原監控處理程序。", "ウィンドウ復元の監視プロセスを起動できませんでした。");

        Add("Overview.Title", "BetterVirtualDesktop · Manage desktops", "BetterVirtualDesktop · 관리 창", "BetterVirtualDesktop · 管理桌面", "BetterVirtualDesktop · 管理桌面", "BetterVirtualDesktop · デスクトップの管理");
        Add("Overview.Hint", "Switch only the display under the pointer.  ·  {0} / {1}", "마우스가 있는 모니터만 전환합니다.  ·  {0} / {1}", "仅切换鼠标所在的显示器。  ·  {0} / {1}", "僅切換滑鼠所在的顯示器。  ·  {0} / {1}", "マウスポインターがある画面だけを切り替えます。  ·  {0} / {1}");
        Add("Overview.PauseResume", "Pause / Resume", "일시 정지 / 재개", "暂停 / 恢复", "暫停 / 繼續", "一時停止 / 再開");
        Add("Overview.Restore", "Restore all windows", "모든 창 복구", "恢复所有窗口", "還原所有視窗", "すべてのウィンドウを復元");
        Add("Overview.Back", "Back · Esc", "돌아가기 · Esc", "返回 · Esc", "返回 · Esc", "戻る · Esc");
        Add("Overview.Paused", "Paused · All windows are visible · Resume to apply the desktop layout.", "일시 정지됨 · 모든 창 표시 · 재개하면 데스크톱 구성이 적용됩니다.", "已暂停 · 显示所有窗口 · 恢复后将应用桌面布局。", "已暫停 · 顯示所有視窗 · 繼續後將套用桌面配置。", "一時停止中 · すべてのウィンドウを表示 · 再開するとデスクトップの配置が適用されます。");
        Add("Overview.Status", "{0} displays  ·  {1} windows  ·  Click to open  /  Right-click to move", "{0}개 디스플레이  ·  {1}개 창  ·  클릭: 열기  /  우클릭: 이동", "{0}台显示器  ·  {1}个窗口  ·  单击打开 / 右键单击移动", "{0}台顯示器  ·  {1}個視窗  ·  按一下開啟 / 按右鍵移動", "ディスプレイ{0}台  ·  ウィンドウ{1}個  ·  クリックで開く / 右クリックで移動");
        Add("Overview.Unmanageable", "  ·  {0} unavailable", "  ·  제어 불가 {0}개", "  ·  {0}个无法控制", "  ·  {0}個無法控制", "  ·  操作不可{0}個");
        Add("Overview.Add", "+ Add desktop", "+ 데스크톱 추가", "+ 添加桌面", "+ 新增桌面", "+ デスクトップを追加");
        Add("Overview.DesktopHint", "{0} · Right-click to rename / delete", "{0} · 우클릭: 이름 변경 / 삭제", "{0} · 右键单击以重命名或删除", "{0} · 按右鍵重新命名或刪除", "{0} · 右クリックで名前変更 / 削除");
        Add("Overview.DesktopAccessible", "{0}, {1}, {2}", "{0}, {1}, {2}", "{0}，{1}，{2}", "{0}，{1}，{2}", "{0}、{1}、{2}");
        Add("Overview.Active", "Active desktop", "현재 데스크톱", "当前桌面", "目前桌面", "現在のデスクトップ");
        Add("Overview.Switch", "Switch", "전환", "切换", "切換", "切り替え");
        Add("Overview.WindowCount", "{0} windows", "{0}개 창", "{0}个窗口", "{0}個視窗", "ウィンドウ{0}個");
        Add("Overview.VisibleSuffix", "  ·  Currently visible", "  ·  현재 표시 중", "  ·  当前显示中", "  ·  目前顯示中", "  ·  現在表示中");
        Add("Overview.Empty", "Empty desktop\nOpen a window here to get started.", "비어 있는 데스크톱\n여기에서 새 창을 열어 보세요.", "空桌面\n在此打开一个窗口即可开始。", "空白桌面\n在此開啟視窗即可開始。", "空のデスクトップ\nここで新しいウィンドウを開いてください。");
        Add("Overview.ClosedWindow", "Closed window", "종료된 창", "已关闭的窗口", "已關閉的視窗", "閉じたウィンドウ");
        Add("Toast.Accessible", "Active desktop: {0}, {1}, {3} of {2}", "현재 데스크톱: {0}, {1}, {2}개 중 {3}번째", "当前桌面：{0}，{1}，第{3}个，共{2}个", "目前桌面：{0}，{1}，第{3}個，共{2}個", "現在のデスクトップ：{0}、{1}、{2}個中{3}番目");
        Add("Toast.Caption", "{0}  ·  {1} / {2}", "{0}  ·  {1} / {2}", "{0}  ·  {1} / {2}", "{0}  ·  {1} / {2}", "{0}  ·  {1} / {2}");

        Add("Shortcuts.Title", "Keyboard shortcuts", "키보드 단축키", "键盘快捷键", "鍵盤快速鍵", "キーボードショートカット");
        Add("Shortcuts.Open", "Keyboard shortcuts…", "단축키 설정", "键盘快捷键…", "鍵盤快速鍵…", "キーボードショートカット…");
        Add("Shortcuts.Description", "Choose a key and modifiers for each action. Changes apply when you save.", "동작별 키와 조합 키를 선택하세요. 저장하면 변경 사항이 적용됩니다.", "为每项操作选择按键和修饰键。保存后应用更改。", "為每個動作選擇按鍵和組合鍵。儲存後套用變更。", "操作ごとにキーと修飾キーを選択します。変更は保存時に適用されます。");
        Add("Shortcuts.Note", "Assigning Win+Tab replaces Windows Task View while this app runs. Disable that binding to use Windows Task View again. Other apps may use the same shortcuts.", "Win+Tab을 지정하면 앱 실행 중 Windows 작업 보기 대신 이 앱이 열립니다. 해당 지정을 해제하면 Windows 작업 보기를 다시 사용할 수 있습니다. 다른 앱의 단축키와 겹칠 수 있습니다.", "指定 Win+Tab 后，本应用运行期间将替代 Windows 任务视图。禁用该绑定可恢复 Windows 任务视图。其他应用可能使用相同快捷键。", "指定 Win+Tab 後，本應用程式執行期間將取代 Windows 工作檢視。停用該設定即可恢復 Windows 工作檢視。其他應用程式可能使用相同快速鍵。", "Win+Tab を割り当てると、アプリの実行中は Windows タスクビューの代わりにこのアプリが開きます。割り当てを無効にすると元に戻ります。他のアプリと重複する場合があります。");
        Add("Shortcuts.Action", "Action", "동작", "操作", "動作", "操作");
        Add("Shortcuts.Modifiers", "Modifiers", "조합 키", "修饰键", "組合鍵", "修飾キー");
        Add("Shortcuts.Key", "Key", "키", "按键", "按鍵", "キー");
        Add("Shortcuts.Preview", "Shortcut", "단축키", "快捷键", "快速鍵", "ショートカット");
        Add("Shortcuts.DefaultPreset", "App defaults", "앱 기본값", "应用默认值", "應用程式預設值", "アプリの初期設定");
        Add("Shortcuts.WindowsPreset", "Windows shortcuts", "Windows 기본키 사용", "Windows 快捷键", "Windows 快速鍵", "Windows のショートカット");
        Add("Shortcuts.PresetHint", "Presets change the draft. Save to apply.", "프리셋을 선택한 후 저장하여 적용하세요.", "预设仅修改草稿，保存后应用。", "預設組合僅變更草稿，儲存後才會套用。", "プリセットは編集中の設定に反映されます。保存すると適用されます。");
        Add("Shortcuts.Save", "Save changes", "변경 사항 저장", "保存更改", "儲存變更", "変更を保存");
        Add("Shortcuts.Cancel", "Cancel", "취소", "取消", "取消", "キャンセル");
        Add("Shortcuts.Disabled", "Disabled", "사용 안 함", "已禁用", "已停用", "無効");
        Add("Shortcuts.SaveFailed", "Could not save the shortcuts. {0}", "단축키를 저장하지 못했습니다. {0}", "无法保存快捷键。{0}", "無法儲存快速鍵。{0}", "ショートカットを保存できませんでした。{0}");
        Add("Shortcuts.Command.ToggleOverview", "Show / hide the overlay", "오버레이 열기 / 닫기", "显示 / 隐藏叠加视图", "顯示 / 隱藏覆蓋檢視", "オーバーレイを開く / 閉じる");
        Add("Shortcuts.Command.PreviousDesktop", "Previous desktop", "이전 데스크톱", "上一个桌面", "上一個桌面", "前のデスクトップ");
        Add("Shortcuts.Command.NextDesktop", "Next desktop", "다음 데스크톱", "下一个桌面", "下一個桌面", "次のデスクトップ");
        Add("Shortcuts.Command.MoveWindowPrevious", "Move window to previous desktop", "창을 이전 데스크톱으로 이동", "将窗口移至上一个桌面", "將視窗移至上一個桌面", "ウィンドウを前のデスクトップへ移動");
        Add("Shortcuts.Command.MoveWindowNext", "Move window to next desktop", "창을 다음 데스크톱으로 이동", "将窗口移至下一个桌面", "將視窗移至下一個桌面", "ウィンドウを次のデスクトップへ移動");
        Add("Shortcuts.Command.AddDesktop", "Create and open a desktop", "데스크톱 만들고 열기", "创建并打开桌面", "建立並開啟桌面", "デスクトップを作成して開く");
        Add("Shortcuts.Command.RemoveDesktop", "Delete current desktop", "현재 데스크톱 삭제", "删除当前桌面", "刪除目前桌面", "現在のデスクトップを削除");
        Add("Shortcuts.Command.RestoreAll", "Restore all windows and pause", "모든 창 복구 후 일시 정지", "恢复所有窗口并暂停", "還原所有視窗並暫停", "すべてのウィンドウを復元して一時停止");
        Add("Shortcuts.Validation.InvalidKey", "Choose a supported key for “{0}”.", "“{0}”에 사용할 수 있는 키를 선택해 주세요.", "请为“{0}”选择支持的按键。", "請為「{0}」選擇支援的按鍵。", "「{0}」に対応するキーを選択してください。");
        Add("Shortcuts.Validation.NeedModifier", "Choose Win, Ctrl, or Alt for “{0}”. Shift can be added too.", "“{0}”에 Win, Ctrl, Alt 중 하나 이상을 선택해 주세요. Shift도 추가할 수 있습니다.", "请为“{0}”选择 Win、Ctrl 或 Alt，也可同时添加 Shift。", "請為「{0}」選擇 Win、Ctrl 或 Alt，也可同時加入 Shift。", "「{0}」に Win、Ctrl、Alt のいずれかを選択してください。Shift も追加できます。");
        Add("Shortcuts.Validation.Reserved", "The shortcut for “{0}” is reserved by Windows. Choose another combination.", "“{0}”의 단축키는 Windows에서 사용합니다. 다른 조합을 선택해 주세요.", "“{0}”的快捷键由 Windows 保留。请选择其他组合。", "「{0}」的快速鍵由 Windows 保留。請選擇其他組合。", "「{0}」のショートカットは Windows が予約しています。別の組み合わせを選択してください。");
        Add("Shortcuts.Validation.Duplicate", "“{0}” and “{1}” use the same shortcut. Change one of them.", "“{0}”와 “{1}”의 단축키가 같습니다. 하나를 변경해 주세요.", "“{0}”与“{1}”使用相同快捷键。请更改其中一个。", "「{0}」與「{1}」使用相同快速鍵。請變更其中一個。", "「{0}」と「{1}」が同じショートカットです。どちらかを変更してください。");
        return languages;
    }
}
