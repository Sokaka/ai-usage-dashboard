# AI Usage 技術總覽

本文件供開發與維護人員閱讀，集中說明程式架構、資料保存方式及各服務的串接細節。一般使用者請改讀[使用說明](../使用說明.md)。

功能是否完成及尚待驗證的項目，以[實作檢查清單](../IMPLEMENTATION_CHECKLIST.md)為準；可下載版本請看 [README 的「目前版本」](../README.md#目前版本)。本文件只說明目前原始碼的設計，不保存逐次審查與驗證歷史。

## 閱讀方式

- 想確認資料存在哪裡，請讀「帳號與偏好資料」。
- 想維護某一項服務，請直接讀該服務的「串接」章節。
- 想查功能是否完成或尚待驗證，請改讀[實作檢查清單](../IMPLEMENTATION_CHECKLIST.md)。
- 想找可下載版本，請讀 [README 的「目前版本」](../README.md#目前版本)。

本文會保留程式碼中的英文識別名稱。必要的技術名稱第一次出現時，會先說明實際行為。

## 程式提供的功能

- WPF 浮窗是唯一主介面，可查看用量並完成帳號新增、編輯、啟用、停用、排序、移除、連接與復原。
- Claude、Codex、GitHub Copilot 與 Grok 支援多個帳號。每張卡片平時都使用自己的登入資料與用量。Copilot 只有在使用者操作連接時，會暫時使用官方 CLI 共用的登入清單，詳見後文。AGY 使用目前 Windows 使用者的單一設定，因此暫限一張卡片。
- 系統匣提供顯示、隱藏、置頂、使用說明、關於與結束程式；帳號管理可用時也能匯出設定，本次執行仍有可用還原點時則能還原匯入前設定。隱藏浮窗不會停止背景更新。
- 同一個 Windows 使用者同時只能執行一份 AI Usage。再次啟動時會帶回既有浮窗，不會開出第二份程式。
- 展開浮窗的停靠角落與收合圖示在工作區內的自訂位置，以及置頂、收合、主題與排序偏好會保存在本機。圖示拖曳時依所在螢幕的工作區與邊距限制位置；工作區或 DPI 改變時依儲存的相對座標重新定位。舊版 `Dashboard`、`Widget`、`DashboardAndWidget` 與 `Tray` 值仍可讀取，避免升級後遺失顯示偏好。
- 每張卡片都有自己的快取。同一張卡片已有查詢在執行時，不會再啟動重複查詢；查詢失敗時可保留上次成功的資料並標成舊資料。讀到快取不會標示成剛完成更新。
- 動態狀態、表單標籤、按鈕與選單都提供鍵盤、螢幕閱讀器與 Windows 高對比模式所需的資訊。

## 啟動與版本資訊

標準安裝的 Updater 透過 `ManagedInstallationRegistrar` 建立目前 Windows 使用者的開始選單捷徑，指向固定的 `current/app/AiUsageDashboard.App.exe`。更新沿用同一目標；自訂安裝位置與免安裝版不建立捷徑。`ManagedStartMenuShortcut` 核對目標、工作目錄、參數、描述、圖示、視窗狀態與 hotkey，只有相符項目可在解除安裝時清理；同名衝突、損壞或不安全的路徑會保留並回報警告。

`WindowsShellShortcut` 使用 Windows `IShellLinkW`／`IPersistFile` 的固定 COM 介面建立及讀回捷徑，並成對釋放 interface 與 COM initialization，避免 trimmed Updater 依賴動態 COM 包裝。此處只處理捷徑，不變更帳號資料或登入自動啟動設定。安裝及復原操作見[分發與支援手冊](../INTERNAL_DISTRIBUTION.md)。

浮窗與系統匣共用一個 `AboutWindow`，從 App assembly 的 `AssemblyInformationalVersion` 取得完整版本，保留預覽版與 source revision 資訊；未指定版本的開發建置使用 `0.0.0-dev`。使用者可複製版本、開啟隨包使用說明，或前往固定的 GitHub Releases／Issues 網址。單純開啟「關於」不查詢 provider 或更新服務；只有按下 **檢查更新**才會立即查詢。

## App 更新偵測與提示

正式 App build 會嵌入 stable feed URL、channel 與可信公鑰。`AppUpdateBuildDefaults` 只有在三項資料全部缺少時才將功能標成 `UnavailableInThisBuild`；任一項部分缺漏或格式無效都會使 production publish／runtime 明確失敗，不會把無法檢查的 build 顯示成最新版。`Publish-Internal.ps1` 與 `Publish-UpdateBundle.ps1` 使用同一組外部公鑰資料建立 App 與 Updater，私鑰不進 App。

`SignedUpdateFeedClient` 是 `Updater.Core` 的唯讀能力：只接受 HTTPS、限制 feed 大小、核對 redirect 最終 URI，再以既有 RSA-PSS canonical JSON 規則驗證簽章、channel 與 artifacts。App 不從 GitHub tag、HTML 或 API 判斷版本，也不下載 ZIP／Updater；真正安裝時，maintenance Updater 會再次取得並驗證 feed，執行 anti-downgrade、自我更新、transaction switch 與重新啟動。

執行中的固定 maintenance Updater 若因 Windows image lock 無法被 delegated 新版覆寫，新版會先留在 maintenance root 的 content-addressed generation。delegated 新版只有在自身 version／size／SHA-256 符合 signed feed、執行路徑是該 artifact 的固定 cache path，且由 Windows 取得的 direct parent exact PID／UTC start time／image path 確認為固定 maintenance Updater 時，才以當下 canonical hash 作 compare-and-swap 條件，原子保存 promotion receipt 並啟動該 generation 的 internal promoter。promoter 會持續等待 exact parent 自然離開，再取得 canonical install root 的 `UpdateInstallLock`；只有 receipt 仍精確指向自己的工作可重新驗證 generation／固定入口 hashes，並從同目錄 temporary file 原子提升。這個 ownership 由新版 child 負責，因此已發布、尚不具 promotion 邏輯的舊 Updater 也能進入更新鏈。被較新 generation 取代或 receipt 已清除的 promoter 會安全結束；pending retry 只有在 install lock 內確認完整 receipt snapshot 未被取代時才能重新綁定，避免舊 retry 蓋回新版 handoff。成功會清除相符 receipt；失敗會保存有界錯誤。固定入口已具 retry 能力時，下次 online 啟動會在讀取 feed 前重試；若初次 bootstrap 後固定入口仍是尚無此能力的舊版，則需由相容 transition feed 再次成功 delegation。Windows uninstall registration 會先修正到目前可執行的 Updater，之後才清理不再被引用的舊 generation；硬中止遺留的嚴格命名 promotion temporary file則由解除安裝的 maintenance ownership cleanup 清除，近似名稱、directory 與 reparse point 仍 fail closed 保留。

`AppInstallationContextDetector` 依目前 executable 與相鄰 `update-manifest.json` fail closed 分類：

- `CanonicalManaged`：exact `%LOCALAPPDATA%\Programs\AiUsageDashboard\current\app\AiUsageDashboard.App.exe`、有效 manifest 與相同 payload identity。只有這一類、固定 maintenance Updater 存在，且 `UpdateShutdownChannel.StartAsync` 已確認 pipe listener ready 時，才顯示 **更新並重新啟動**。
- `CustomManaged`：符合 `<derivedRoot>\current\app` 與相鄰有效 manifest，但不在 canonical path。可以用 manifest 偵測新版，只開固定 Releases，不將自訂 root 猜成安裝目標。
- `Unmanaged`：portable ZIP、單獨複製的 EXE，以及任何路徑／manifest 歧義。去除正式 artifact `ProductVersion` 的 build metadata 後比較 `ReleaseVersion`；無法解析時是 `UnknownCurrentVersion`，不會顯示 up-to-date。

`UpdateCheckCoordinator` 維持單一 observed request。手動檢查立即執行並 join 既有 request；自動檢查先顯示 non-modal 說明並保留 30 秒關閉時間，成功後等待 24 小時，失敗後依 `15m → 1h → 4h → 24h` 退避。每次 timer tick 與 Windows resume 都重新以 UTC 判斷 due time；持久排程若超前目前時間超過 5 分鐘，或首次說明的 30 秒 gate 遇到時鐘回撥，會視為 clock anomaly 並重新建立節流。關閉自動檢查時停止自動網路查詢；若有 snooze，只保留不連網的本機到期 tick，手動檢查仍可用。

presentation／排程 cache 寫在 `%LOCALAPPDATA%\AiUsageDashboard\update-check-state-v1.json`，採 strict schema、bounded read、same-directory temporary file、flush 與 atomic replace。它只保存目前版本、前次結果、最高已見 sequence、失敗次數、balloon attempt key 與依 `version + releaseSequence` 設定的 24 小時 snooze，不保存下載 URL、Updater path 或 install decision，也不是 trust input。讀取損壞視為 cache miss；一般寫入失敗會保留記憶體狀態並寫 diagnostic，不阻塞 App。自動檢查開關若無法保存，當次執行仍立即套用，但介面會明確警告重新啟動後可能恢復舊設定。

展開浮窗使用獨立 update banner；收合狀態在一般主題使用專屬珊瑚紅通知色的圓形 badge，搭配反色圓環與向量 `↑`，不會重用代表錯誤的 `DangerBrush`；High Contrast 則改用系統 `WindowTextColor`／`WindowColor`。同一 composer 會更新 `AutomationProperties.Name`、`HelpText` 與 tooltip。Banner 的 live-region announcement 以首次說明或 `version + releaseSequence` 為 key；刷新中、視窗未啟用或收合時先保留 pending，恢復可宣告狀態後補發，同一可見週期只宣告一次，snooze 到期重新出現時再宣告。Tray 永久保留手動檢查與自動檢查開關，另依狀態顯示安裝或 Releases action。Windows balloon 只在浮窗 hidden／collapsed／inactive、未 snooze 且該 release key 未嘗試時補充提示；persistent banner、badge 與 tray 才是可靠狀態來源。

## 主題與配色

浮窗右上角的 **⋯** → **主題配色** 選單提供四種主題；切換後會立即更新浮窗，並在下次啟動時沿用：

| 顯示名稱 | `AppTheme` | 配色檔 |
| --- | --- | --- |
| 經典藍 | `ClassicBlue` | `Themes/Palette.xaml` |
| 曜石黑 | `Midnight` | `Themes/MidnightPalette.xaml` |
| 柔霧灰 | `Light` | `Themes/LightPalette.xaml` |
| 櫻花粉 | `Sakura` | `Themes/SakuraPalette.xaml` |

主題與浮窗高度依設定格式版本處理：

- 從版本 4 起，`DashboardShellPreferences.Theme` 會寫入 `%LOCALAPPDATA%\AiUsageDashboard\preferences.json`；舊版本沒有主題欄位時使用 `ClassicBlue`。
- `IsHeightFollowingCardCount` 從版本 6 起寫入；舊本機設定預設維持固定可用高度。
- 本機設定版本 7 會成對儲存收合圖示的相對 X／Y 座標與螢幕識別；舊版沒有座標時，仍依原本的停靠角落顯示。匯出設定不包含這組僅適用於原電腦的座標與螢幕識別。
- 匯出設定檔版本 6 會寫入 `isHeightFollowingCardCount`。匯入版本 3–5 時保留目標電腦目前的自動高度設定；版本 3 另會保留目前主題；版本 1／2 同時保留目前浮窗偏好與主題。

一般主題共用相同的資源鍵與控制項樣式。介面透過 `DynamicResource` 取得配色；切換時，`App.UpdatePaletteResources` 會直接更新仍可修改的 `SolidColorBrush`，其餘資源則換成目標配色的值，避免已取得的筆刷物件仍顯示舊主題。浮窗展開與收合時的 Logo 都使用 `ThemeLogoStyle` 與配色資源；執行中的視窗標題列與系統匣圖示由同一 Logo 產生，切換主題或 Windows 高對比時更新。執行檔、開始選單捷徑及安裝項目仍使用固定的封裝圖示，工作列顯示也可能受 Windows 的合併與圖示快取影響。

帳號設定、Codex 連接與重置券、關於、Antigravity 連接視窗都使用 App 配色；Antigravity 連接視窗保留獨立但與 App 對齊的按鈕樣式。互動啟動需要顯示法律條款時，App 會先唯讀取得已選主題，條款視窗使用同一組背景、文字與按鈕資源。Windows 原生標題列、系統對話框及系統匣選單仍由 Windows 繪製。

從展開浮窗開啟的重置券、排序規則與關於視窗使用非模態顯示；重置券依帳號卡片、排序規則與關於視窗各自重用已開啟的實例。浮窗收合或隱藏時同步隱藏這些視窗，展開或顯示時恢復。從系統匣開啟的關於視窗獨立顯示並保留工作列入口。帳號編輯、Codex workspace 選擇及確認視窗維持模態，關閉後才繼續依結果執行操作。

一般主題的捲動滑塊沒有外框，平常的 `ScrollBarThumbColor` 與用量條底色 `ProgressTrackColor` 相同。滑鼠移入與拖曳時保留同色系，逐步小幅提高與背景的色差，避免狀態切換時突然變亮或變深。可見滑塊維持 6 DIP，操作區域維持 18 DIP。Windows 高對比模式保留系統文字色與選取色。

平台色與狀態色採固定角色分工：

- 帳號卡片以平台名稱的文字色識別服務，不使用平台商標圖示或彩色邊緣裝飾。`AccountTitleStyle` 依 `ProviderKind` 選取對應的 `ProviderTextBrush`；名稱 `Run` 使用 `Segoe UI Bold`，方案與暱稱的 `AccountHeaderSuffixText` 則使用 `PrimaryTextBrush`，保留標題的 `SemiBold` 字重。
- 正常用量進度條使用對應的 `ProviderAccentBrush`。名稱與進度條保留相同色系，但文字色會依背景調整：`ClassicBlue`／`Midnight` 略提亮名稱，`ClassicBlue` 的 Grok 已接近白色，因此維持原亮度；`Light`／`Sakura` 的名稱略深於進度條，以保留小字對比。各 `ProviderTextBrush` 都透過 `DynamicResource` 參照同主題的 `ProviderTextColor`，供切換主題時更新。
- `UsageLevel` 依 `UsedPercent` 判定：超過 80% 為 `Warning`，達到 100% 為 `Critical`。即使畫面切換為顯示剩餘用量，狀態仍以已使用比例計算。用量進度條與數字在 `Warning` 時改用 `WarningBrush`／`WarningTextBrush`，`Critical` 時改用 `DangerBrush`／`DangerTextBrush`；平台名稱不隨用量警示改色。
- `AccountStatusSeverity.Warning` 與 `AccountStatusSeverity.Critical` 的狀態標記、復原提示及文字，分別使用 `Warning*` 與 `Danger*` 資源。警告因此維持黃色系，危險與錯誤維持紅色系，不會被平台色取代。
- 浮窗的帳號設定健康提示使用 `Warning*` 資源及 12 DIP 文字，與一般資訊提示區分。
- 開啟 Windows 高對比模式時，`HighContrastPalette.xaml` 會暫時取代目前配色，平台名稱與後綴改用系統文字色；正常進度條使用系統強調色，用量警示仍由對應的系統色資源覆寫。關閉高對比模式後，程式會重新套用使用者目前選擇的主題。

## 各服務如何取得用量

| 服務 | 資料來源 | 串接限制 | 資料來源的狀態 |
| --- | --- | --- | --- |
| Claude | Claude Code `/usage`；必要時改讀既有 status line 的 `rate_limits` | 只對 Claude.ai Pro／Max／Team 執行 `/usage`；Enterprise 僅辨識訂閱資訊，不執行 `/usage`、無法顯示用量 | 官方命令的實驗性背景查詢；status line 由官方事件觸發 |
| Codex | `codex app-server` 的 `account/read`、`account/rateLimits/read` | 只接受 ChatGPT account，登入及查詢都在帳號專用目錄內執行 | 官方、實驗性本機介面 |
| Grok | Grok Build CLI ACP stdio 的 `x.ai/billing`、`x.ai/auth/info`；舊 `_x.ai/*` namespace 相容 | 每張卡片使用獨立設定；只有通過來源檢查的官方 CLI 才能執行 | 通過簽章與路徑驗證的官方本機 CLI；實驗性 ACP |
| GitHub Copilot | 官方 `GitHub.Copilot.SDK` 1.0.11 的 Experimental `account.getQuota`；使用本機官方 Copilot CLI | 只支援不同的 `github.com` 帳號；每張卡片使用自己的 token | 官方 SDK／CLI 的 Experimental 介面 |
| Antigravity | 官方 AGY CLI print mode `/usage` | 使用目前 Windows 使用者的單一登入來源；連接時在 App process 內執行一次性本機設定 | 官方命令的實驗性背景查詢；production 不使用 ConPTY 或 status line |

功能完成、實機驗證與尚待測試的項目，請看[實作檢查清單](../IMPLEMENTATION_CHECKLIST.md)。

AI Usage 不匯入瀏覽器 cookie。Claude、Codex 與 Grok 的登入及憑證都交由各自的官方工具保存。

Copilot 只在使用者明確連接時，透過受控的官方 CLI 網頁登入流程取得並驗證選定帳號；完成後將每張卡片的 token 存入 Windows Credential Manager。日常背景查詢不再借用 CLI 的共用登入清單，也不使用從執行環境繼承的憑證。

### 版本檢查

| 服務 | 排查問題時的比較版本 | 允許嘗試的版本 | 執行前必須符合 |
| --- | --- | --- | --- |
| Claude | `2.1.169` | 格式固定的三段版本 `>=2.1.169` | `<2.1.169` 缺少目前命令參數（argv）所需的 `--safe-mode` 功能；產品身分、來源、簽章與路徑仍須通過 |
| Codex | `0.144.1` | 可辨識為 `codex-cli x.y.z` 的非零三段版本 | 無法確認產品、版號、執行檔來源或程序能安全隔離時停止 |
| GitHub Copilot | SDK `1.0.11`；CLI 基準 `1.0.79` | 本機官方 CLI 的三段式正式版 `>=1.0.79` 且 `<2.0.0` | 核對 `GitHub, Inc.` Authenticode 與 ProductName，再執行受保護副本；只接受 `github.com` 與每張卡片明確提供的 token；帳號主體或額度驗證失敗時停止採用，訂閱資訊失敗另行提示 |
| Grok | `1.0.3` | 通過來源驗證的官方執行檔；版號不同或無法解析都可進行 ACP 初始交握（handshake） | 只接受固定預設路徑、固定磁碟、所有上層路徑都不是 reparse point、安全 ACL、通過 WinVerifyTrust，且簽署者必須精確符合程式固定的 `X.AI LLC` |
| AGY | `1.1.11` | 標準三段穩定版 `1.1.11 <= version < 2.0.0` | `<1.1.11` 缺少官方 print mode 功能；`>=2.0.0`、預發行格式（prerelease）與非標準版本格式一律在執行前拒絕，因為執行後無法撤回可能影響 |

表中的「排查問題時的比較版本」只供本版排查故障時比對，不是允許版本清單，也不表示候選版本已用該精確版本完成試用測試。

- 允許嘗試但不同於基準的版本，成功時不顯示警告。
- 命令、通訊協定（protocol）、資料格式（schema）或未知回應失敗時，服務保留原始錯誤分類與復原動作，再附上偵測版本、相容性基準，以及「版本差異可能是原因之一」的提示。
- 明確未登入或授權失效、計費錯誤（billing）、帳號主體衝突（principal conflict）、來源驗證、程序隔離、本機 OS／I/O、逾時（timeout）、已分類的上游服務暫時錯誤（server transient），以及無法安全繼續的錯誤，不附版本提示，也不會因此改變原始錯誤分類。

### 執行官方 CLI 前的檔案保護

Windows 上的 Claude、Codex、Copilot 與 Grok 不直接執行原始安裝檔。程式會先鎖住安裝檔、驗證來源，再複製到受保護的資料夾：

- 用不允許其他程序寫入或刪除的檔案控制代碼（handle）鎖住原始檔。
- 確認檔案位於固定磁碟（fixed drive）、完整路徑沒有重新導向點（reparse point）、檔案不超過 512 MiB，並通過 WinVerifyTrust 與各服務要求的簽署者（signer）檢查。
- 從同一個檔案控制代碼複製檔案並計算 SHA-256。
- 暫存檔與完成提交（commit）後的副本，都會重新驗證上層資料夾 ACL、SHA-256 與簽章。完成驗證後，同一個已驗證副本控制代碼（staged file handle）會持續保持唯讀鎖。

每個服務會在下列資料夾中，依檔案內容的 SHA-256 分開保存副本：

```text
%SystemDrive%\AiUsageDashboard.ClaudeCli.<current-user-SID>\executables-v1
%SystemDrive%\AiUsageDashboard.CodexCli.<current-user-SID>\executables-v1
%SystemDrive%\AiUsageDashboard.CopilotCli.<current-user-SID>\executables-v1
%SystemDrive%\AiUsageDashboard.GrokCli.<current-user-SID>\executables-v1
```

受保護目錄建立時就套用 DACL，只讓目前使用者的 SID、Local System 與 Builtin Administrators 擁有 Full Control。其他一般使用者沒有存取權。

每次版本檢查、登入與背景查詢都會取得獨立的唯讀鎖，直到最後一個程序或傳輸確認結束。讀到快取時仍會檢查路徑、ACL、檔案身分與變更標記；標記改變時會重新建立受保護副本。

每個服務正常保留目前來源對應的副本與前一個有效副本。程式會盡力清理更舊副本；若檔案仍被使用，或路徑、ACL、重新導向點與刪除檢查失敗，則保留檔案並在之後重試，所以短期內可能超過兩份。

暫存檔不列入有效副本。當次失敗會嘗試立即清除；當機遺留的固定名稱暫存檔，只有在受保護目錄、路徑與 ACL 都再次通過檢查，且至少一小時未更新時才會刪除。

執行檔鎖只有在確認沒有程序再使用該副本後才會釋放，不能只看前景等待逾時或方法已返回。

- Claude 與 Codex 的終止、等待和輸出讀取若逾時，會交由背景清理工作接手，完成後才釋放鎖。
- 若 `wait` 與 `HasExited` 都無法確認程序已離開，執行檔鎖與帳號操作鎖會保留到本次 App 結束；背景仍會在固定上限內重試終止。
- `Kill(entireProcessTree: true)` 部分或全部失敗時，也不會把根程序已結束當成整棵程序樹已清空。
- Claude 尚未確認程序樹已清空時，鎖會和這次執行的識別碼（attempt ID）一起保留。
- Grok 無法確認程序樹已清空時，會停止新的 Grok 程序，並保留所有使用中副本的鎖到 App 結束。

### 查詢失敗後如何重試

已串接的服務共用同一個復原原則。暫時性、未知但可安全再試，或這次資料不完整的錯誤，一律回傳 `UsageRecoveryAction.Retry`。共用背景查詢流程會逐步加長重試間隔，但不超過固定上限，也不因失敗次數停止；App 每次啟動也會立即開始背景查詢，接續可恢復狀態。

`Retry` 狀態不提供復原按鈕（recovery button），也不要求使用者操作。只有下列情況才顯示需要使用者介入的操作：

- 已明確登出或授權失效。
- 需要安裝或更新。
- 來源不受支援，或通訊協定（protocol）缺少必要功能。
- 帳號識別確定衝突。
- 程式無法證明再次執行是安全的。

帳號識別保存與移除後的本機狀態清理，也會在背景依 1、2、4、8、最多 15 分鐘的間隔重試。重試到期時間會跨每次背景更新（refresh tick）保留，不會每 10 秒重新寫入磁碟。這些維護工作與正常帳號的用量查詢並行；單一帳號暫時無法保存或清理，不會阻塞其他帳號。

## 帳號與偏好資料

帳號設定儲存在：

```text
%LOCALAPPDATA%\AiUsageDashboard\accounts.json
```

目前的 `accounts.json` 使用格式版本 7（schema 7）。每張卡片包含 ID、服務種類（Provider）、選填暱稱、啟用狀態、`providerAccountIdentity`、`hasAcceptedClaudeQuotaRisk` 與 `showSubscriptionContext`。

- `providerAccountIdentity` 是卡片的公開綁定識別。
  - 新版 Claude 卡片與指定工作區的 Codex 卡片，會在此欄保存隨機產生、不可反推出帳號的 ID。實際帳號、Claude 組織或 Codex 工作區的加鹽 SHA-256 指紋另存於卡片的私有綁定檔，不保存原始值。
  - 未指定工作區的 Codex 卡片仍直接保存正規化後的帳號識別，目前是 email。舊版卡片也可能在重新連接前保留可讀的舊識別。
- Copilot 使用正規化的 `github.com` 主機名稱與 GitHub `node_id` 建立 SHA-256 識別；即使登入名稱（login）改名，綁定也不會改變。Grok 在此檔只保存隨機公開 ID，實際帳號的加鹽 SHA-256 指紋另存於私有綁定檔。
- AGY 的 print mode 用量資料不含 email，因此使用固定的本機工作階段識別；production 不從 status line 補讀 email 或方案，也不把這些欄位寫入 `accounts.json`。
- `hasAcceptedClaudeQuotaRisk` 只記錄該張 Claude 卡片是否已接受 `/usage` 的殘餘額度風險。新增 Claude 卡片時，只有使用者在風險說明下按 **儲存並連接** 才會寫入 `true`；只儲存、舊格式版本、非 Claude 帳號與匯入卡片都視為 `false`。
- `showSubscriptionContext` 決定卡片是否顯示已驗證的方案、組織或工作區資訊。格式版本 7 以前的帳號預設不顯示。
- 上述欄位都不是 token、PAT、OAuth 憑證、cookie 或其他機密資料。
- Copilot token 依卡片 ID 存在 Windows Credential Manager；不會進入 `accounts.json`、用量快取、匯出的設定檔或診斷紀錄。未來其他服務若需由 App 保存憑證，也必須使用作業系統的憑證保管區，不可寫入此 JSON。

未綁定的卡片不能使用磁碟快取建立首次綁定。Claude 必須在官方連接流程中確認帳號、組織與訂閱方案，才會保存訂閱綁定；其他服務依各自的連接流程驗證即時帳號資料。

Claude 卡片缺少公開綁定 ID，或仍保存舊的帳號識別時，會回傳 `NotConfigured + ConfirmSubscription`，顯示「需要確認 Claude 訂閱」。此狀態只表示訂閱連接需要確認，不推定資料損壞；整份 `accounts.json` 的 schema 版本也不能代表每張卡片是否已完成連接。背景查詢保留帳號與磁碟上的舊用量，不採用舊用量作為目前可用資料，也不自行建立新綁定。使用者可沿用原帳號完成官方登入，確認訂閱範圍，再選擇舊用量的保留、清除或取消。

公開綁定 ID 已符合新版格式但私有綁定檔遺失時，顯示連接資料不完整；綁定內容或對應 ID 無效時，顯示連接資料無效。一般 I/O 或存取權限錯誤仍回傳 `Error + Retry`，與需要確認訂閱分開處理。

之後進行背景查詢或在重啟後載入快取時，帳號識別必須與已保存的綁定一致。若識別明確不一致，或服務明確回報未登入，程式會忽略該筆用量並要求重新連接。若只有單次回應暫時缺少識別，程式會忽略該筆資料、保留既有綁定並自動重試。

AGY 的識別只證明卡片綁定這台電腦目前核准的 AGY 登入來源，不證明 email 或方案。登入切換不會自動搬移快取、重綁卡片或略過帳號識別衝突；需要切換時必須重新連接並核對用量。

重複綁定依服務實際提供的額度範圍判斷：

- Claude：同一登入帳號可建立多張卡片，但每張必須對應不同的組織。同一個「登入帳號＋組織 ID」組合不可重複。
- Codex：同一登入帳號可建立多張卡片，但每張必須對應不同的工作區（workspace）。同一個「登入帳號＋workspace」組合不可重複。
- Copilot、Grok：同一實際帳號不可綁定到多張卡片。
- AGY：目前只允許一張卡片。

不同服務即使回傳相同字串，仍視為不同帳號。若綁定無法安全寫入，這次用量不會寫入快取，卡片也會回復到上次已保存的識別。

手動排序時，`accounts` 陣列順序就是浮窗順序。新增帳號會插在同一服務的最後一張卡片後方；若目前沒有相同服務，則依自動排序使用的服務順位選擇插入位置，但不重排既有帳號。自動排序只調整畫面，不改寫該陣列。其他顯示偏好另存於：

```text
%LOCALAPPDATA%\AiUsageDashboard\preferences.json
```

每次成功儲存都會維護一份通過驗證的 `accounts.json.bak`。主檔遺失或 JSON 損壞時，程式會先驗證備份，再以原本的卡片 ID 還原；損壞主檔會保留為 `accounts.corrupt-*.json`。只有主檔與備份都無效時，才會從空白帳號清單啟動。若資料由較新的格式版本建立，程式會保留原檔、不予修改，並停用帳號編輯。

### 匯入設定檔

匯入會先在記憶體檢查版本、欄位、數量、深度與大小，再顯示預覽。各版本保留的偏好如下：

- 格式版本 6 包含浮窗顯示、收合、置頂、停靠角落、主題與依卡片內容自動調整高度。
- 格式版本 4／5 沒有自動高度欄位；版本 3 另沒有主題；版本 1／2 只有排序與用量顯示。
- 所有版本都排除只適用於原電腦的顯示器識別。匯入檔缺少的欄位，會保留目標電腦目前的偏好。

Copilot 卡片的 ID、暱稱與順序會保留，但帳號綁定與登入 token 不會匯出。匯入後必須逐卡重新連接。

套用 `accounts.json`、`accounts.json.bak` 與 `preferences.json` 前，程式會建立復原紀錄，保存三個檔案原本的狀態。三個檔案都寫入成功後，這次匯入才算完成。

- 匯入與啟動復原共用跨程序獨占鎖，避免兩份 AI Usage 同時修改同一批檔案。
- 若程式在寫完前中止，下次啟動會先依復原紀錄還原三個檔案，也不會誤刪原有的 Copilot 登入資料。
- 三個設定檔都寫入完成後，程式才會清除已保留卡片的舊 Copilot 登入資料與私有登入目錄。若清理中途停止，下次啟動會接著完成。

寫入前會再次比對目前檔案的雜湊值（hash），避免覆寫最後一次檢查後的外部變更。Windows 無法在這裡依檔案內容完成不可分割的「比對後交換」（atomic compare-and-swap），因此不保證能保留未遵守同一鎖定協定的第三方程式，在最後檢查與取代檔案之間寫入的內容。

成功匯入後，程式內只保留一份精確的匯入前資料。重新啟動，或後續修改匯入所追蹤的欄位，都會使這個還原點失效。`IsWidgetVisible` 不列入比對，讓使用者隱藏浮窗後仍能從系統匣還原；舊版匯入檔缺少的欄位也不列入比對。用量快取與執行中的狀態不會復原。

## Claude 串接

### 帳號隔離與連接

Claude 採使用者明確啟用、優先在背景執行 `/usage` 查詢的本機實驗方案。`2.1.169` 同時供排查故障時比對，也是 `--safe-mode` 的功能下限；更高的標準三段版本不會只因精確版號不同而被拒絕。每張可顯示用量的帳號卡片使用獨立的 Claude.ai Pro、Max 或 Team 登入與設定目錄：

```text
%LOCALAPPDATA%\AiUsageDashboard\claude\<account-id>\config
```

候選 Claude Code 原始執行檔必須通過前述共用檔案保護流程的來源檢查，且簽署者（signer）為 `Anthropic, PBC`。版本檢查、登入與 `/usage` 都只執行 `%SystemDrive%\AiUsageDashboard.ClaudeCli.<current-user-SID>\executables-v1\claude-<sha256>.exe` 下重新驗證過的受保護副本。

每張卡片的安全狀態、自動重試階段與期限另存於
`%LOCALAPPDATA%\AiUsageDashboard\claude\<account-id>\usage-safety-v1.json`。
檔名為相容既有安裝而維持不變，目前內容使用格式版本 3。
發生可能與版本有關的錯誤時，程式會保存當次已驗證的正式 CLI 版本；
重新載入後再依本版規則建立診斷。格式版本 1／2 仍可讀取，缺少的資料不會靠猜測補上。

在不啟動 CLI 的狀態變更中，程式會先把預定變更寫成待完成紀錄
（程式中的 `TransitionIntentDocument`）。這筆紀錄一旦安全寫入磁碟，即使主要狀態檔暫時無法替換，
下次存取或 App 重啟也會先完成變更，不會把已確認可恢復的結果誤判成中斷作業。

舊格式版本 1 狀態的來源資訊也會保留。只有結果已能證明命令完整結束時，
結束碼衝突標記才會改成自動重試。若舊版的中斷、取消或零用量無法驗證標記
沒有「程序已全部結束」的證據，程式會等待人工確認，不靠推測自動重跑。

每次真正執行 `/usage` 前，App 會先保存具有唯一 attempt ID 的 `Prepared` 狀態。程序會先以暫停狀態建立，並在建立時加入這次執行專用的具名 Windows Job Object；確認加入成功且 `StartedContained` 已寫入磁碟後，程序才會開始執行。

App 或主機意外中止時，Job 的「關閉時終止程序」功能（kill-on-close）會終止整棵程序樹。下次啟動會重新開啟同一個 Job，必要時終止殘留程序；確認程序樹已清空後，才改成自動重試。

若清理尚未得到程序樹已清空的證據，attempt ID 與執行檔鎖會一起保留。下一次 `/usage` 必須先完成復原，不能直接重跑。

一般版本與登入檢查若未在前景期限內完成清理，程序和鎖會交給背景工作持有，直到終止、離開與輸出讀取全部完成。只有舊格式版本 1 的執行中標記，或新版明確標為 `Uncontained` 的狀態，才必須等待人工確認。

帳號移除若遇到尚未完成的受控執行，會依序處理：

1. 先從狀態標記移除帳號識別，並安全寫入磁碟。
2. 以同一個 attempt ID 確認整棵程序樹已結束；確認前不會提交清除結果。
3. 確認後寫入不含帳號識別的待完成清理紀錄（程式中的 `ClearIntentDocument`），
   再以有上限的重試清理舊狀態與待完成變更紀錄。

實體檔案若暫時無法刪除，後續存取仍會依清理紀錄繼續處理。
若尚未確認程序已結束，不含帳號識別的 attempt 標記會保留到下次啟動重試。

App 啟動時也會在固定時間內掃描 Claude 資料根目錄的第一層。
只接受名稱為 32 位十六進位 GUID、且不是重新導向點（reparse point）的帳號目錄；
不會遞迴搜尋或穿越目錄連結（junction）。

- 仍存在於帳號設定中的卡片，包括停用卡片，只完成既有的待完成清理紀錄
  並清除名稱完全相符的暫存檔，保留原本的安全狀態。
- 只有帳號設定已確認可管理時，不再存在於設定中的帳號目錄才會清除。
  程式會先確認相關程序全部結束，再採用和正常移除帳號相同的流程。
- 掃描、程序復原或清理失敗會寫入診斷紀錄，並加入背景重試；
  後續啟動也會接著處理，不會阻止 App 啟動。

使用者必須先接受「程式只能在 `/usage` 執行後檢查結果」的殘餘風險，AI Usage 才會啟動官方登入與查詢。新增 Claude 卡片並立即連接時，新增畫面的風險說明與 **儲存並連接** 會一併記錄同意，不再顯示第二個視窗；只儲存、舊版或匯入卡片則在第一次從卡片連接時顯示一次確認。拒絕時不會登入或執行 `/usage`，背景更新也不會繞過這個同意狀態。

### 背景查詢流程

AI Usage 直接啟動 Claude 執行檔，不透過 shell，並依序執行版本檢查、登入狀態檢查與 `/usage`。版本與登入檢查沿用一般受控程序執行器（process runner）；真正可能產生用量的 `/usage` 使用上述可在當機後復原的 Job 執行器（crash-contained Job runner）：

```text
claude -p /usage
  --output-format json
  --max-turns 1
  --permission-mode dontAsk
  --no-session-persistence
  --safe-mode
  --tools ""
```

子程序會移除可能覆寫帳號來源的 API key 與雲端服務環境變數。執行 `/usage` 前還會確認：

- 執行檔身分可確認為 Claude Code，版本是格式固定的三段版本，且不低於 `2.1.169` 功能下限。
- `auth status --json` 顯示已登入。
- 登入方式為 `claude.ai`、`firstParty`；方案為 Pro、Max 或 Team 時才執行 `/usage`。Enterprise 可辨識訂閱資訊，但目前不執行 `/usage`，也無法顯示用量。

Claude CLI 暫時不存在時，帳號專用設定與憑證不會被刪除；安裝或更新後會自動重試。

### 額度安全機制

只有下列條件全部成立時，AI Usage 才接受背景查詢結果：

- 模型互動次數（model turn）為 0。
- input、output 與 cache token 全部精確為 0。
- 費用（cost）為 0。
- 沒有權限遭拒（permission denial）。
- `usage` 欄位符合已驗證的資料格式（schema）；出現未知欄位或型別變更時會停止採用結果。
- 結果可省略 `local_command` 與 `result_index`；若有提供，必須分別為 `usage` 與數值 0。其他 turn／token／cost 檢查仍須全部通過。
- `fast_mode_*` 只當作有長度上限的狀態附加資料；新增狀態值不影響零 turn／token／cost 判定，也不會單獨觸發安全停止標記。

暫時性安全驗證失敗不會要求使用者處理。處理方式如下：

- 服務回傳 `UsageRecoveryAction.Retry`。共用背景查詢流程保留上次成功的用量，並在畫面上標成舊資料。
- 背景會約在 1、2 分鐘後重試，之後每 4 分鐘持續重試，不會因失敗次數停止。
- 重試階段、期限、已隔離的執行嘗試與已確認的狀態轉換，都可在 App 重啟後接續。
- 若失敗可能與 CLI 版本有關，安全狀態會保存當時偵測到的版本，讓冷卻期間與 App 重啟後仍能顯示相容性提示。本機 I/O、逾時、登入、重大安全問題或中斷復原狀態不保存這項版本資料。
- App 重啟後會先處理可能尚未結束的程序樹，再按原本的等待期限重試。
- 任一次成功後，程式會先把「清除安全狀態」的意圖寫入磁碟，再移除安全狀態並套用最新用量。

下列情況不會自動反覆執行 `/usage`：

- 已明確看到非零 model turn、token、cost 或 permission denial；
- 安全狀態檔損壞，或確認無法恢復；
- 舊版或 `Uncontained` 執行中標記沒有程序隔離證據。

暫時性的 I/O 或逾時寫入失敗，仍由背景工作在固定上限內重試。只有無法自動安全恢復、確實需要本人確認時，服務才會回傳 `UsageRecoveryAction.RevalidateUsage`，卡片顯示 **重新檢查 Claude 用量**。

按下按鈕後，AI Usage 會鎖定這張卡目前的操作，清除該帳號的安全停止標記、記憶體狀態與背景查詢快取，但不會碰登入資料。接著會重新驗證一次；若再次發現明確的重大安全錯誤，就會再次停止，不必重啟整個程式。

這項執行後檢查只能偵測異常的模型呼叫（invocation），無法撤銷第一次異常呼叫可能已產生的額度（quota）或費用。`/usage` 回傳的是人類可讀文字，不是具版本的機器可讀額度格式（machine-readable quota schema），因此此功能維持實驗標示；CLI 文案或最外層結果格式（result envelope）改變時，解析器會停止採用結果。

新版不再提供狀態列設定（status line setup）或手動 CLI 入口。既有擷取輔助程式與備用讀取方式只為相容舊設定。擷取資料只保存允許清單內的格式版本、Claude Code 版本、模型識別、擷取時間、`HasCurrentUsage` 狀態與兩個額度週期；不保存工作目錄、逐字稿路徑、session ID、費用、原始 context token 或完整 JSON input。

### 分發與驗證條件

Anthropic 的身分驗證政策不允許第三方開發者代表使用者提供 Claude.ai 登入、代表使用者以 Free、Pro、Max 方案的憑證傳送請求，或收集、保存及居間處理 Claude.ai 憑證／session token。

現行政策同時說明，若產品遵守 `Commercial Terms`、執行 Anthropic 發布且未修改的 Claude Code 執行檔、不移除內建身分驗證方式，並由每位終端使用者以自己的憑證驗證及直接付費，終端使用者仍可在該產品中登入 Claude Code。

AI Usage 不收集 Claude 憑證，登入交由 Anthropic 官方流程。執行檔會先複製成位元完全相同、依內容雜湊保存的副本，再從副本執行；但文件核對不能代替合約或法律判定。

Claude 的產品整合條款適用與 child-process 隔離是否構成限制內建 auth 仍有不確定性，保留至發布者最後決策；不因個人作品或風險同意而視為已獲許可。保留官方 binary、`--claudeai`、`CLAUDE_CONFIG_DIR`、官方登入及使用者自己的直接計費。各使用者仍須另外接受查詢可能產生用量與費用的風險；詳見 [發布流程](../RELEASING.md)。

官方參考：

- [`/usage` 文件](https://code.claude.com/docs/en/costs#using-the-usage-command)
- [背景 token 用量](https://code.claude.com/docs/en/costs#background-token-usage)
- [Claude Code CLI 參考](https://code.claude.com/docs/en/cli-reference)
- [`CLAUDE_CONFIG_DIR` 參考](https://code.claude.com/docs/en/env-vars)
- [法律條款、產品內執行與憑證使用條件](https://code.claude.com/docs/en/legal-and-compliance)

## Codex 串接

### 帳號隔離與連接

Codex 透過官方 `codex app-server` 的標準輸入輸出（stdio）交換 JSONL 訊息。啟動前會在受控程序中檢查版本，確認格式為非零的 `codex-cli x.y.z` 三段版本，但不限制數值範圍；`0.144.1` 只供排查問題時比對。

每張帳號卡片都有獨立的 `CODEX_HOME` 與 `CODEX_SQLITE_HOME`，由 Codex CLI 管理登入、設定與執行狀態；AI Usage 不讀取或保存 token。

```text
%LOCALAPPDATA%\AiUsageDashboard\codex\<account-id>\home
```

Codex 執行檔解析器（resolver）會先從官方安裝目錄找到實際的 `codex.exe`。來源必須通過共用的受保護副本檢查，且簽署者必須是 `OpenAI OpCo, LLC`。版本檢查、登入與 app-server 背景查詢都只執行下列重新驗證過的副本：

```text
%SystemDrive%\AiUsageDashboard.CodexCli.<current-user-SID>\executables-v1\codex-<sha256>.exe
```

版本檢查使用同一服務目錄下的 `validation-v1\probe-<guid>` 隔離設定目錄。建立後會重新檢查所在位置是固定磁碟、父路徑沒有重新導向點（reparse point），而且 ACL 只允許指定帳號存取；結束時也會再次確認父目錄、GUID 名稱、ACL 與路徑，才遞迴刪除這次建立的目錄。

若無法確認版本檢查的程序已完全受控，相關執行檔鎖會保留到 App 結束。程式也會關閉整個程序的 Codex 啟動入口；後續解析、建立副本、登入與背景查詢都直接拒絕，不再建立另一個隔離鎖。

只有使用者選擇 **儲存並連接**、**連接 Codex 帳號** 或 **切換 Codex 帳號** 時，程式才會建立帳號專用目錄並呼叫 `account/login/start`。背景更新不會自行開啟登入。

完成 ChatGPT 登入後，AI Usage 等待 `account/login/completed`，再清除該帳號的用量快取並執行一輪新的用量檢查。

### 背景查詢流程

每次背景查詢都會啟動一個短時間執行的 `codex app-server --listen stdio://` 程序，依序執行：

```text
initialize → initialized → account/read → account/rateLimits/read
```

流程不建立對話執行緒或模型互動（thread／turn）、不發送 prompt，也不呼叫模型。`account/read` 只接受 ChatGPT account；`account/rateLimits/read` 會讀取 primary、secondary 與 `rateLimitsByLimitId` 的多個額度區間。

`rateLimitResetCredits.availableCount` 是可用重置次數的來源。只有官方 `credits` 明細完整且有效、可用項目數與 `availableCount` 相符，且每張券的到期資訊可判定時，才取尚未到期項目中最早的 `expiresAt` 顯示本機到期時間；距到期 48 小時內，時間改用警示色。顯式 `expiresAt: null` 代表不會到期，缺少 `expiresAt` 則代表到期資訊未知；舊版 count-only 回應、缺少明細或截斷明細仍顯示次數，不推測到期時間。

Codex 卡片的 **查看重置券** 視窗只讀取該卡片目前的用量快照，上方顯示可用總數與不帶服務名稱前綴的帳號，資料時間位於帳號正下方，並列出官方實際回傳且格式有效、狀態為 `available`、目前未確認已到期的逐券名稱與到期資訊。顯式 `expiresAt: null` 顯示「不會到期」，缺少欄位顯示「未提供到期時間」；有到期時間的券沿用卡片的 48 小時警示規則與 `ResetTimeTextStyle`，每張券獨立判斷。視窗保持開啟時會觀察該卡片的快照變更，背景或手動檢查完成後同步內容；主浮窗收合或隱藏時一起隱藏，重新展開或顯示時恢復；帳號身分不符時清空舊資料，帳號被移除或視窗關閉時解除觀察。`redeemed`、`expired` 和未知狀態不佔用可用券明細上限，也不在視窗列出。`availableCount` 仍是來源回傳的可用總數；明細可能為 `null`、空陣列、被截斷或有無效欄位，視窗會提示缺少或不完整的可用券明細，不把可見列數當成可用總數。若來源回傳的 `available` 列數超過 `availableCount`，視窗保留總數但隱藏矛盾的明細並提示重新檢查；若券在快照取得後到期，視窗隱藏該券，將總數標示為上次資料並提示重新檢查。逐券明細只留在記憶體，不保存到磁碟快取或匯出設定；重啟後要等下一次背景更新取得。此視窗不會自行發起 CLI 查詢，也不呼叫會消耗重置券的 `account/rateLimitResetCredit/consume`。

`account/usage/read` 尚未實作，因此 token 活動不會與 rate-limit 額度混在一起顯示。

子程序會移除 `CODEX_ACCESS_TOKEN`、`CODEX_API_KEY` 與 `OPENAI_API_KEY`，並限制 JSONL 訊息與標準錯誤輸出（stderr）的大小。程序結束、逾時、取消或回應格式錯誤（malformed response）時，都會停止採用結果並終止程序。

從程序建立開始，app-server 的通訊管道就由同一個背景清理工作追蹤：

- 即使關閉 stdin pipe、終止程序樹或建立通訊管道中途失敗，或終止後第二次等待仍逾時，程序、剩餘輸出讀取與受保護執行檔副本的鎖仍交由背景工作清理。
- 只有確認程序已結束，而且通訊管道已完成釋放（disposal）後，才會釋放執行檔鎖。前景查詢或登入先返回，不會提前解鎖執行檔。
- 若 `wait` 與 `HasExited` 都無法確認程序已結束，清理工作會把本次作業的鎖保留到 App 重新啟動，並繼續等待，讓有時間上限的清理流程執行逾時／終止路徑。

暫時性的 refresh token 逾時、服務暫時無法使用（service unavailable）、沒有其他說明的 `401` 或 `Unauthorized`，只會安排重試。只有 app-server 明確回報未登入、要求再次登入，或 refresh token 已撤銷、失效、過期、無效或被重複使用時，才會要求重新連接。

`account/read` 若回報 ChatGPT 已登入但沒有可用 email，或回傳 `account: null` 同時明確表示不需要 OpenAI 驗證，代表目前官方介面無法提供建立每張卡片綁定所需的穩定識別。這種狀態會明確顯示帳號尚未完成設定並要求連接，不會被當成暫時錯誤永久背景重試。

官方參考：[Codex App Server](https://learn.chatgpt.com/docs/app-server)、[`CODEX_HOME` 與 `CODEX_SQLITE_HOME`](https://learn.chatgpt.com/docs/config-file/environment-variables)。

## Grok 串接

### 執行檔與帳號隔離

Grok 每張帳號卡片都有獨立的 `GROK_HOME`、空白工作目錄、OAuth 登入與執行狀態：

```text
%LOCALAPPDATA%\AiUsageDashboard\grok\<account-id>\home
```

AI Usage 不從 `PATH` 接受任意 `grok.exe`，只解析目前 Windows 使用者預設安裝位置 `%USERPROFILE%\.grok\bin\grok.exe`。來源必須通過共用的受保護副本檢查，且簽章者為 `X.AI LLC`。

登入與背景查詢只執行 `%SystemDrive%\AiUsageDashboard.GrokCli.<current-user-SID>\executables-v1\grok-<sha256>.exe` 下重新驗證過的副本。

`1.0.3` 是相容性基準，不是最低允許版本。版本探查接受單行的 `grok x.y.z (commit)`，也接受同格式加上精確的 ` [stable]` 尾綴；其他尾綴不推測版號。可信任執行檔的版號不同或無法解析時，仍會啟動 ACP，再以實際的初始交握、方法與資料格式判斷是否相容。任一來源檢查失敗時都會停止，並引導安裝或更新官方 CLI。

只有使用者選擇新增、連接、切換或重新連接 Grok 卡片時，AI Usage 才會在沿用目前 console 輸入輸出的終端執行：

```text
grok --no-auto-update login --oauth
```

瀏覽器要求的 token、授權碼或 callback URL 由使用者貼回該終端；AI Usage 浮窗不接收或保存這些內容。背景更新不會自行開啟登入。

### ACP 背景查詢、綁定與復原

背景查詢會在受 Job Object 隔離的程序中執行 `grok --no-auto-update agent stdio`。

程序環境會先清空，再只加入帳號專用目錄、受限的 `PATH` 與必要的 Windows 系統變數，重建最小且受控的執行環境；空白工作目錄則作為 working directory。

ACP client 宣告不提供檔案系統讀寫與終端功能，只執行 `initialize`、billing 與 auth-info extension；不建立 session、不送出 prompt，也不呼叫模型。

Billing 回應的可選 `subscriptionTier`（亦接受 `subscription_tier`）只在帳號綁定、每週用量與方案值均通過驗證後顯示於卡片標題；欄位缺失、衝突或無效不影響每週用量。

JSONL 每行大小、總輸出量、回應數量（response count）、標準錯誤輸出（stderr）、資料格式（schema）、每週週期（weekly period）、用量百分比、重置時間與帳號主體（principal）都有固定上限或嚴格驗證；未知或矛盾資料會停止採用。

每張卡片都有私有綁定檔，用其中的隨機 salt 與帳號主體的 SHA-256 指紋驗證實際帳號。`accounts.json` 只保存不能反推出原始帳號的公開綁定 ID。同一個 Grok 帳號主體不能綁定到兩張卡片；若官方資料沒有可顯示的 email，使用者可用本機暱稱區分卡片，暱稱不參與安全綁定。

連接流程先建立不含 token 或原始帳號識別的 `grok-connection-pending-v1.json`，再依序記錄 `LoginStarted`、`LoginCompleted` 與 `BindingPersisted`。

CLI 登入成功且 `LoginCompleted` 寫入磁碟後，即使 App 中止，下次啟動也能重新驗證帳號並完成綁定。若在此之前中止，或無法證明是同一次安全連接，使用者必須重新連接。

若無法確認 Grok 程序樹已清空，程式會停止建立新的 Grok 副本、驗證、登入與背景查詢，並把使用中副本的鎖保留到 App 結束。使用者必須重新啟動 App 才能恢復。

Grok 最短每 15 分鐘更新一次，快取在 30 分鐘後標成舊資料。ACP 暫時失敗、本機 I/O 失敗、逾時、回傳內容（payload）或資料格式（schema）損壞，以及資料不完整時，程式會拒絕採用這次資料、保留上次成功的資料，並由共用背景查詢流程逐步延長等待時間後重試。

只有 ACP 方法、初始交握、回傳內容、資料格式、必要欄位或未知回應失敗，才會在偵測版本不同於基準或無法解析時，附上「版本差異可能是原因之一」的提示。已分類的上游服務暫時錯誤（server transient）、本機 I/O 與逾時不附版本提示。

明確未登入時要求連接；來源不可信或必要 ACP 方法不相容時要求安裝或更新；帳號主體不一致，或帳號明確不是 unified／weekly billing 時要求切換帳號。

匯出的設定檔會保留 Grok 卡片的 ID、服務類型（Provider）、暱稱、啟用狀態與卡片順序，以及全域用量排序、顯示模式、主題與不含螢幕識別的浮窗偏好；不包含私有登入目錄、登入資料、綁定資料、服務帳號識別或帳號主體指紋。

匯入到另一個 Windows 使用者或電腦後，必須逐卡重新連接。移除 Grok 卡片時，程式會先鎖定這張卡片的帳號操作、清除私有綁定、依復原紀錄接續未完成工作，並刪除該卡片的完整私有帳號目錄。失敗時會保存清理工作並在之後重試，不會在 UI 執行緒同步遞迴刪除。

## GitHub Copilot 串接

### 本機 CLI 來源與版本

Copilot CLI 由使用者依[官方安裝文件](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli)安裝，建議使用 WinGet 標準安裝。解析器接受目前 Windows 使用者的 WinGet 標準 package、標準 npm 安裝的 native `copilot.exe`，或 `PATH` 內的真正官方 EXE／npm prefix 下的 native 位置；不執行 `.cmd`／`.ps1` wrapper，也不從舊 App bundle 取得備援執行檔。

來源須通過 `GitHub, Inc.` Authenticode、ProductName 與共用檔案保護檢查，再由共用 stager 複製到 `%SystemDrive%\AiUsageDashboard.CopilotCli.<current-user-SID>\executables-v1`。只在 Copilot 操作時取得執行檔，不因啟動 App 或使用其他 provider 就要求安裝 Copilot。

連接與用量查詢都在背景執行 CLI 搜尋、驗簽與副本準備，避免這些同步檔案作業佔用 UI 執行緒。取消時會先結束呼叫端的等待；若背景準備稍後才取得執行檔鎖，會由清理工作釋放，不啟動登入或 SDK。

支援三段式正式版 `>=1.0.79` 且 `<2.0.0`；`1.0.79` 是固定 SDK `1.0.11` 的既有配對與起始支援基準，其他 `1.x` 仍須通過 RPC 相容性檢查，不能據版本範圍宣稱真人實測完成。CLI 缺少或不符時保留卡片、token 與資料，安裝或更新後重試；只有帳號驗證失效才要求重新連接。

### 支援範圍與穩定帳號識別

Copilot 一張卡片對應一個不同的 `github.com` 帳號；目前不支援 GitHub Enterprise Server，也不把同一帳號的組織、enterprise seat 或訂閱拆成多張額度卡。

每次連接與更新都先向 GitHub `GET /user` 取得 `node_id`、database ID 與 login，再以正規化主機名稱加上 `node_id` 的 SHA-256 摘要建立穩定的 `providerAccountIdentity`。登入名稱變更不會改變識別；相同識別已綁定其他卡片時會拒絕提交。

### 受控連接與日常隔離

只有使用者明確新增、連接、切換或重新連接 Copilot 卡片時，AI Usage 才會在跨程序全域鎖內建立一次性登入暫存目錄，並啟動通過本機來源與相容性檢查的官方 CLI：

```text
copilot.exe --no-auto-update login --web-flow
```

官方網頁登入流程（web flow）會使用官方 CLI 在作業系統中的共用登入清單。這只是連接時使用的共用環境，不代表各卡片平時共用登入資料。

互動 CLI 與讀取登入結果的背景程序，都會先以暫停狀態建立，確認放入 `KILL_ON_JOB_CLOSE` Job Object 後才開始執行。背景程序只使用 `127.0.0.1` 的 TCP 端點與獨立的 `COPILOT_CONNECTION_TOKEN`；AI Usage 只接受官方 CLI 的固定連接埠公告格式。

若啟動失敗、取消或清理延遲，程式必須先確認整棵程序樹已結束，才會解除相關鎖。無法確認時不會開始下一次登入；跨程序重試也會先清除可辨識、但已失去所屬程序的登入暫存目錄。

AI Usage 會交叉比對 `auth status`、`current auth` 與 `all-users`，且只接受唯一相符的 `github.com` 主機名稱、登入名稱與 token。接著再用 `GET /user` 與第一筆額度驗證實際帳號，並確認暫存目錄沒有留下 token。

驗證通過後，token 會先寫入該卡片的暫存憑證項目。只有卡片設定成功保存後，才會轉成正式項目；取消或失敗時會捨棄暫存項目，不會留下半完成綁定。

日常背景查詢不再讀取 CLI 的共用登入清單。每張卡片的 token 依卡片 ID 存在 App 專用的 Windows Credential Manager 項目；SDK client 使用 `UseLoggedInUser=false` 與該卡片明確提供的 token。子程序環境只保留必要的 Windows 與網路變數，並排除 `GH_TOKEN`、`GITHUB_TOKEN` 等從執行環境繼承的憑證。

每張卡片另使用自己的執行目錄：

```text
%LOCALAPPDATA%\AiUsageDashboard\copilot\<account-id>\home
```

背景更新只讀取該卡片的憑證，不會開啟瀏覽器、執行登入或借用另一張卡片的 token。`401` 代表 App 保存的 token 已失效，卡片會要求重新連接；目前沒有 refresh token 或到期時間資料（expiry metadata），也不提供無提示更新（silent refresh）。

### 額度、匯入與清理

額度來源固定為官方 `GitHub.Copilot.SDK` 1.0.11 的 Experimental `account.getQuota`，執行環境使用本機安裝的官方 Copilot CLI。CLI `1.0.79` 是相容性基準，與 SDK 版本分開記錄；主包不再包含第三方 CLI。

每次呼叫額度 RPC 前，都會以同一張卡片的 token 重查 `GET /user`，並同時比對憑證綁定與卡片預期的帳號識別。若帳號識別已被替換（identity swap），程式會在 SDK 啟動前停止。

SDK 以該卡片明確提供的 token 查詢額度，不使用從執行環境繼承的憑證。同一帳號的操作會依序執行，不同帳號則可並行。只有正規化後的額度、方案與穩定帳號識別能寫入用量快取；token 與原始回應不會進入快取或診斷紀錄。

SDK 啟動並取得額度後，會讀取 `account.getCurrentAuth` 的訂閱資料。`CopilotSubscriptionMetadataReader` 只讀取核對帳號、方案與計費模式所需的 JSON 欄位，不讀取回應中的 token；舊格式含有 token、新格式省略 token，或該欄位為 null，都使用相同的訂閱解析規則。這不改變查詢時必須明確提供該卡片憑證的要求。

Reader 支援 `hmac`、`env`、`token`、`copilot-api-token`、`user`、`gh-cli` 與 `api-key` 七種 auth type。主機名稱、Copilot 使用者 login，以及 `env`／`user`／`gh-cli` 的非空 auth login，都必須符合這張卡片已由 `GET /user` 驗證的帳號；`user`／`gh-cli` 保留 SDK 對 auth login 欄位存在的要求。未知 auth type、身分不符、必要資料缺失、已知欄位型別錯誤或重複關鍵欄位，均拒絕採用訂閱資訊；未知輔助欄位不影響解析。

SDK `1.0.11` 的 `AuthInfoToken` model 要求回應包含 token，但訂閱讀取不需要這個欄位。該版本沒有公開的 raw RPC 入口，因此 `CopilotSubscriptionRpc` 以限定用途的 reflection adapter 取得 `JsonElement`，只呼叫固定的 `account.getCurrentAuth`。Adapter 檢查 SDK assembly 版本、`ServerAccountApi._rpc` 欄位型別、`InvokeRpcAsync` 的唯一方法與六個參數型別，以及 `Task<JsonElement>` 回傳契約；不符合時拒絕這次訂閱讀取。通訊、取消與程序清理仍由原 SDK transport／lifecycle 處理，不另建通訊管道，也不變更 SDK 相依版本。後續升級 SDK 時須重新檢查此契約並重跑新舊回應的回歸測試。

訂閱查詢失敗或無法確認時，保留已驗證的額度，顯示安全警告並透過 `AppDiagnostics` 寫入診斷；寫入失敗也會出現在警告中。診斷只使用固定摘要及 exception 型別、HResult、stack trace，不寫入 token、原始回應或 exception message。缺少方案時不猜測方案，計費模式不明時也不強制顯示 AI Credits。實作及實測狀態見[實作檢查清單](../IMPLEMENTATION_CHECKLIST.md#目前-source)與 [CLI 相容性](CLI_COMPATIBILITY.md#103-正式版)。

計費模式優先讀取 `quota_snapshots.premium_interactions.token_based_billing`；該欄位缺少時，才改讀帳號最上層的 `token_based_billing`。帳號不符、查詢失敗或兩個欄位都缺少時，計費模式維持 `unknown`。

- `true`：把 `premium_interactions` 顯示為 `AI Credits`，並隱藏已包含在 AI Credits 內的舊 `chat` 額度。
- `false`：保留 `Premium requests` 與 `chat`。
- `unknown`：使用中性的 `Premium usage`。

方案層級（plan tier）只用來顯示已驗證的方案與組織額度說明，不用來猜測計費模式。

有限額度直接以 `usedRequests / entitlementRequests` 顯示。AI Credits 在「已使用」模式保留絕對值；切換為「剩餘」模式時，才使用 SDK 提供的剩餘百分比。

Business／Enterprise 回傳代表不限量的特殊值時，不能解讀成 AI Credits 沒有上限。GitHub 的 included credits 是帳務單位共用池，還可能受使用者、組織、cost center 或 enterprise budget 控制。`account.getQuota` 沒有提供本整合可安全採用的共用池總量或個人預算，因此畫面只顯示「上限由共用額度與預算控制」。

方案未知時，依 token 計費的不限量特殊值只顯示「此來源未提供上限」。付費方案的程式碼補全（code completions）與下一步編輯建議（next edit suggestions）不消耗 AI Credits，因此 `completions` 仍依 SDK 額度分開顯示。

顯示規則以 GitHub 的[個人用量計費](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-individuals)、[組織／Enterprise 用量計費](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-organizations-and-enterprises)，以及套件固定的 SDK 資料格式為準。

程式不會寫死方案額度。

每次互動登入都使用獨立的暫存目錄：

- Windows 在隔離登入目錄內建立的 `AppData\Local\Microsoft\Windows\INetCache\Content.IE5` junction，必須同時符合下列條件才會列入允許清單：
  - 連結直接指向的目標與解析後的最終目標，都是同一次登入嘗試內、與 `Content.IE5` 同層的 `IE` 資料夾。
  - 中間每一層都是一般資料夾。
- 巡覽檔案樹時不會進入連結指向的目標；清理時也只刪除連結本身，不遞迴刪除目標。其他重新導向點（reparse point）或超出該次登入目錄的目標，會阻止新登入。
- 暫存目錄中的項目數、單一檔案大小與總容量都有上限。Token 殘留檢查會逐一掃描其中的一般檔案，以固定大小的緩衝區分段讀取，並限制總讀取量；遇到官方 CLI 執行環境中的大型檔案時，記憶體也不會隨檔案大小增加。

匯入設定檔會保留 Copilot 卡片，但清除帳號綁定並要求重新連接。若程式在設定完成前中止，下次啟動會還原匯入前設定並保留原有登入資料。

設定全部寫入後，程式才會清除已提交卡片的舊私人資料。清除完成後會寫入一筆名為 `commit-callback-completed` 的完成紀錄；復原時看到這筆紀錄就不會再次清除，完成紀錄互相矛盾時則停止處理。

App 啟動、同次執行中的清理重試，以及建立新清理工作前，都會先完成尚未結束的匯入復原。同一項目仍有已授權清理時，必須先做完，不能再新增另一筆工作。

移除卡片或完成匯入後的清理，只會刪除該卡片 ID 的 Windows Credential Manager 正式／暫存項目、用量快取與私有登入目錄。失敗時會記錄並在之後重試，不會刪除另一張 Copilot 卡片，也不會操作官方 CLI 的共用登入清單。

建置 App 時，[SDK](../third-party-notices/GitHub-Copilot-SDK-LICENSE.md) 的第三方授權聲明會放在輸出目錄的 `third-party-notices`；不再交付 Copilot CLI binary、component 或 CLI 授權文件。使用者另行安裝的 CLI 仍適用其官方條款。

建立發布套件時，授權文件置於 `app\third-party-notices`；根目錄使用說明的相對連結會指向該處。

Copilot 的功能完成、建置、封裝、實機驗證與尚待測試項目，以[實作檢查清單](../IMPLEMENTATION_CHECKLIST.md)為準。

## Antigravity 串接

AGY 是選用功能。Production 只使用官方 AGY CLI 的 print mode `/usage`，不再使用 ConPTY、私有 profile 或 status line。使用者新增或重新連接 AGY 時，設定視窗由 `AiUsageDashboard.Antigravity.Setup.dll` 在 `AiUsageDashboard.App.exe` process 內顯示；套件不包含 `AiUsageDashboard.Antigravity.Setup.exe` 或 `AiUsageDashboard.AntigravityCapture.exe`。

主程式會在開啟設定視窗前，把含 `setupAttemptId` 的待辦寫入：

```text
%LOCALAPPDATA%\AiUsageDashboard\antigravity-connection-pending-v1.json
```

各階段另存於 `%LOCALAPPDATA%\AiUsageDashboard\antigravity\setup-attempt-states-v1`，依序為 `Launching`、`Active` 與 `ApprovalRequested`。無法先寫入時，不會開始變更設定。

設定視窗要求外部設定提交前，會先把來源類型與目標帳號識別（identity）指紋標記為 `ApprovalRequested`。設定提交完成且候選來源已釋放後，再把同一份核准證明寫入 `setup-approval-receipts-v1`。

階段更新使用跨程序檔案鎖，較晚到達的舊階段不能覆寫已提交階段。核准證明則只能建立一次，建立後不可覆寫。

設定結果為 `CompletionUnknown`、主程式關閉，或提交期間中止時，設定待辦都會保留。

下次背景查詢或 App 啟動時，程式優先讀取核准證明。若證明尚未寫入，但已保存 `ApprovalRequested`，該階段可作為已提交的證據。

主程式接著強制重新執行 official print 驗證；結果必須得到 `OfficialExperimental` 與固定的本機工作階段識別。

驗證成功後才清除舊快取並提交卡片設定。設定仍在進行時只會等待；舊額度、來源或帳號不符，以及互相矛盾的狀態與證明，都不會被推定為完成。

可恢復錯誤會由背景依間隔重試。只有確實需要操作時才顯示對應動作，不會要求使用者再次按 **完成連接**。

連接復原紀錄保存下列資料：

- 帳號卡 ID、開始連接前的綁定識別 SHA-256 指紋、待完成階段與 `setupAttemptId`。
- 執行嘗試狀態（attempt state）與核准證明只保存來源、程序識別與目標帳號識別的 SHA-256 指紋，不保存原始 email。

後續階段可以安全重複執行。暫時寫入失敗時，背景工作會逐步延長等待時間後重試；App 重啟後也會從復原紀錄接續。

復原紀錄尚未成功讀取或寫入時，AGY 用量查詢、新連接、新增卡片及涉及 AGY 的匯入都會暫停。背景成功載入狀態後才會自動解鎖。

若同一帳號 ID 同時有舊復原紀錄與本次尚未寫入的工作，記憶體中的本次工作優先，並會重新寫入，避免舊階段略過本次快取清除。開始連接前保存的指紋可讓既有舊版綁定安全升級，也能避免同 ID 卡片已被其他設定取代後誤套舊待辦。

只有復原紀錄或執行嘗試證明本身損壞、帳號已不存在、指紋不再符合，或其他無法安全套用的狀態，才會停止自動提交。

AGY 沒有可驗證的逐帳號設定檔（profile）或官方多帳號介面，因此只允許一張卡片。設定綁定目前的 Windows 使用者與電腦，換機後必須重做。

官方 print mode 用量資料不含 email，所以卡片使用固定的 `agy.local-session.v1` 識別，代表「目前這台電腦核准的 AGY 登入來源」。

先前版本可能在 AGY 的 `settings.json` 寫入 AI Usage 專用的 status-line command，並在 `%LOCALAPPDATA%\AiUsageDashboard\private\antigravity-statusline` 留下 helper 或擷取資料。新版第一次讀取 AGY 狀態時只會在設定值含精確的 AI Usage ownership marker、路徑位於預期私人目錄、檔名／內容與 ACL 都通過檢查時，原子移除該欄位並清理自己的檔案。任何格式、並行變更、路徑或擁有權無法確認時都會保留原狀；使用者自訂的 status line 不會修改。一般使用與升級都不需要先執行 `/statusline off`。

### 官方 print mode 路徑

所有新連接與既有連接都只使用 AGY 1.1.11 起提供的 print mode。程式中的版本分類如下：

- `1.1.11`：相容性比對基準，也是官方 print mode 的最低版本，分類為 `Reference`。
- 高於 `1.1.11` 且低於 `2.0.0` 的標準穩定版：分類為 `UnverifiedAllowed`，仍可執行。
- 低於 `1.1.11`：不能使用官方 print mode，分類為 `MissingRequiredCapability`，必須更新 AGY 後重新連接。
- `2.0.0` 以上、帶前後綴、前導零或預發行格式（prerelease）：分類為 `SafetyBoundaryRejected`，因為只能在執行後才發現命令語意改變，無法撤回已產生的活動。

`SafetyBoundaryRejected` 不表示已知一定不相容，只表示目前不能安全執行後再確認。

設定核准後，會把來源種類與執行檔絕對路徑保存在 `%LOCALAPPDATA%\AiUsageDashboard\antigravity\approved-source-v1.json`。升級時若尚未建立此檔案，可把目前 Windows 使用者的舊 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 值一次性遷移進來；production 不再以環境變數作為持久化設定。路徑無效、來源驗證或擷取失敗時都會停止，不會改走其他執行方式。

設定視窗與每次背景查詢都會重新驗證：

- 路徑是本機允許目錄內、完全限定的普通 `.exe`，且檔案與父路徑沒有重新導向點（reparse point）。
- WinVerifyTrust 成功，簽署者憑證的 subject 與 thumbprint 精確符合程式內固定的 Google LLC signer。
- 版本探查（probe）回傳上述標準穩定版範圍，探查前後的檔案狀態不變，並持有執行檔鎖（executable lease），防止檢查與啟動之間遭替換。

通過後只會直接執行固定的命令參數（argv），不經 shell：

```text
agy -p /usage --output-format stream-json
```

[AGY 1.1.11 的官方發行說明](https://antigravity.google/changelog)指出，這個 print command 不建立 conversation、不建立 agent turn，也不消耗 quota；這是上游行為說明，不是對 AI Usage 的背書。

AI Usage 仍會在回傳後硬性驗證程序成功、沒有標準錯誤輸出（stderr）或截斷、result 為 success、conversation／turn 未建立，而且 input、output、thinking、cache-read 與 total token 全部為零；任何不符都會停止後續擷取。

解析器只接受 typed `usage` command data，內容必須正好有兩個已知群組（group），每組正好有兩個已知額度區間（bucket），總計四個額度週期（quota window）。

出現未知 event、欄位、group、bucket、重複值、超出範圍的 fraction、無效 reset time，或 command／result 不一致時，都會停止採用結果。

Stdout／stderr 有固定上限；原始 JSON bytes 只在記憶體中使用，完成後會清除，不會寫入快取、診斷紀錄、原始碼庫或套件。主程式只收到固定的本機工作階段識別（local-session identity）與四個正規化額度週期。

官方 print process 是獨立、短時間執行的命令，不會附加到既有 AGY session，因此新連接不需要關閉已開啟的 AGY 視窗。擷取工作一次只執行一個，並由共用背景查詢流程限制為每分鐘最多一次。

啟動時會先清空環境，只保留固定允許的 Windows 使用者／資料／暫存路徑，並加入 `NO_COLOR=1` 與 `AGY_CLI_DISABLE_AUTO_UPDATE=true`；不繼承 `PATH`、`COMSPEC`、API keys 或其他呼叫端環境。程序透過完整核准路徑直接啟動，不經 shell。

每次啟動官方 print mode 前，App 內的設定與背景讀取都會先取得同一個跨程序執行鎖，再把不含帳號、路徑或原始輸出的安全標記寫到：

```text
%LOCALAPPDATA%\AiUsageDashboard\antigravity\official-print-safety-v1.json
```

若 `UnverifiedAllowed` 版本發生命令、資料格式（schema）或回應格式錯誤，標記會保存當時偵測到的 CLI 版本，讓後續更新與 App 重啟仍能提示「版本差異可能是原因之一」。本機執行、來源驗證、逾時與重大安全錯誤不保存這項版本資料。

每次保存或清除狀態前，都會先用原子取代寫入同路徑的 `.journal`。復原紀錄一旦成功寫入，就代表狀態已提交；即使主檔暫時被鎖或無法取代，後續存取與 App 重啟仍會優先依紀錄繼續完成。

開始標記必須在 `CreateProcessW` 前寫入磁碟。執行後已確認可恢復的結果也必須先寫入，避免重啟後誤回到舊的執行中狀態。

功能探查與 `/usage` 使用相同的程序隔離方式：

- `CreateProcessW` 建立程序時，透過 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 在同一個 Windows kernel 操作中，把程序加入啟用 kill-on-close 的 Job Object。
- Job Object 的 active-process limit 固定為 `1`，不允許官方命令再建立子程序；超出限制會失敗並拒絕結果。
- 無論成功、逾時、取消或發生錯誤，都會在同一段清理時間內嘗試確認整個 Job Object 程序樹已清空。
- 若 Job Object 或程序查詢持續失敗或逾時，程式會關閉輸出資源，以及啟用 kill-on-close 的 Job／process handles，完成範圍有限的最後隔離與清理，再釋放執行檔鎖與跨程序執行鎖；不會無限查詢或持續占用 handles。

最後一項只能證明程式已執行隔離與清理，不能證明程序樹確實已空。因此安全標記仍維持只能手動處理，背景工作不會把它改成自動重試。

安全標記的清除與重試規則如下：

- 只有完整驗證 conversation、turn 與所有 token 都是零的成功結果，才會清除標記。其他結果會保留失敗原因。
- 若已明確確認程序樹停止，暫時逾時、呼叫端取消、一般程序錯誤，以及完成後的輸出錯誤都可自動恢復。輸出錯誤包括 `NonZeroExit`、空白或過大輸出、無法驗證的 JSON、資料格式（schema）或最外層格式（envelope），以及重複或矛盾資料。
- 可自動恢復的錯誤會交由共用刷新流程逐步延長等待時間後重試，不會因一次性嘗試次數用完而停止。
- 這些錯誤只能證明程序樹已空，不能像成功結果一樣再次證明 token 為零。持續重試仍以「上游 `/usage` 不建立 turn、不消耗 quota」為前提。
- 固定 argv、跨程序鎖、Job Object、單次執行上限與最多 15 分鐘的重試間隔會共同限制風險。
- 只有回傳資料明確顯示 conversation、turn、token 或費用（cost）活動時，才會以 `UsageActivityDetected` 重大安全錯誤停止自動執行。

取消與中斷依發生時間處理：

- 程序開始前收到取消時，程式完成隔離檢查後直接回報取消，不建立 `CanceledAfterStart` 標記。
- 每次自動嘗試前，程式先保存 `AttemptInterrupted`。若前置檢查暫時失敗，或用量程序尚未啟動就失敗，會還原原本可自動恢復的標記。
- 目前格式的 `AttemptInterrupted` 有有效的執行識別碼（attempt ID）時，程式會用對應的命名 Job 確認程序樹。Job 不存在或已空表示清理完成；仍有程序時會先終止並再次確認。
- `Prepared` 標記會在冷卻到期後執行上述檢查。無法確認程序樹已清空時，會保留標記並重新排定自動檢查，不要求使用者操作。
- 程序開始後留下的「已開始但沒有結果」（started／no-outcome）標記，只有在確認程序樹已清空後，才恢復自動重試並重新等待至少一分鐘。無法確認時，卡片會保留 **重新檢查 Antigravity 用量**。

系統時間回撥使標記時間落在未來時，冷卻時間會改從目前時間起算。明確不支援的執行檔會清除舊標記，回到既有的來源修復流程。

一般可恢復結果依 1、2、4、8、最多 15 分鐘的間隔重試。等待期間不顯示操作按鈕或重複的可及性公告，並繼續顯示上次成功的資料。

格式版本 v3／v4 的 `TimedOut` 與「已取得完整輸出」（completed-output）標記，若有有效 attempt ID，可遷移舊版已耗盡旗標。舊格式版本 v1、缺少有效 attempt ID、無法確認程序已停止、已發生用量活動或安全狀態損壞時，仍必須由使用者手動重新檢查。

Production 不會載入 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE`、ConPTY profile、私有 key 或 reviewed manifest。舊 ConPTY 程式碼只保留在開發用 Spike，供研究與比較，不會編入或放入正式套件。使用官方 CLI 命令不代表 Google 核准或支援本工具；研究工具與限制請見 [AGY Spike 說明](../tools/AiUsageDashboard.AntigravitySpike/README.md)。

## 建置與分發

- 本機執行：`dotnet run --project src/AiUsageDashboard.App`
- 內部安裝、更新、回復、移除與支援：[Windows 分發與支援手冊](../INTERNAL_DISTRIBUTION.md)
- 正式候選版條件與工作流程：[發佈流程](../RELEASING.md)
- 當前功能與待辦：[實作檢查清單](../IMPLEMENTATION_CHECKLIST.md)

正式 `app` 目錄只允許 `AiUsageDashboard.App.exe` 與 `AiUsageDashboard.ClaudeCapture.exe` 兩個 EXE。AGY 設定 UI 以 `AiUsageDashboard.Antigravity.Setup.dll` 併入 App process；`AiUsageDashboard.Antigravity.Setup.exe`、`AiUsageDashboard.AntigravityCapture.exe`、開發用 Spike 與 `createdump.exe` 都不得出現在成品。

## 發布用授權與更新信任

精確 license、條款 scope 與離線交付由 `AiUsageDashboard.Licensing` 共用。feed 的驗簽格式與信任鍵設定見 [UPDATE_FEED_FORMAT.md](UPDATE_FEED_FORMAT.md)，實際成品、序號盤點、private 候選凍結與公開端點驗收見 [發布流程](../RELEASING.md)。安裝 identity、account IDs、資料路徑與 Credential Manager target 維持不變。
