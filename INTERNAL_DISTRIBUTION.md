# Windows 分發與支援手冊

讀者：AI Usage 的維護者與協助安裝、更新或排錯的人員。本手冊說明目前 source 的 Windows x64 行為；實際成品是否驗收通過，以該版本 Release 的紀錄為準。一般使用者請優先讀隨附指南。

一般使用者請閱讀 [`使用說明.md`](使用說明.md)。一般問題與功能建議走[支援入口](SUPPORT.md)，安全問題依[私密回報指引](SECURITY.md)處理。正式 ZIP 只附使用者指南；本手冊留在專案原始碼庫，供維護與支援人員查閱，不隨套件分發。

本手冊只說明前置條件、操作步驟、預期結果與失敗處理。執行檔驗證、程序隔離、更新切換與中斷復原的完整設計，請查閱 [`docs/TECHNICAL_OVERVIEW.md`](docs/TECHNICAL_OVERVIEW.md)。

## 支援範圍

| 元件 | 內部版本政策與支援範圍 |
| --- | --- |
| AI Usage | 有版本編號、自帶執行環境的 Windows x64 套件；目前使用 .NET 8.0.31，僅支援至 2026-11-10 |
| Claude | 相容性基準 `2.1.169`；官方 Claude Code `>=2.1.169` 才有需要的 `--safe-mode`；執行 `Anthropic, PBC` 簽署、目前 Windows 使用者專用的受保護副本；Claude.ai Pro／Max／Team；Enterprise 可辨識，但目前不提供 `/usage`；實驗性輪詢 |
| Codex | 相容性基準 `0.144.1`；可確認為官方 `codex-cli x.y.z` 的三段式正式版本都會先實測；執行 `OpenAI OpCo, LLC` 簽署、目前 Windows 使用者專用的受保護副本；實驗性 `app-server` 串接 |
| Grok | 相容性基準 `1.0.3`；可信任的官方版本即使版號不同或無法解析，仍先測試 ACP 通訊；只接受預設安裝位置、`X.AI LLC` 簽章與目前 Windows 使用者專用的受保護副本；實驗性多帳號串接 |
| GitHub Copilot | 使用本機官方 CLI 三段式正式版 `>=1.0.79` 且 `<2.0.0`；基準 `1.0.79`，App 固定 `GitHub.Copilot.SDK` `1.0.11`；核對 `GitHub, Inc.` 簽章與 ProductName，執行目前使用者的受保護副本；只支援不同的 `github.com` 帳號，每張卡片分開保存登入資料 |
| AGY | 相容性基準 `1.1.11`；Windows 10 1809 以上；production 只執行官方用量輸出功能（official print），限 `1.1.11 <= version < 2.0.0` 的正式版本，並核對 Google 簽章 |

相容性基準只供比較和診斷，不是允許版本清單。其他官方版本只要通過必要功能與安全檢查，就應先實測；不得只因版號不同而拒絕，也不得把版本差異寫成已確認的失敗原因。每個候選版本仍須記錄實際 CLI 版本與測試結果。

套件的 .NET 與相依元件語系資源只保留繁體中文（`zh-Hant`）、簡體中文（`zh-Hans`）、英文（`en`）及日文（`ja`），由 [Directory.Build.props](Directory.Build.props) 統一設定。英文預設訊息包含在元件本體中，因此不一定另有 `en` 資料夾。這項設定控制執行環境的訊息資源；AI Usage 自身介面目前使用繁體中文。

### 執行檔安全要求

Claude、Codex 與 Grok 只會執行 AI Usage 建立的受保護副本。程式會鎖住來源檔，確認檔案位於本機固定磁碟、路徑沒有重新解析點（reparse point）、大小不超過 512 MiB、Windows 簽章與發行者正確，再依 SHA-256 建立副本。受保護目錄只開放目前使用者、Local System 與 Builtin Administrators 完整存取：

- `%SystemDrive%\AiUsageDashboard.ClaudeCli.<current-user-SID>\executables-v1`
- `%SystemDrive%\AiUsageDashboard.CodexCli.<current-user-SID>\executables-v1`
- `%SystemDrive%\AiUsageDashboard.GrokCli.<current-user-SID>\executables-v1`

正常情況下，每個服務只保留目前與前一個有效副本。若檔案仍在使用、安全檢查失敗或刪除失敗，必須先保留，待下次再清理。不得強制刪除、放寬權限或手動替換副本。

程序結束前，副本必須保持鎖定。若程式無法確認整個子程序樹都已結束，Claude、Codex 或 Grok 的相關操作必須停止到 AI Usage 重新啟動，不得在狀態不明時繼續啟動新的程序。

Grok 只接受 `%USERPROFILE%\.grok\bin\grok.exe`，發行者必須為 `X.AI LLC`。AGY 每次執行前都要重新確認已核准的官方路徑、版本、Google 簽章與檔案狀態；驗證失敗時停止，不得改走其他執行方式。不要從其他電腦複製服務執行檔、帳號目錄、受保護副本或登入資料，也不要用 `PATH` 或替代檔案繞過檢查。

執行環境期限依照官方 [.NET 支援政策](https://dotnet.microsoft.com/en-us/platform/support/policy)。請在期限前完成移轉，並確認新的 .NET 執行環境仍在支援期。套件自帶的執行環境一旦超出支援期限，就不會繼續取得修正。

## 發佈前檢查

先完成 [發布流程](RELEASING.md) 的來源、免費額度、精確授權文字、條款接受、簽章、版本及 private／public 分段驗收。驗證三份相鄰的 `.sha256` 後，仍須先驗證 feed 的專案簽章與 pinned trust key；同站雜湊不能單獨證明發布者。

Claude、Codex、GitHub Copilot、Grok 與 AGY 都須記錄實際 CLI 版本及本人帳號測試結果。Claude 背景查詢可能增加用量或費用，使用者須自行同意；執行後檢查不能撤回已產生的用量。Claude／AGY 的個案條款不確定性及 Microsoft publisher 義務留在最終發布決策，不設公司審批或個別廠商回函的必經步驟。

## CLI 相容性維護

- **時機**：每週檢查一次、每次候選發布前，以及得知官方新版或收到相容性故障時。
- **版本政策與公開結果**：統一使用 [CLI 相容性](docs/CLI_COMPATIBILITY.md) 的版本要求與逐平台實測表；基準常數不等於本候選實測通過。
- **執行範圍**：使用隔離的 Windows 環境與測試資料，不替換使用者全機 CLI、登入資料或受保護副本。

1. 在受控驗收紀錄固定要驗證的 App 成品、完整 source SHA 與版本；逐一記錄平台、CLI 版本及 SHA-256。
   各平台比較相容性基準與最新正式 CLI；Copilot 另記錄 App 固定的 SDK `1.0.11`，分開核對本機 CLI 版本與來源。新版依賴另建測試包，不替換已凍結的候選檔案。
2. 從官方來源核對版本、下載來源與簽章，再按官方方式安裝至隔離環境。
   AGY 另核對 signer subject 與憑證 thumbprint；官方換證也可能被拒絕，須先查明，不放寬信任檢查。
3. 先確認版本符合硬限制，再做不帶登入資料的版本／協定前檢。
   超出硬限制、來源不符或無法安全取得基準時，記錄拒絕或缺件原因，功能仍標未驗證，不繞過檢查。
4. PR 與一般 CI 不讀個人登入資料、token 或發布簽章私鑰，也不執行真實登入及用量查詢。
   live 檢查另由本人帳號在受控環境執行；事前確認平台、動作與次數，Claude 須先接受可能產生用量或費用的風險。
5. 依本手冊的服務驗收項目，檢查官方登入、帳號／組織／工作區、手動用量、背景更新、重啟、帳號隔離及取消／失敗處理。
   詳細 source、雜湊、原始錯誤及證據放受控驗收紀錄；公開表只放去識別摘要，不含本機證據路徑、帳號、token、登入資料或原始私密用量輸出。
6. 各項分別標示通過、失敗、待驗或不適用；不適用須說明原因。
   整體範圍另標同候選完整實測、部分實測、歷史參考或未驗證；缺任何必要項目不能寫整體通過。
7. 若原 App 已相容，只在上述唯一實測表更新或新增帶 App 版本的紀錄，保留既有結果；若須改程式或固定依賴，才建立新 App 版本並重新驗收。
   新版失敗先比較原始錯誤、來源與必要功能，不因版號不同就要求降版，也不把其他平台或舊候選結果移作本次通過。

### 受控驗收紀錄模板

每個 App 成品、平台與 CLI 版本各留一份，不覆蓋既有紀錄；以下未填值均為待驗，同列各子項須分記結果，不能直接改成通過。

```markdown
| 項目 | 紀錄 |
| --- | --- |
| App 版本／完整 source SHA／成品 SHA-256 | 待驗 |
| 平台／CLI 版本／CLI SHA-256／基準或最新／Copilot SDK 版本 | 待驗 |
| Windows 版本與組建／日期與時區 | 待驗 |
| 官方來源 URL／簽章檢查（AGY 含 subject、thumbprint） | 待驗 |
| 本人帳號受控執行與 Claude 費用同意（不記帳號秘密） | 待驗 |
| 證據位置與 SHA-256 | 待驗 |

| 檢查項目 | 結果 | 證據／原始錯誤／不適用原因 |
| --- | --- | --- |
| 來源、版本與無憑證協定前檢 | 待驗 | 待驗 |
| 官方登入與帳號／組織／工作區 | 待驗 | 待驗 |
| 手動用量／背景更新 | 待驗 | 待驗 |
| 重啟／帳號隔離／取消與失敗處理 | 待驗 | 待驗 |
| 整體結果與實測範圍（同候選完整／部分／歷史／未驗證） | 待驗 | 待驗 |
```

### 每週自動化方案

**狀態：未實作／未啟用。** 擬每週偵測官方 release，於隔離 Windows 做不帶憑證的版本／協定前檢，保存版本、雜湊及結果；不自動執行 live 登入或用量查詢。
啟用前先檢閱 workflow 的來源、權限、執行次數、逾時與清理方式，確認 Actions 免費額度及儲存預算足夠，再受控啟用；不得將此方案列為已有 CI 覆蓋。

## 套件內容

主包不再隨附第三方 CLI；各服務的官方 CLI 由使用者另行安裝。Copilot 仍使用 App 固定的 `GitHub.Copilot.SDK` `1.0.11`，SDK 授權文件隨實際交付元件保留。

ZIP 固定使用簡短的根目錄 `AiUsageDashboard`，避免 Windows 解壓縮路徑重複包含完整版本名稱。以下路徑都以該目錄為基準：

- `使用說明.md`：放在解壓縮套件根目錄，供一般使用者閱讀的繁中說明。
- `app\AiUsageDashboard.App.exe`：一般使用者唯一需要啟動的執行檔。
- `app\AiUsageDashboard.Antigravity.Setup.dll`：由 App process 載入的 AGY 設定介面；不是獨立入口。套件不得包含同名 Setup EXE 或 `AiUsageDashboard.AntigravityCapture.exe`。
- `app\README.md`：內容與一般使用者說明相同；應用程式會以這個檔名開啟本機復原指引。
- `app\third-party-notices\`：包含元件交付清單與實際版本的第三方授權、附加條款及 notices；自有 LICENSE 也放在 app 內。封裝時會調整根目錄使用說明的相對連結，讓兩份指南都能離線開啟授權文件；套件根目錄維持只有 `app\` 與 `使用說明.md`。

內部分發、驗證與復原流程只保留在專案原始碼庫的 `INTERNAL_DISTRIBUTION.md`，不放入使用者 ZIP。

## 安裝與啟動

一般安裝使用 Updater，首次安裝與後續更新使用同一入口。安裝後可從 Windows 開始選單搜尋 **AI Usage**；App 的 **關於 AI Usage** 提供版本資訊、隨包指南、Releases 與問題回報入口。

以下為免安裝 ZIP 的操作步驟：

1. 請使用者先閱讀根目錄的 `使用說明.md`。
2. 使用相鄰的 `.sha256` 檔案驗證 ZIP。
3. 把 ZIP 解壓縮至新的、有版本區隔且使用者可寫入的目錄，再開啟其中的 `AiUsageDashboard` 目錄。
4. 只安裝使用者要新增之服務所需、且已核准的官方 CLI。
5. 啟動 `app\AiUsageDashboard.App.exe`。這是免安裝 ZIP 的一般啟動入口。
6. 首次使用時，從浮窗新增需要的帳號。換機或重建設定時，可從浮窗的 **⋯** 選單匯入先前匯出的設定檔，再逐張重新連接 Claude、Codex、GitHub Copilot、Grok 與 AGY。這些服務的本機登入資料或電腦綁定不會隨匯入移轉。AGY 為選用服務；使用者選擇 Antigravity 後才會開始連接，受支援的新版本不必先關閉既有 AGY 視窗。

不要複製其他使用者的 `%LOCALAPPDATA%\AiUsageDashboard` 目錄。裡面可能包含帳號專屬的 CLI 狀態、Grok 登入目錄、用量快取、診斷資訊，以及 AGY approved source 與安全復原狀態。

### Claude 使用者同意

Claude 背景查詢只有在 model turns、tokens 與 cost 全部恰好為零時才接受結果。這項檢查發生在 CLI 命令執行之後；若結果異常，可以阻止之後的查詢，卻無法撤回這次查詢已經產生的用量或費用。

每位使用者須自行接受 Claude 的用量風險，決定寫在每張卡片的本機設定。一般第三方條款另依同一 Windows 使用者、版本與範圍在本機共用；不建立或上傳試用者名單。

新卡片及從尚未保存 Claude 風險同意的舊格式升級而來的卡片，預設為未接受；已有有效同意紀錄的卡片升級後會保留原狀態。使用者明確接受並儲存設定前，程式不會登入或呼叫 `/usage`。拒絕 Claude 風險不影響 Codex、GitHub Copilot、Grok 或 AGY。Codex 用量查詢不會建立 turn；AGY 仍會要求回傳結果中的 turn 與所有 token 都是零。上游說明不代表 Google 對 AI Usage 背書。

## 選用的 Grok 多帳號設定

未新增 Grok 的使用者不會建立 Grok 專用登入目錄，也不會執行 OAuth 或 ACP 用量查詢。需要 Grok 的使用者請依序操作：

1. 從 xAI 官方來源安裝 Grok Build CLI，確認它位於 `%USERPROFILE%\.grok\bin\grok.exe`，並記錄實際版本。AI Usage 會自動建立或更新目前 Windows 使用者專用的受保護副本。本版相容性基準為 `1.0.3`，其他可信任官方版本仍先實測。
2. 在浮窗選擇 **新增帳號**，選取 Grok，再選擇 **儲存並連接**。
3. AI Usage 會開啟與目前終端共用輸入輸出的登入視窗及瀏覽器；若網頁顯示存取權杖（token）、授權碼或回傳網址（callback URL），請貼回該終端並按 Enter，不要貼到 AI Usage 浮窗。
4. 終端成功結束後，AI Usage 會從同一張卡片的專用目錄讀取每週用量與實際帳號。連接完成後會顯示 **可用**。官方資料沒有 `email` 時不算失敗；請使用本機暱稱區分多張卡片。

每張 Grok 卡片都使用 `%LOCALAPPDATA%\AiUsageDashboard\grok\<account-id>` 下的獨立登入目錄、空白工作目錄、連接資料與快取。同一個實際帳號不能綁定到兩張卡片。

`accounts.json` 的 Grok 身分欄位只保存隨機產生的公開連接 ID。私密連接檔另存格式版本（schema version）、同一個公開 ID、隨機值（salt）與加鹽後的 SHA-256 帳號指紋；不保存原始帳號、token 或 `email`。

登入開始前會先把不含 token 或原始帳號的待處理紀錄安全寫入磁碟。CLI 登入成功且 `LoginCompleted` 已保存後，若 AI Usage 在連接資料寫入前中止，重新啟動會先確認仍是同一帳號，再自動接續。若中止得更早，或無法確認登入是否安全完成，則要求重新連接。背景更新不會自行開啟登入終端。

移除 Grok 卡片會清除該卡的連接資料、待處理工作與完整專用帳號目錄，因此該卡的本機 CLI 登入也會遺失；這不代表已從 xAI 服務端登出。清理失敗時會保留重試紀錄並顯示警告，不得改成手動遞迴刪除整個 `grok` 目錄。

## 選用的 AGY 帳號設定

未新增 Antigravity 的使用者不會看到這套流程，也不會建立 AGY 卡片或本機 AGY 設定資料。

需要 AGY 的使用者請依序操作：

1. 登入官方 AGY 應用程式。使用 `1.1.11 <= version < 2.0.0` 的正式版本與官方用量輸出功能時，不需要關閉既有 AGY 視窗。
2. 在浮窗選擇 **新增帳號**，選取 Antigravity，再選擇 **儲存並連接**。
3. 應用程式會暫停該卡片的輪詢，並在同一個 App process 內開啟 Antigravity 設定視窗。
4. 核對「目前登入的 Antigravity 帳號」與四個用量週期，再選擇 **完成連接**。

整個流程中浮窗都會保持開啟。成功後，應用程式會立即更新卡片。最終確認前取消或關閉設定視窗，卡片會維持原狀；按下 **完成連接** 後若程式中斷，重新開啟 AI Usage 會重新驗證來源、程序與帳號，再自動接續，不應要求使用者再次確認。選擇 **儲存** 只會建立帳號；之後可從 **帳號設定** 選擇 **連接 Antigravity 帳號**。

AGY 的用量結果不含 email。卡片保存的是這台電腦核准的 AGY 登入來源，不是可跨電腦使用的帳號識別。

舊版 `1.1.7`／`1.1.9` 缺少目前要求的 official print 能力；先更新至 `1.1.11 <= version < 2.0.0` 的正式版本再重新連接。Production 不會為既有卡片退回 ConPTY。

AI Usage 不會安裝或讀取 status line，不會附帶 AGY capture EXE，也不需要先執行 `/statusline off`。從舊版升級時，App 只會移除帶有精確 AI Usage ownership marker、且路徑、檔名、內容與 ACL 都能驗證的舊設定與自有檔案；使用者自訂的 status line 保持不變。無法確認擁有權時一律保留並停止清理。

若 AGY 更新後仍在支援範圍內，AI Usage 會在每次讀取時重新驗證來源與版本。若卡片仍顯示待處理操作，請使用 **重新連接 Antigravity 帳號** 或 **連接 Antigravity 帳號**。版本、簽章或路徑無法確認時必須停止，不得改用其他執行方式。

套件不再包含 `AiUsageDashboard.Antigravity.Setup.exe` 或 `AiUsageDashboard.AntigravityCapture.exe`，因此沒有可供單獨啟動的 AGY helper。支援人員也應從 App 的卡片操作開啟設定流程，不要建立或轉傳替代 helper。

### AGY 維護安全注意事項

官方用量輸出流程會把核准來源與執行檔完整路徑寫入 `%LOCALAPPDATA%\AiUsageDashboard\antigravity\approved-source-v1.json`。舊版目前使用者範圍的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 只供缺少新檔時一次性遷移；遷移後以新檔為準。設定與每次讀取都必須重新確認：

- 它是一般本機 `.exe`，路徑沒有 reparse point。
- Windows 信任簽章通過，Google LLC 簽章名稱與憑證指紋（thumbprint）相符。
- 版本在 `1.1.11 <= version < 2.0.0`，而且檢查前後檔案沒有改變。

來源驗證通過後，只能直接執行 `agy -p /usage --output-format stream-json`，不得透過 shell。啟動環境先清空，再只保留固定允許的 Windows 使用者／資料／暫存路徑，以及 `NO_COLOR=1`、`AGY_CLI_DISABLE_AUTO_UPDATE=true`；不得繼承 `PATH`、`COMSPEC` 或 API keys。

版本探查與 `/usage` 都必須放入 kill-on-close Windows Job Object，active-process limit 固定為 `1`。官方程序嘗試建立 child process、輸出超過固定上限、stderr 非空、逾時、清理不明，或解析器看到非預期資料時，都必須拒絕結果。

解析器只接受 `usage` 指令的兩組資料、每組兩個週期，並要求成功、conversation／turn 為零、所有 token 精確為零。原始 JSON 只在記憶體中使用，用完即清除，不得寫入診斷、快取或套件。

### 已退役的 AGY 研究路徑

舊 ConPTY、R0／R1 profile、private key、reviewed manifest 與 status-line capture 都不是 production 相容路徑，也不會放入正式套件。其程式與操作說明只保留在 [`tools/AiUsageDashboard.AntigravitySpike`](tools/AiUsageDashboard.AntigravitySpike/README.md) 供開發研究與回歸比較；不得用來連接一般使用者、繞過 official print 驗證，或把研究結果宣稱為正式支援。

## 更新、回復舊版與更換電腦

### App 內建更新檢查

執行正式發布腳本時使用 PowerShell 7.4 以上的 `pwsh`。Windows PowerShell 5.1 不支援成品使用的 .NET 8 assembly inspection，三個發布入口會在任何發布工作前停止。

Production App 與 Updater 必須使用相同的 `FeedUrl`、`Channel` 與外部 `TrustedKeysFile`。`tools\Publish-Internal.ps1` 要求明確提供三項；`tools\Publish-UpdateBundle.ps1` 的 `Channel` 預設為 `stable`，省略 `FeedUrl` 時會使用該 channel 的 GitHub latest download URL，但仍須提供外部 `TrustedKeysFile`。`Publish-UpdateBundle.ps1` 會把解析後的同一組值分別交給 App 與 Updater 的 publish 入口；URL 或 trust store 無效時會停止，App package gate 也會從已發布 DLL 讀回 feed／channel metadata 與 embedded trust bytes 逐值核對。一般 dev build 可不嵌入，About 會標成此 build 無更新來源。App 只讀取並驗證 signed feed，不下載 artifacts，也不接受本機 cache 提供 URL 或安裝決策。

首次啟用會先以浮窗 banner 或 tray balloon 說明網路行為，至少保留 30 秒後才走 automatic path。成功後 24 小時內不再自動查詢；失敗依 15 分鐘、1 小時、4 小時、24 小時退避。自動檢查可從 banner 或 tray 關閉；關閉後不再自動連線，若仍有 snooze，只保留不連網的本機到期檢查。若開關無法寫入狀態檔，當次執行仍立即套用，但會警告重新啟動後可能恢復舊設定；排除本機資料夾寫入問題後必須再設定一次。Tray 與 About 的手動檢查仍立即可用；可見介面會顯示 inline 結果，從 tray 發起且浮窗隱藏／收合時，失敗會顯示結果 dialog，UpToDate 也會顯示完成提示。測試與支援時要分清楚用量背景更新和 App 版本檢查，兩者有獨立狀態及排程。

App 依 running executable 與 adjacent installed manifest 分流：canonical managed install 才能在 shutdown listener ready 且固定 maintenance EXE 存在時顯示 **更新並重新啟動**；custom managed 只開 Releases，且一般 Updater 可能建立 canonical install；portable／unmanaged 以 `ProductVersion` 偵測後只開 Releases，完整 ZIP 必須 side-by-side 解壓，不能覆蓋 running tree。任何路徑、manifest、版本或 shutdown identity 歧義都降級為 Releases，不得猜 `--install-root`。

新版提示以 banner、收合 `↑` badge 與 tray action 持續保留；balloon 只是每個 `version + releaseSequence` 最多嘗試一次的補充。Banner 的 live-region announcement 會在刷新結束、浮窗重新啟用或展開後補發，並在 snooze 到期重新出現時再次宣告。**稍後提醒**只暫停該 key 的 banner／balloon 24 小時，不隱藏 badge 或 tray action。狀態檔 `%LOCALAPPDATA%\AiUsageDashboard\update-check-state-v1.json` 是可刪除的 strict cache，不是簽章或 anti-downgrade 信任根。

### 使用獨立更新程式安裝或更新

確認獨立更新程式（updater）可直接存取更新清單（feed）與檔案網址後，使用者只需要保留 `AiUsageDashboard-Updater-<version>-win-x64.exe`。直接執行時，它會檢查指定管道的最新版；若有較新的更新程式，會先下載並驗證，再交由新版完成 App 安裝。固定 maintenance Updater 正在執行而無法立即覆寫時，delegated 新版會先確認自身是 signed feed 指定的 cache artifact、direct parent 是固定 maintenance Updater，再保留已驗證 generation 與 promotion receipt。promoter 會持續等待舊程序的 exact PID／start time 自然離開，再取得 install lock；只有仍持有相符 receipt lease 的工作能以預期 canonical hash 提升固定入口，被後續 generation 取代的舊工作會安全結束。這項 handoff 由新版負責，已發布的舊 Updater 不需要預先具備 promotion 功能。失敗狀態會留在 maintenance root；固定入口已具 retry 能力時，下次 online 啟動會在讀取 feed 前以完整 receipt snapshot 安全重試。若初次 bootstrap 後固定入口仍是尚無此能力的舊版，必須保留相容 transition feed，讓下次 delegation 重建 promotion；不應先撤除舊版可驗證的簽章鏈。Windows 登錄修正成功後才會清理不再使用的 generation；解除安裝會清理由 promotion 硬中止留下、且符合嚴格自有命名的 temporary file，其他近似或不安全項目仍保留並警告。

更新程式只接受格式、管道、版本、大小、SHA-256、檔名與 HTTPS URL 全部正確的更新清單與檔案。它會拒絕降版、同版本不同內容、較舊的發布順序、轉址到非 HTTPS，以及大小或 SHA-256 不符的下載。

更新程式必須能直接存取所有網址；瀏覽器登入狀態不會自動帶入。若檔案放在 GitLab 或內部下載服務，請用 `tools\Publish-UpdateBundle.ps1` 的 `-FeedUrl` 與 `-AssetBaseUrl` 指向已核對的 HTTPS 位置，不必修改更新程式原始碼。

完整 update bundle 必須提供已核對的遞增 sequence、stable feed／asset HTTPS URL、外部可信公鑰檔與受控簽署私鑰。參數與固定格式請見 [發布流程](RELEASING.md)及[feed 格式](docs/UPDATE_FEED_FORMAT.md)。正式封裝要求有效 source SHA 與乾淨工作樹；本機驗證包不等於發布候選。

指令稿會建立且交付以下六個不可改名的檔案：

- `AiUsageDashboard-<version>-win-x64.zip`
- `AiUsageDashboard-<version>-win-x64.zip.sha256`
- `AiUsageDashboard-Updater-<version>-win-x64.exe`
- `AiUsageDashboard-Updater-<version>-win-x64.exe.sha256`
- `AiUsageDashboard-update-<channel>.json`
- `AiUsageDashboard-update-<channel>.json.sha256`

候選 workflow 產生相同 source/run/attempt 的六件。凍結前先保存 private 候選 Release；公開時只重驗並轉正同一 Release，不再重建、重包、重簽或替換 assets。正式 stable feed 使用公開 latest URL，private 受控測試 feed 另存，不代表匿名正式下載已通過。

預設安裝目錄是 `%LOCALAPPDATA%\Programs\AiUsageDashboard`，固定啟動路徑是 `current\app\AiUsageDashboard.App.exe`。這是目前 Windows 使用者專用的安裝，必須以一般權限執行；若使用系統管理員權限，更新程式會在修改檔案前拒絕。

第一次安裝前，必須完全結束所有可攜版 AI Usage。後續更新會先驗證新 ZIP，再要求目前執行中的 App 安全關閉。App 只會在背景工作全部停止後自然退出；更新程式不會強制結束程序。App 或套件內輔助程式五分鐘內未結束時，更新中止，原本的 `current` 保持不變。

安裝完成或確認為最新版後，更新程式會把維護副本放在 `%LOCALAPPDATA%\Programs\AiUsageDashboardUpdater\AiUsageDashboard.Updater.exe`，並在 Windows **已安裝的應用程式**登錄 **AI Usage**。

維護副本或登錄失敗時會顯示警告，但已安裝的 App 仍可使用。這不是 MSI，也不需要管理員權限。使用自訂 `--install-root` 的測試安裝不會覆寫正式的 Windows 應用程式項目。

### 條款與命令列入口

首次啟動 portable App 或 Updater 時可先閱讀適用條款，再接受或退出。紀錄僅存在同一 Windows 使用者的電腦，依條款內容版本與涵蓋範圍共用，不綁定 provider 帳號、不上傳。有效接受已涵蓋時，背景查詢與 helper 回呼不重複提示；條款變更後必須重新確認。

App、Updater 與隨包的 Claude capture helper 提供 `--licenses` 閱讀、`--export-licenses <新目錄>` 離線匯出，以及 `--accept-licenses <本版顯示的 digest>` 明確預先接受。AGY 設定介面在 App process 內，沿用 App 的接受狀態，沒有獨立命令列入口。非互動入口缺少有效接受會停止；請先閱讀同一版本的文字。授權提示不會混入 helper 的回呼輸出。解除安裝、關閉 App 供更新與人工離線復原不受一般啟動提示阻擋。

### 舊 internal 安裝銜接

舊 internal Updater 的 channel 已固定；只換 `--feed-url` 不會切到 stable。正式版本驗收通過後，從正式 Release 取得新 stable Updater，以原 Windows 使用者、一般權限，在原 install root 手動執行一次；自訂安裝位置以 `--install-root` 明確指定原目錄。

保留 `%LOCALAPPDATA%\AiUsageDashboard`、既有帳號、Credential Manager 與受保護 CLI，不複製或重建登入資料。完成後核對版本、maintenance 副本及 Windows 已安裝的應用程式；有警告就不要當成全部成功。後續更新使用 stable feed，仍會拒絕降版與同版本異內容。此銜接的實機結果以該 Release 為準。

### 更新切換與離線復原

更新時，既有 `current` 會先在同一磁碟改名為 `previous`，新版本再改名為 `current`。切換失敗時會立即還原；`previous` 與完成紀錄會保留供診斷。

結束碼：`0` 表示已是最新版或更新完成，`1` 表示安全中止或其他失敗，`2` 表示命令參數錯誤，`3` 表示版本已更新但重新啟動失敗。

`apply-local` 保留為維護與離線復原入口：

```powershell
.\AiUsageDashboard-Updater-<version>-win-x64.exe apply-local `
  --package <AiUsageDashboard-版本-win-x64.zip> `
  --sha256 <AiUsageDashboard-版本-win-x64.zip.sha256>
```

### 更新信任與復原驗收

stable feed 在信任 URL、hash、size、版本與 sequence 前先驗證專案簽章。首次下載仍依正式 repo／HTTPS 及發布資訊建立信任；自有 EXE 未購買 Authenticode 憑證，Windows 可能提示或依政策阻擋，不關閉防護來通過。

更新交易的 journal 與 rename 邊界須接受程序中斷、再次復原中斷、rollback 失敗、磁碟不足及未知檔案保留測試。保留既有 previous／交易紀錄供診斷；沒有確認 ownership 不做自動刪除。單元測試不等同真實斷電證據，公開前仍需乾淨 Windows 成品驗收。

`apply-local` 是使用者明確選擇可信本機 ZIP／SHA256 的人工離線復原路徑，不讀線上 feed，也不執行一般安裝入口的條款提示，不具有同等簽章與線上反降級保障。一般首次安裝請使用 Updater 的正常入口，App 的正常啟動仍須有效接受紀錄。維持 Windows 登錄、解除安裝、maintenance 自清理與使用者資料保留；使用標準安裝位置時（含 `apply-local`）會建立開始選單捷徑；自訂安裝位置與免安裝 ZIP 不建立捷徑。所有安裝方式都不會自動釘選工作列。

`transactions/<id>/transaction.json` 以 `Flush(true)` 加上同目錄 rename 提交；`receipt-*.tmp` 不會被當成已提交證據。正常更新保留 previous，rollback 保留 staging，復原不自動清除交易、暫存或未知檔案，也不改既有 downloads／updater-cache 清理規則。

legacy 中斷紀錄缺少新增的 identity 證據時會拒絕自動復原。請依序處理：

1. 退出 App／Updater，備份整個 install root。
2. 人工比對 current／previous／staging 的 manifest 與可信套件。
3. 確認可用版本及未知檔案皆已備份後，將該 legacy transaction 整個移至 install root 外另存。
4. 若 current 不可用，也先將它另存，再用可信 ZIP／sidecar 在原 install root 執行 `apply-local`。

單純重跑命令無法繞過損壞 receipt。這是人工處理程序，未宣稱硬體 write cache 或真實斷電已驗收。

### 回復舊版

浮窗目前是唯一的主介面。現有格式版本（schema）2 或 3 偏好設定仍相容：啟動畫面設定值（startup surface）為 `Dashboard`、`Widget` 或 `DashboardAndWidget` 時都會開啟浮窗；明確設為 `Tray` 時仍會保持隱藏。應用程式下次儲存偏好設定後，會將值統一寫成 `Widget` 或 `Tray`。

不同版本的應用程式共用 `%LOCALAPPDATA%\AiUsageDashboard`，不要在版本目錄之間複製該目錄。需要回復舊版時，先完全結束新版，再啟動舊版執行檔。若舊版回報設定格式較新、停用帳號編輯或要求復原，請停止操作並回到新版；不要改寫 JSON，也不要手動降級 JSON 檔案。

### 更換電腦

更換電腦前，先從舊電腦浮窗的 **⋯** 選單匯出設定檔。匯出檔未加密，只能存放在可信任的位置。

在新電腦安裝並驗證套件後，匯入帳號卡、順序、排序與顯示偏好，再逐張重新連接 Claude、Codex、GitHub Copilot、Grok 與 AGY。

Claude 的用量風險同意、Copilot 的 Windows Credential Manager 登入資料、各服務的帳號目錄、Grok 專用登入目錄與 AGY 電腦設定都不會移轉。絕對不要複製另一台電腦的 `%LOCALAPPDATA%\AiUsageDashboard`、服務帳號目錄、AGY 設定檔或 AGY 金鑰。

Windows 登入啟動項的登錄狀態，以及 Windows **啟動應用程式**中的啟用或停用狀態，都屬於目前電腦，不會隨匯出的設定檔移轉，也不會因匯入而變更。換機後如需登入後自動啟動，請在新電腦重新登錄並確認 Windows 未停用。

## 移除程式與本機資料

### 移除程式

若由獨立更新程式安裝，請從 Windows **已安裝的應用程式**解除安裝 **AI Usage**。解除安裝程式會先驗證安裝內容並安全關閉 App；不要自行強制結束程序。

若 Windows 項目遺失，受信任維護人員可從安裝目錄外執行：

```powershell
AiUsageDashboard.Updater.exe uninstall --confirm
```

解除安裝必須以一般權限執行，也不能從安裝目錄或 `updater-cache` 內啟動。程式只會清除自己管理的 `current`、`transactions`、`downloads`、`updater-cache` 及鎖定／完成紀錄。

待刪除目錄不得含 reparse point。未知的頂層項目會保留，根目錄只有在空白時才移除；中途失敗時會保留可供下次接續的紀錄。最後執行中的維護 EXE 只會交由目前使用者的隱藏程序刪除該精確檔案，不會使用萬用字元或遞迴刪除。

若使用離線 ZIP，請先結束 AI Usage，再刪除自行解壓的版本目錄。兩種方式都會保留使用者資料、Copilot 的 Windows Credential Manager 登入資料，以及 Claude、Codex、Copilot、Grok 的受保護執行副本，供日後重新安裝使用。

永久刪除本機資料是另一項不可復原的操作。執行前，資料擁有者必須確認會失去 AI Usage 設定、快取、診斷資料、各服務的本機登入、受保護執行副本及 AGY 核准來源。

請依下列順序清理：

1. 若 AI Usage 還能正常開啟，先逐張移除所有 Copilot 卡片，等待清理警告消失。內建清理會依卡片識別碼刪除 Windows Credential Manager 中使用中（active）與待完成（pending）的登入資料，以及該卡片的本機目錄。
2. 如果 AI Usage 無法開啟，請停止一般清理流程。現行套件沒有離線批次清除 Copilot 登入資料的工具。
3. 離線清理必須由受信任工具從已確認的帳號狀態取得完整且正確的帳號 ID（account ID）。只刪除 Windows Credential Manager 中完全相符的 `AiUsageDashboard/Copilot/<account-id>` 與 `AiUsageDashboard/Copilot/Pending/<account-id>`。
4. Account ID 必須是 32 位十六進位格式，且不含連字號。不得使用萬用字元、前綴批次刪除或未核對身分的命令。無法確認精確目標時，不能宣稱已永久清除。
5. 從系統匣結束 AI Usage，確認目前使用者的 Windows 登入期間沒有 AI Usage 或其 Claude、Codex、Grok、Copilot 子程序仍在執行。
6. 由受信任工具從目前 `WindowsIdentity` 取得完整 SID，並從 Windows system directory 取得 system-volume root。不得接受手動輸入的 SID、替代磁碟或環境變數覆寫。
7. 只用上一步取得的值建立 Claude、Codex、Copilot 與 Grok 四個完整路徑：`%SystemDrive%\AiUsageDashboard.<Provider>Cli.<current-user-SID>`，其中 `<Provider>` 只能替換成這四個服務名稱之一。
8. 對每個存在的目錄重新檢查：它必須位於固定磁碟，所有路徑都沒有 reparse point，擁有者是目前使用者 SID，而且 DACL 已停用繼承，只授予目前使用者、Local System (`S-1-5-18`) 與 Builtin Administrators (`S-1-5-32-544`) Full Control。任一條件不符就停止，不得遞迴刪除。
9. 只刪除目前使用者的 `%LOCALAPPDATA%\AiUsageDashboard`，以及通過上一步全部檢查的四個完整服務目錄。不得使用父目錄、萬用字元、前綴比對或跟隨 junction 擴大範圍。刪除整個 `%LOCALAPPDATA%\AiUsageDashboard` 也會刪除其中的 AGY 本機連接資料與核准來源；若要保留既有 AGY 連接，不得執行完整資料清除。
10. 若曾使用 AGY，再清除目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 與 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE` 環境變數。不得碰觸其他使用者的設定、受保護目錄或共用磁碟內容。

### 資料範圍與帳號移除

從浮窗移除卡片，會移除該帳號的 AI Usage 設定與用量快取。各服務的差異如下：

- Claude、Codex：不會刪除官方 CLI 的完整登入狀態。
- GitHub Copilot：會刪除該卡片在 Windows Credential Manager 中使用中與待完成的登入資料，以及該卡片的本機目錄；不影響其他 Copilot 卡片。
- Grok：會刪除該卡片的連接狀態、本機登入與 `%LOCALAPPDATA%\AiUsageDashboard\grok\<account-id>`。
- AGY：會保留這台電腦共用的 approved source 與安全復原狀態。

清理失敗時，介面會顯示警告並在背景重試。移除卡片都不代表已從服務端登出。

本機資料主要包括：

- `%LOCALAPPDATA%\AiUsageDashboard\accounts.json*`：AI Usage 帳號設定。
- `%LOCALAPPDATA%\AiUsageDashboard\preferences.json`：介面偏好設定。
- `%LOCALAPPDATA%\AiUsageDashboard\update-check-state-v1.json`：更新檢查開關、節流、前次結果、snooze 與通知 attempt cache；不含 feed URL、下載 URL 或簽章信任鍵。
- `%LOCALAPPDATA%\AiUsageDashboard\<provider>\<account-id>\usage-snapshot-v1.json`：各帳號分開保存的上次用量。
- `%LOCALAPPDATA%\AiUsageDashboard\diagnostics.log`：有容量上限的診斷紀錄。
- `%LOCALAPPDATA%\AiUsageDashboard\claude\<account-id>`：帳號專屬的 Claude CLI 設定與登入資料。
- `%LOCALAPPDATA%\AiUsageDashboard\codex\<account-id>`：帳號專屬的 Codex CLI 設定與登入資料。
- `%LOCALAPPDATA%\AiUsageDashboard\copilot\<account-id>`：Copilot 卡片的隔離執行目錄；登入 token 不在這裡。
- `%LOCALAPPDATA%\AiUsageDashboard\grok\<account-id>`：帳號專屬的 Grok 主目錄、空白工作目錄、私密連接資料、快取與本機登入；移除該卡時會整體清理。
- Windows Credential Manager 的 `AiUsageDashboard/Copilot/<account-id>` 與 `AiUsageDashboard/Copilot/Pending/<account-id>`：Copilot 卡片目前使用與暫存中的登入資料。實際帳號 ID 使用 32 位十六進位格式，不含連字號。
- `%SystemDrive%\AiUsageDashboard.ClaudeCli.<current-user-SID>\executables-v1`：目前 Windows 使用者共用、依檔案內容雜湊分類的 Claude 受保護執行檔版本；正常保留目前與前一個有效版本，安全清理失敗時可暫時超過兩份，移除單張卡片時不清理。
- `%SystemDrive%\AiUsageDashboard.CodexCli.<current-user-SID>\executables-v1`：目前 Windows 使用者共用、依檔案內容雜湊分類的 Codex 受保護執行檔版本；正常保留目前與前一個有效版本，安全清理失敗時可暫時超過兩份，移除單張卡片時不清理。
- `%SystemDrive%\AiUsageDashboard.CodexCli.<current-user-SID>\validation-v1`：Codex 版本檢查使用的私密根目錄；每次正常檢查會刪除自己的 `probe-<guid>` 目錄，移除單張卡片時不清理根目錄。
- `%SystemDrive%\AiUsageDashboard.CopilotCli.<current-user-SID>\executables-v1`：目前 Windows 使用者共用的 Copilot 受保護執行檔副本；只有 Copilot 操作時才取得，移除卡片不清理此目錄。
- `%SystemDrive%\AiUsageDashboard.GrokCli.<current-user-SID>\executables-v1`：目前 Windows 使用者共用、依檔案內容雜湊分類的 Grok 受保護執行檔版本；正常保留目前與前一個有效版本，安全清理失敗時可暫時超過兩份，移除單張卡片時不清理。
- `%LOCALAPPDATA%\AiUsageDashboard\grok-connection-pending-v1.json`：不含 token／原始帳號的 Grok 連接續做狀態。
- `%LOCALAPPDATA%\AiUsageDashboard\account-cleanup-pending-v1.json`：帳號清理失敗後供背景重試的狀態。
- `%LOCALAPPDATA%\AiUsageDashboard\portable-settings-import-transaction-v1`：設定匯入中斷後的復原紀錄，以及匯入前檔案狀態與備份。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity-connection-pending-v1.json`：AGY 連接中斷後供安全續做的狀態。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\official-print-safety-v1.json` 與 `official-print-safety-v1.json.journal`：AGY 官方用量輸出的安全狀態與中斷復原紀錄。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\setup-attempt-states-v1` 與 `setup-approval-receipts-v1`：AGY 設定嘗試狀態與核准完成紀錄。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\<account-id>\account-display-binding-v1.json`：AGY 帳號卡的本機顯示身分綁定。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\approved-source-v1.json`：核准的 official print 來源種類與 AGY 執行檔絕對路徑。
- `%LOCALAPPDATA%\AiUsageDashboard\private\antigravity-statusline`：舊版可能留下的自有 status-line helper／擷取資料；新版只在精確驗證 ownership 與私人 ACL 後清理，不作為資料來源。
- 目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE`：舊版持久化設定；只有新 approved-source 檔缺少時才一次性遷移。
- 目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE`：已退役的舊版 ConPTY 設定，production 不讀取。

只有在資料擁有者確認正確帳號，並接受官方 CLI 登入將會遺失後，才能手動刪除登入或設定目錄。一般移除卡片時，絕對不要遞迴刪除整個 `AiUsageDashboard` 目錄；Copilot 與 Grok 應交由內建的單卡片清理處理。

移除 AGY 卡片會保留目前使用者的 approved source，方便日後重新連接。若要完全停用並清除 AI Usage 的 AGY 資料，請依上方完整資料清除流程處理；舊環境變數也要逐一以目前使用者範圍清除。不要手動遞迴刪除名稱近似的 status-line 或 private 目錄。

## 候選版本驗收清單

每個候選版本都必須在非開發用 Windows x64 電腦執行此清單。每項記錄為通過、失敗或不適用；不適用時要寫明原因。任何原因不明的失敗都會阻止發佈。

驗收 Windows 登入啟動時，不要同時用 Registry Editor 或其他管理工具修改同名的 `AiUsageDashboard` 項目。App 與解除安裝程式會避免彼此同時寫入，但無法保證不會與外部工具衝突。

### 套件與啟動

- [ ] 記錄候選版本、完整 commit SHA、Actions 執行網址、測試者與日期、Windows 版本、各服務已安裝的 CLI 完整版本，以及各版本是否等於本版相容性基準；不相同時先標記為未驗證，完成基本啟動與功能檢查後再記錄結果，不得直接標成不支援。
- [ ] 驗證 App ZIP、獨立更新程式與更新清單的三份相鄰 SHA-256，並確認清單內的版本、發布序號、大小、SHA-256、`sourceRevision` 與實際六個檔案一致。
- [ ] 確認根目錄說明可以開啟，且執行檔的 Product version 與候選版本紀錄一致。
- [ ] 列舉 `app` 中的 EXE：只允許 `AiUsageDashboard.App.exe` 與 `AiUsageDashboard.ClaudeCapture.exe`；必須有 `AiUsageDashboard.Antigravity.Setup.dll`，且不得有 `AiUsageDashboard.Antigravity.Setup.exe`、`AiUsageDashboard.AntigravityCapture.exe` 或 `createdump.exe`。
- [ ] 在沒有開發用 SDK 或專案原始碼的電腦上，確認應用程式已完全結束後再啟動，並確認浮窗、系統匣與帳號編輯器皆正常；另確認不再有獨立的 Dashboard 視窗或開啟 Dashboard 的選單項目。
- [ ] 啟用 Windows 登入啟動，分別保存浮窗顯示與 Tray 隱藏狀態後實際登出／登入，確認依偏好啟動且不搶焦點。已有程式執行時，另執行 `--startup`，確認安靜結束且不喚回浮窗。
- [ ] 從 Windows **啟動應用程式**停用再啟用，確認實際啟動狀態跟著改變。分別在已登錄、未登錄及被 Windows 停用時更新，確認更新程式不會自行建立或修復 `HKCU Run` 值。解除安裝只清除完全相符的值；衝突值須保留並顯示警告。

### 服務與更新行為

- [ ] Claude：確認程式只執行通過路徑、大小、`Anthropic, PBC` 簽章、SHA-256 與權限檢查的受保護副本。操作結束前不得覆寫或刪除副本；無法確認子程序已結束時，後續操作須停止到 App 重新啟動。
- [ ] Claude：拒絕風險提示時，不得開始登入或呼叫 `/usage`。接受後完成登入並進入 **可用**，記錄 `turns`、`tokens` 與 `cost` 都是零。
- [ ] Codex：確認程式只執行通過路徑、大小、`OpenAI OpCo, LLC` 簽章、SHA-256 與權限檢查的受保護副本。完成官方登入後，確認帳號、方案與所有預期的用量週期，且不建立 turn。
- [ ] Codex：登入或查詢結束前不得解除副本鎖定。模擬無法確認子程序已結束後，所有 Codex 啟動都須停止到 App 重新啟動；正常的 `validation-v1\probe-<guid>` 測試目錄則應自動清理。
- [ ] Grok：確認只接受 `%USERPROFILE%\.grok\bin\grok.exe`，並通過路徑、大小、`X.AI LLC` 簽章、SHA-256 與權限檢查。版本不同於 `1.0.3` 時仍要先實測，不得只因版號不同而拒絕。
- [ ] Grok：完成登入並確認每週用量與重置時間。更新官方 CLI、從系統匣結束再重開後，應建立新的受保護副本，且不再次開啟登入。無法確認子程序已結束時，後續操作須停止到 App 重新啟動。
- [ ] 受保護副本清理：分別更新 Claude、Codex 與 Grok，確認正常只保留目前與前一個有效副本。使用中、來源可疑或刪除失敗的檔案不得強制刪除；問題排除後才於下次更新清理。三個目錄不得開放給其他一般使用者。
- [ ] Grok 多帳號：用已綁定的同一帳號連接第二張卡片時，必須拒絕且不改變第一張卡。改用另一個帳號後，兩張卡的登入、連接狀態、快取、重新啟動及手動更新都應彼此隔離。移除其中一張後，其本機目錄應消失，另一張仍可更新。
- [ ] Grok 中斷復原：分別在登入完成狀態寫入前後中止程式。寫入後重開應重新驗證帳號並接續；寫入前重開則應要求重新連接，不得誤綁或洩漏帳號原始資料。
- [ ] GitHub Copilot：使用兩個不同的 `github.com` 帳號連接兩張卡片，確認重新啟動及逐卡更新後仍各自顯示正確帳號和用量。再用第一個帳號連接第三張卡片，確認重複綁定被拒絕且既有兩張卡不變。
- [ ] GitHub Copilot：使其中一張卡片收到 `401`，確認只要求該卡重新連接。移除其中一張後，該卡在 Credential Manager 中使用中與待完成的項目及本機目錄必須清除，另一張仍可更新。
- [ ] AGY 官方用量輸出：記錄 `1.1.11 <= version < 2.0.0` 的實際版本；保持既有 AGY 視窗開啟仍能完成連接。確認設定視窗屬於 App process、approved source 已保存、來源與 Google 簽章通過、固定參數正確、四個用量週期存在，以及 `conversation`、`turn`、所有 `token` 都是零。
- [ ] AGY process 邊界：確認 version probe 與 `/usage` 都直接執行 approved executable、不經 shell，環境沒有 `PATH`／`COMSPEC`，Job active-process limit 為 `1`；整段操作不得出現 `cmd.exe`、Setup EXE、AGY capture EXE 或其他 child process。
- [ ] AGY 安全拒絕：把 approved source 改成無效路徑或不受信任測試檔，確認不會改用環境變數、ConPTY 或舊版 profile。格式錯誤、多出資料組、非零 `turn`／`token`、截斷輸出或 child process 嘗試都必須拒絕，且不得保存原始 JSON。
- [ ] AGY status line：以非 AI Usage owned 的 `statusLine.enabled=true` 設定及一份自訂 status line 各測一次；連接與更新必須正常，不要求 `/statusline off`，設定檔及自訂 command bytes 必須保持不變。
- [ ] AGY 舊版清理：準備舊版精確 AI Usage ownership marker 與對應 hash-named helper，確認只移除該 `statusLine` 欄位與自有檔案；自訂、格式不明、路徑或 ACL 不符、以及同時遭其他程序更新的設定都必須保留並 fail closed。
- [ ] AGY 中斷續做：按下 **完成連接** 後，在 in-process 設定提交期間結束並重開 AI Usage，確認重新驗證 approved source 與用量後自動接續；不得要求使用者再按一次，也不得依舊用量猜測完成。
- [ ] 重新開啟後，程式必須重新驗證來源、信任與帳號，再自動完成設定與快取；不得用舊用量猜測完成。來源或帳號不符時不得誤綁。
- [ ] 驗證排程更新依照設定間隔執行、更新頻率符合預期，且失敗時會顯示上次正常資料；並確認背景更新絕不會開始互動式登入。
- [ ] 驗證已使用／剩餘顯示、手動排序、依重置時間自動排序、各服務顏色，以及用量／重置時間的警告顏色。
- [ ] 建立包含多個服務、暱稱、啟用狀態與手動順序的設定。匯出後修改目前設定，再匯入並確認預覽數量、順序、模式、卡片及顯示偏好都正確還原。
- [ ] 在同一次執行期間使用 **還原匯入前設定**，確認回到匯入前狀態；再修改任一設定，確認還原入口失效。
- [ ] 匯出 JSON 不得包含密碼、token、登入資料、Claude 風險同意、Copilot 登入或綁定、Grok 帳號指紋或專用目錄、AGY 機器綁定、用量快取或浮窗位置。
- [ ] 在隔離的 Windows 使用者匯入同一份設定，確認 Claude、Codex、GitHub Copilot、Grok 與 AGY 卡片都要求重新連接。再匯入格式無效與較新格式版本的檔案，兩者都必須拒絕且目前設定保持不變。

### 視窗、重新啟動、更新與回復舊版

- [ ] 使用浮窗完成所有一般操作；把浮窗移到不同螢幕，確認能貼齊螢幕角落、收合／展開與切換置頂。
- [ ] 分別從全域選單與系統匣隱藏浮窗，再用 **顯示浮窗** 與雙擊系統匣圖示恢復；接著完全結束並重新啟動，確認帳號、卡片順序、偏好設定與上次用量。
- [ ] 使用含 `Dashboard` 或 `DashboardAndWidget` 舊格式偏好設定的版本升級一次，確認浮窗會顯示；再以 `Tray` 重複測試，確認應用程式保持隱藏，直到從系統匣恢復。
- [ ] 使用受控 HTTPS signed feed 驗證首次 non-modal 說明先於自動連線、30 秒 delay、24 小時成功節流、四級失敗退避、Windows resume、關閉自動檢查及 tray／About 手動 bypass；確認背景失敗只寫 diagnostic，手動失敗才顯示可操作訊息。
- [ ] 分別以 canonical managed、custom managed 與 portable 成品提供新版：核對 banner、收合 `↑`、tooltip／螢幕閱讀器名稱、tray action、24 小時 snooze 與每 release 一次的 balloon attempt。只有 canonical 且 shutdown listener ready、maintenance EXE 存在時可啟動一鍵更新；其他兩類只能開固定 Releases。
- [ ] 移除或破壞 portable `ProductVersion`、managed manifest、maintenance EXE 與 shutdown listener，各自確認 fail closed：不能顯示 up-to-date、不能猜安裝目錄、不能提前關閉 App。以舊 sequence、同版本不同 bytes、壞簽、錯誤 channel 與 redirect 降級重跑 App checker；Updater 安裝時仍須獨立再驗一次。
- [ ] 保留一份較舊更新程式，在乾淨安裝目錄執行首次安裝，再讓更新清單提供新版更新程式與 App。確認舊版會交由已驗證的新版接手、執行中的固定入口會經 generation／receipt／exact parent identity 提升且下次從新版固定入口啟動，並以 `A → A+B → B` 信任鍵過渡確認舊版不會卡住更新鏈；固定啟動路徑不含版本、`previous` 保留舊版，且帳號資料仍只在 `%LOCALAPPDATA%\AiUsageDashboard`。若可攜版 AI Usage 尚未結束，首次安裝必須拒絕且不替換檔案。
- [ ] 分別在已是最新版、沒有服務工作、可正常等待工作結束，以及等待逾時時執行更新程式。可更新時只允許 App 自然退出；等待逾時須保留原 `current` 與失敗紀錄，不得強制結束程序。
- [ ] 再測更新清單／管道不符、降版、發布順序重播、SHA-256／大小錯誤、轉址到非 HTTPS、ZIP 路徑穿越或含重新解析點、同時執行更新程式、檔案鎖定及重啟失敗。
- [ ] 一般解除安裝：在 `%LOCALAPPDATA%\AiUsageDashboard` 建立測試檔，再從 Windows **已安裝的應用程式**解除安裝。確認 App 安全關閉、顯示完成通知、程式檔與 **AI Usage** 登錄項目已移除，測試檔仍保留。
- [ ] 安靜解除安裝：重新安裝候選版並建立同樣的測試檔，執行登錄項目中的 `QuietUninstallString`。確認沒有完成通知，其餘程式、登錄與使用者資料結果和一般解除安裝相同。
- [ ] 確認帳號設定從 v6 升級到目前的 v7。先用上一個已知正常的 v6 套件建立多個服務、暱稱與 Claude 風險同意，再用 v7 候選版本開啟。帳號 ID、順序、啟用狀態、暱稱與同意狀態都必須保留；新增的 **顯示組織**／**顯示 workspace** 預設為關閉。
- [ ] 在 v7 重新連接至少一張 Claude 與 Codex 卡片，分別選擇組織與 workspace，再開啟上述顯示選項。儲存並重新啟動後，確認綁定、顯示選項與對應資訊都保留。完全結束後，再用同一個 v6 套件開啟已寫成 v7 的主要與備份設定；舊版必須回報較新格式、停用編輯與儲存，且不得改變檔案。回到 v7 後，資料仍須完整。這是預期的安全拒絕，不是成功回復舊版。
- [ ] 確認診斷與 ZIP 不含密碼、token、Copilot Credential Manager 資料、原始 Grok 登入／帳號／連接資料、Grok 專用目錄、原始 AGY 終端或 JSON、帳號設定、用量快取、PDB 或開發用 Spike 工具。

## 支援與復原

- Claude／Codex CLI 遺失或不相容：先記錄偵測版本與完整原始錯誤。版本差異只是可能原因；若官方來源、簽章與必要功能都合格，先重新啟動 AI Usage，讓程式自行建立或更新受保護副本，再對照候選版試用紀錄。不得手動複製受保護執行檔或放寬目錄權限；既有帳號識別會保留。
- GitHub Copilot CLI 缺少或不符：依[官方文件](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli)安裝或更新，建議 WinGet 標準安裝；來源也可為標準 npm 安裝或 `PATH` 可解析的真正官方 `copilot.exe`。AI Usage 不執行 `.cmd`／`.ps1` wrapper、不退回舊 App bundle；卡片及 token 保留，修正 CLI 後重試，不先重新登入。精確來源與版本規則見[技術總覽](docs/TECHNICAL_OVERVIEW.md#本機-cli-來源與版本)。
- GitHub Copilot 要求重新連接：只重新連接受影響的卡片。若移除卡片後仍顯示清理警告，保持 AI Usage 開啟以完成背景重試；不要手動批次刪除 Windows Credential Manager 項目。
- Grok CLI 遺失或來源不受信任：只能從 xAI 官方來源安裝到 `%USERPROFILE%\.grok\bin\grok.exe`。重新啟動 AI Usage，讓程式自行更新受保護副本，並確認 `X.AI LLC` 簽章與路徑檢查通過。`1.0.3` 是相容性基準；不得只因版本不同而要求降版，也不得用 `PATH`、手動複製或替代執行檔繞過檢查。
- 服務暫時失敗：稍後重試；畫面會保留上次正常的用量資料。
- 明確的登入驗證失敗：只重新連接受影響的帳號。
- Grok 連接中斷：先重新啟動 AI Usage。若待處理紀錄已保存 `LoginCompleted`，程式會用新取得的帳號資料驗證後接續；否則卡片會要求重新連接。不要手動複製、編輯或刪除連接／待處理檔案。
- Grok 帳號衝突：同一個實際帳號不能綁定兩張卡片。確認登入終端使用預定帳號，再移除錯誤或重複卡片並重新連接；不得改寫公開連接 ID 或私密指紋。
- Grok 清理警告：保持 AI Usage 開啟以完成背景重試，確認警告消失且只有目標 `%LOCALAPPDATA%\AiUsageDashboard\grok\<account-id>` 被清除。不要遞迴刪除整個 `grok` 目錄，以免破壞其他帳號。
- AGY 登入來源不同：使用 **重新連接 Antigravity 帳號** 或 **連接 Antigravity 帳號**，並核對新的四個用量週期；不必關閉既有 AGY 視窗，也不需執行 `/statusline off`。不要沿用其他電腦的 approved source 或設定。
- AGY 來源拒絕：確認 approved source 指向官方安裝的一般 `.exe`，版本在 `1.1.11 <= version < 2.0.0`。不得改走環境變數、舊版 profile 或 ConPTY。執行 `agy --version`，並回報畫面上的完整安全診斷區塊。
- AGY 用量暫時失敗：程序逾時、取消、一般錯誤、非零結束碼或輸出格式錯誤時，程式會在背景以 1、2、4、8、最多 15 分鐘間隔重試。期間保留上次正常用量，重開程式後仍會接續。
- AGY 用量安全問題：只有舊格式狀態、無法確認子程序已結束、偵測到用量活動，或安全狀態損壞／無法保存時才會停止，並顯示 **重新檢查 Antigravity 用量**。成功後才清除標記；不得手動刪除標記來繞過安全檢查。
- 診斷紀錄不得包含登入資料、原始 Grok OAuth／ACP／帳號／連接內容或 AGY 終端內容。支援時只分享必要且已遮蔽敏感資訊的最少行數。
- 支援回報必須包含套件 ZIP 檔名、Windows 檔案內容中執行檔的 **Product version**、Windows 版本、受影響服務的 CLI 版本與畫面上可見的錯誤。絕對不要要求對方提供整個本機應用程式資料目錄。

交付團隊前，必須在非開發用 Windows 電腦確認 AI Usage 已完全結束後再啟動，並記錄 Claude、Codex、GitHub Copilot、Grok 與 AGY 的試用結果。未實測的帳號組合必須繼續標示為未驗證。
