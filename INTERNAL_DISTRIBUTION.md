# Windows 分發與支援手冊

讀者：AI Usage 的維護者與協助安裝、更新或排錯的人員。本手冊說明目前 source 的 Windows x64 行為；實際成品是否驗收通過，以該版本 Release 的紀錄為準。一般使用者請優先讀隨附指南。

一般使用者請閱讀 [`使用說明.md`](使用說明.md)。正式 ZIP 只附使用者指南；本手冊留在專案原始碼庫，供維護與支援人員查閱，不隨套件分發。

本手冊只說明前置條件、操作步驟、預期結果與失敗處理。執行檔驗證、程序隔離、更新切換與中斷復原的完整設計，請查閱 [`docs/TECHNICAL_OVERVIEW.md`](docs/TECHNICAL_OVERVIEW.md)。

## 支援範圍

| 元件 | 內部版本政策與支援範圍 |
| --- | --- |
| AI Usage | 有版本編號、自帶執行環境的 Windows x64 套件；目前使用 .NET 8.0.31，僅支援至 2026-11-10 |
| Claude | 相容性基準 `2.1.169`；官方 Claude Code `>=2.1.169` 才有需要的 `--safe-mode`；執行 `Anthropic, PBC` 簽署、目前 Windows 使用者專用的受保護副本；Claude.ai Pro／Max／Team；Enterprise 可辨識，但目前不提供 `/usage`；實驗性輪詢 |
| Codex | 相容性基準 `0.144.1`；可確認為官方 `codex-cli x.y.z` 的三段式正式版本都會先實測；執行 `OpenAI OpCo, LLC` 簽署、目前 Windows 使用者專用的受保護副本；實驗性 `app-server` 串接 |
| Grok | 相容性基準 `1.0.3`；可信任的官方版本即使版號不同或無法解析，仍先測試 ACP 通訊；只接受預設安裝位置、`X.AI LLC` 簽章與目前 Windows 使用者專用的受保護副本；實驗性多帳號串接 |
| GitHub Copilot | 隨附官方 Copilot CLI `1.0.79` 與 `GitHub.Copilot.SDK` `1.0.11`；只支援不同的 `github.com` 帳號；每張卡片分開保存登入資料 |
| AGY | 相容性基準 `1.1.11`；Windows 10 1809 以上；官方用量輸出功能（official print）只執行 `1.1.11 <= version < 2.0.0` 的正式版本，並核對 Google 簽章；既有 1.1.7／1.1.9 只保留已審查 SHA-256 的相容流程 |

相容性基準只供比較和診斷，不是允許版本清單。其他官方版本只要通過必要功能與安全檢查，就應先實測；不得只因版號不同而拒絕，也不得把版本差異寫成已確認的失敗原因。每個候選版本仍須記錄實際 CLI 版本與測試結果。

### 執行檔安全要求

Claude、Codex 與 Grok 只會執行 AI Usage 建立的受保護副本。程式會鎖住來源檔，確認檔案位於本機固定磁碟、路徑沒有重新解析點（reparse point）、大小不超過 512 MiB、Windows 簽章與發行者正確，再依 SHA-256 建立副本。受保護目錄只開放目前使用者、Local System 與 Builtin Administrators 完整存取：

- `%SystemDrive%\AiUsageDashboard.ClaudeCli.<current-user-SID>\executables-v1`
- `%SystemDrive%\AiUsageDashboard.CodexCli.<current-user-SID>\executables-v1`
- `%SystemDrive%\AiUsageDashboard.GrokCli.<current-user-SID>\executables-v1`

正常情況下，每個服務只保留目前與前一個有效副本。若檔案仍在使用、安全檢查失敗或刪除失敗，必須先保留，待下次再清理。不得強制刪除、放寬權限或手動替換副本。

程序結束前，副本必須保持鎖定。若程式無法確認整個子程序樹都已結束，Claude、Codex 或 Grok 的相關操作必須停止到 AI Usage 重新啟動，不得在狀態不明時繼續啟動新的程序。

Grok 只接受 `%USERPROFILE%\.grok\bin\grok.exe`，發行者必須為 `X.AI LLC`。AGY 每次執行前都要重新確認官方路徑、版本、Google 簽章與檔案狀態；官方來源驗證失敗時不得改走舊版相容流程。不要從其他電腦複製服務執行檔、帳號目錄、受保護副本或登入資料，也不要用 `PATH` 或替代檔案繞過檢查。

執行環境期限依照官方 [.NET 支援政策](https://dotnet.microsoft.com/en-us/platform/support/policy)。請在期限前完成移轉，並確認新的 .NET 執行環境仍在支援期。套件自帶的執行環境一旦超出支援期限，就不會繼續取得修正。

## 發佈前檢查

先完成 [發布流程](RELEASING.md) 的來源、免費額度、精確授權文字、條款接受、簽章、版本及 private／public 分段驗收。驗證三份相鄰的 `.sha256` 後，仍須先驗證 feed 的專案簽章與 pinned trust key；同站雜湊不能單獨證明發布者。

Claude、Codex、GitHub Copilot、Grok 與 AGY 都須記錄實際 CLI 版本及本人帳號測試結果。Claude 背景查詢可能增加用量或費用，使用者須自行同意；執行後檢查不能撤回已產生的用量。Claude／AGY 的個案條款不確定性及 Microsoft publisher 義務留在最終發布決策，不設公司審批或個別廠商回函的必經步驟。

## 套件內容

ZIP 固定使用簡短的根目錄 `AiUsageDashboard`，避免 Windows 解壓縮路徑重複包含完整版本名稱。以下路徑都以該目錄為基準：

- `使用說明.md`：放在解壓縮套件根目錄，供一般使用者閱讀的繁中說明。
- `app\AiUsageDashboard.App.exe`：一般使用者唯一需要啟動的執行檔。
- `app\README.md`：內容與一般使用者說明相同；應用程式會以這個檔名開啟本機復原指引。
- `app\third-party-notices\`：包含元件交付清單與實際版本的第三方授權、附加條款及 notices；自有 LICENSE 也放在 app 內。封裝時會調整根目錄使用說明的相對連結，讓兩份指南都能離線開啟授權文件；套件根目錄維持只有 `app\` 與 `使用說明.md`。

內部分發、驗證與復原流程只保留在專案原始碼庫的 `INTERNAL_DISTRIBUTION.md`，不放入使用者 ZIP。

## 安裝與啟動

1. 請使用者先閱讀根目錄的 `使用說明.md`。
2. 使用相鄰的 `.sha256` 檔案驗證 ZIP。
3. 把 ZIP 解壓縮至新的、有版本區隔且使用者可寫入的目錄，再開啟其中的 `AiUsageDashboard` 目錄。
4. 只安裝使用者要新增之服務所需、且已核准的官方 CLI。
5. 啟動 `app\AiUsageDashboard.App.exe`。這是唯一的一般啟動入口。
6. 首次使用時，從浮窗新增需要的帳號。換機或重建設定時，可從浮窗的 **⋯** 選單匯入先前匯出的設定檔，再逐張重新連接 Claude、Codex、GitHub Copilot、Grok 與 AGY。這些服務的本機登入資料或電腦綁定不會隨匯入移轉。AGY 為選用服務；使用者選擇 Antigravity 後才會開始連接，受支援的新版本不必先關閉既有 AGY 視窗。

不要複製其他使用者的 `%LOCALAPPDATA%\AiUsageDashboard` 目錄。裡面可能包含帳號專屬的 CLI 狀態、Grok 登入目錄、用量快取、診斷資訊，以及 AGY 私密校準資料。

### Claude 使用者同意

Claude 背景查詢只有在 model turns、tokens 與 cost 全部恰好為零時才接受結果。這項檢查發生在 CLI 命令執行之後；若結果異常，可以阻止之後的查詢，卻無法撤回這次查詢已經產生的用量或費用。

每位使用者須自行接受 Claude 的用量風險，決定寫在每張卡片的本機設定。一般第三方條款另依同一 Windows 使用者、版本與範圍在本機共用；不建立或上傳試用者名單。

新卡片或升級後的卡片預設為未接受；使用者明確接受並儲存設定前，程式不會登入或呼叫 `/usage`。拒絕 Claude 風險不影響 Codex、GitHub Copilot、Grok 或 AGY。Codex 用量查詢不會建立 turn；AGY 仍會要求回傳結果中的 turn 與所有 token 都是零。上游說明不代表 Google 對 AI Usage 背書。

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
3. 應用程式會暫停該卡片的輪詢，並開啟套件內的輔助程式。
4. 核對「目前登入的 Antigravity 帳號」與四個用量週期，再選擇 **完成連接**。

整個流程中浮窗都會保持開啟。成功後，應用程式會立即更新卡片。最終確認前取消或關閉設定視窗，卡片會維持原狀；按下 **完成連接** 後若程式中斷，重新開啟 AI Usage 會重新驗證來源、程序與帳號，再自動接續，不應要求使用者再次確認。選擇 **儲存** 只會建立帳號；之後可從 **帳號設定** 選擇 **連接 Antigravity 帳號**。

AGY 的用量結果不含 email。卡片保存的是這台電腦核准的 AGY 登入來源，不是可跨電腦使用的帳號識別。

若使用者沒有自訂狀態列（status line），AI Usage 會加入自己的單檔輔助程式，從官方資料讀取 `email`、回報時間與選填的 `plan_tier`。

原始顯示資料保存於 `%LOCALAPPDATA%\AiUsageDashboard\private\antigravity-statusline\account-display-v1.json`。同一次更新確認可安全配對後，方案名稱也可能寫入該卡片的本機用量快取。這些資料不會匯出，也不能用來重新綁定卡片或當成切換帳號的安全證明。既有的自訂狀態列絕不可覆寫。

若 AGY 更新後仍在支援範圍內，AI Usage 會在每次讀取時重新驗證來源與版本。若卡片仍顯示待處理操作，請使用 **重新連接 Antigravity 帳號** 或 **連接 Antigravity 帳號**。版本、簽章或路徑無法確認時必須停止，不得改用舊版相容設定。

不要要求一般使用者自行尋找或啟動 `app\AiUsageDashboard.Antigravity.Setup.exe`。這是與 AI Usage 共用自帶執行環境目錄的內部輔助程式。只有支援復原時可以直接啟動：先完全結束 AI Usage，再啟動輔助程式，並完成最終的帳號與用量確認。

### AGY 維護安全注意事項

官方用量輸出流程會把核准的執行檔完整路徑寫入目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE`。設定與每次讀取都必須重新確認：

- 它是一般本機 `.exe`，路徑沒有 reparse point。
- Windows 信任簽章通過，Google LLC 簽章名稱與憑證指紋（thumbprint）相符。
- 版本在 `1.1.11 <= version < 2.0.0`，而且檢查前後檔案沒有改變。

來源驗證通過後，只能直接執行 `agy -p /usage --output-format stream-json`，不得透過 shell。輸出大小有固定上限；解析器只接受 `usage` 指令的兩組資料、每組兩個週期，並要求成功、conversation／turn 為零、所有 token 精確為零。原始 JSON 只在記憶體中使用，用完即清除，不得寫入診斷、快取或套件。

AGY 1.1.7／1.1.9 只保留已審查 SHA-256 的 ConPTY R1 相容流程，使用 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE` 與私密設定檔（profile）／金鑰（key）。

輔助程式不接受任意設定檔路徑，也不顯示或封裝原始終端內容、本機路徑、指紋、金鑰、登入資料、原始帳號或用量資料。舊版畫面或設定有變動，或正式版本的版本、簽章、資料格式、零 token 檢查任一失敗，都不得寫入設定。

### 舊版 AGY 建置的維護審查

以下流程只適用於已審查 SHA-256 的 ConPTY 相容流程。未知的舊版建置會在讀取前停止；不得用這套流程繞過官方用量輸出的簽章、版本或資料格式檢查。新增舊版支援不是一般使用者操作。維護者必須使用開發用 Spike，明確授權本機讀取、收集多份私密觀察資料、審查辨識規則、建立及驗證私密設定檔，最後只匯出可攜且已審查的規則：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  export-reviewed-package-manifest `
  --profile <absolute-reviewed-private-profile> `
  --output <new-temporary-json> `
  --contract-id <stable-reviewed-contract-id> `
  --i-understand-public-manifest-export
```

匯出命令會建立新檔；若內容含電腦或私密欄位，就會停止。更換內建規則清單（manifest）前，必須審查匯出的 JSON 與隱私測試。絕對不要把來源設定檔、金鑰、原始資料、草稿、設定檔內容雜湊值或其他辨識指紋複製到專案原始碼庫或套件。

## 更新、回復舊版與更換電腦

### 使用獨立更新程式安裝或更新

確認獨立更新程式（updater）可直接存取更新清單（feed）與檔案網址後，使用者只需要保留 `AiUsageDashboard-Updater-<version>-win-x64.exe`。直接執行時，它會檢查指定管道的最新版；若有較新的更新程式，會先下載並驗證，再交由新版完成 App 安裝。舊的啟動 EXE 不必手動替換。

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

`apply-local` 是使用者明確選擇可信本機 ZIP／SHA256 的人工離線復原路徑，不讀線上 feed，也不執行一般安裝入口的條款提示，不具有同等簽章與線上反降級保障。一般首次安裝請使用 Updater 的正常入口，App 的正常啟動仍須有效接受紀錄。維持 Windows 登錄、解除安裝、maintenance 自清理與使用者資料保留；本版不建立快捷方式或工作列釘選。

`transactions/<id>/transaction.json` 以 `Flush(true)` 加上同目錄 rename 提交；`receipt-*.tmp` 不會被當成已提交證據。正常更新保留 previous，rollback 保留 staging，復原不自動清除交易、暫存或未知檔案，也不改既有 downloads／updater-cache 清理規則。

legacy 中斷紀錄缺少新增的 identity 證據時會拒絕自動復原。請先退出 App／Updater、備份整個 install root，再人工比對 current／previous／staging 的 manifest 與可信套件；確認可用版本及未知檔案皆已備份後，將該 legacy transaction 整個移至 install root 外另存。若 current 不可用，也先另存，再用可信 ZIP／sidecar 在原 install root 執行 `apply-local`。單純重跑命令無法繞過損壞 receipt。這是人工處理程序，未宣稱硬體 write cache 或真實斷電已驗收。

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

若使用離線 ZIP，請先結束 AI Usage，再刪除自行解壓的版本目錄。兩種方式都會保留使用者資料、Copilot 的 Windows Credential Manager 登入資料，以及 Claude、Codex、Grok 的受保護執行副本，供日後重新安裝使用。

永久刪除本機資料是另一項不可復原的操作。執行前，資料擁有者必須確認會失去 AI Usage 設定、快取、診斷資料、各服務的本機登入、受保護執行副本及 AGY 私密資料。

請依下列順序清理：

1. 若 AI Usage 還能正常開啟，先逐張移除所有 Copilot 卡片，等待清理警告消失。內建清理會依卡片識別碼刪除 Windows Credential Manager 中使用中（active）與待完成（pending）的登入資料，以及該卡片的本機目錄。
2. 如果 AI Usage 無法開啟，請停止一般清理流程。現行套件沒有離線批次清除 Copilot 登入資料的工具。
3. 離線清理必須由受信任工具從已確認的帳號狀態取得完整且正確的帳號 ID（account ID）。只刪除 Windows Credential Manager 中完全相符的 `AiUsageDashboard/Copilot/<account-id>` 與 `AiUsageDashboard/Copilot/Pending/<account-id>`。
4. Account ID 必須是 32 位十六進位格式，且不含連字號。不得使用萬用字元、前綴批次刪除或未核對身分的命令。無法確認精確目標時，不能宣稱已永久清除。
5. 從系統匣結束 AI Usage，確認目前使用者的 Windows 登入期間沒有 AI Usage 或其 Claude、Codex、Grok、Copilot 子程序仍在執行。
6. 由受信任工具從目前 `WindowsIdentity` 取得完整 SID，並從 Windows system directory 取得 system-volume root。不得接受手動輸入的 SID、替代磁碟或環境變數覆寫。
7. 只用上一步取得的值建立 Claude、Codex 與 Grok 三個完整路徑：`%SystemDrive%\AiUsageDashboard.<Provider>Cli.<current-user-SID>`，其中 `<Provider>` 只能替換成這三個服務名稱之一。
8. 對每個存在的目錄重新檢查：它必須位於固定磁碟，所有路徑都沒有 reparse point，擁有者是目前使用者 SID，而且 DACL 已停用繼承，只授予目前使用者、Local System (`S-1-5-18`) 與 Builtin Administrators (`S-1-5-32-544`) Full Control。任一條件不符就停止，不得遞迴刪除。
9. 只刪除目前使用者的 `%LOCALAPPDATA%\AiUsageDashboard`，以及通過上一步全部檢查的三個完整服務目錄。不得使用父目錄、萬用字元、前綴比對或跟隨 junction 擴大範圍。刪除整個 `%LOCALAPPDATA%\AiUsageDashboard` 也會刪除其中的 AGY 本機連接資料與私密校準資料；若要保留既有 AGY 連接，不得執行完整資料清除。
10. 若曾使用 AGY，再清除目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 與 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE` 環境變數。不得碰觸其他使用者的設定、受保護目錄或共用磁碟內容。

### 資料範圍與帳號移除

從浮窗移除卡片，會移除該帳號的 AI Usage 設定與用量快取。各服務的差異如下：

- Claude、Codex：不會刪除官方 CLI 的完整登入狀態。
- GitHub Copilot：會刪除該卡片在 Windows Credential Manager 中使用中與待完成的登入資料，以及該卡片的本機目錄；不影響其他 Copilot 卡片。
- Grok：會刪除該卡片的連接狀態、本機登入與 `%LOCALAPPDATA%\AiUsageDashboard\grok\<account-id>`。
- AGY：會保留這台電腦共用的 AGY 設定與私密校準資料。

清理失敗時，介面會顯示警告並在背景重試。移除卡片都不代表已從服務端登出。

本機資料主要包括：

- `%LOCALAPPDATA%\AiUsageDashboard\accounts.json*`：AI Usage 帳號設定。
- `%LOCALAPPDATA%\AiUsageDashboard\preferences.json`：介面偏好設定。
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
- `%SystemDrive%\AiUsageDashboard.GrokCli.<current-user-SID>\executables-v1`：目前 Windows 使用者共用、依檔案內容雜湊分類的 Grok 受保護執行檔版本；正常保留目前與前一個有效版本，安全清理失敗時可暫時超過兩份，移除單張卡片時不清理。
- `%LOCALAPPDATA%\AiUsageDashboard\grok-connection-pending-v1.json`：不含 token／原始帳號的 Grok 連接續做狀態。
- `%LOCALAPPDATA%\AiUsageDashboard\account-cleanup-pending-v1.json`：帳號清理失敗後供背景重試的狀態。
- `%LOCALAPPDATA%\AiUsageDashboard\portable-settings-import-transaction-v1`：設定匯入中斷後的復原紀錄，以及匯入前檔案狀態與備份。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity-connection-pending-v1.json`：AGY 連接中斷後供安全續做的狀態。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\official-print-safety-v1.json` 與 `official-print-safety-v1.json.journal`：AGY 官方用量輸出的安全狀態與中斷復原紀錄。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\setup-attempt-states-v1` 與 `setup-approval-receipts-v1`：AGY 設定嘗試狀態與核准完成紀錄。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\<account-id>\account-display-binding-v1.json`：AGY 帳號卡的本機顯示身分綁定。
- `%LOCALAPPDATA%\AiUsageDashboard\private\antigravity-statusline\account-display-v1.json`：AGY 狀態列最近回報的 `email`、時間與選填 `planTier`，只供本機顯示。
- `%LOCALAPPDATA%\AiUsageDashboard\antigravity\private`：與電腦綁定的 AGY 設定檔與金鑰。
- 目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE`：官方用量輸出流程核准的 AGY 執行檔絕對路徑。
- 目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE`：指向唯一核准、只適用於這台電腦的 AGY 設定檔。

只有在資料擁有者確認正確帳號，並接受官方 CLI 登入將會遺失後，才能手動刪除登入或設定目錄。一般移除卡片時，絕對不要遞迴刪除整個 `AiUsageDashboard` 目錄；Copilot 與 Grok 應交由內建的單卡片清理處理。

移除 AGY 卡片會保留目前使用者核准的官方執行檔、舊版設定檔與私密校準檔。若要停用 AGY，請先結束 AI Usage，再清除目前使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 與 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE`。

若不是執行上方的完整資料清除，只想移除部分舊版 AGY 資料，必須由資料擁有者
核對確切的設定檔／金鑰，只刪除那些檔案。不得遞迴刪除共用的
`antigravity\private` 目錄。

## 候選版本驗收清單

每個候選版本都必須在非開發用 Windows x64 電腦執行此清單。每項記錄為通過、失敗或不適用；不適用時要寫明原因。任何原因不明的失敗都會阻止發佈。

驗收 Windows 登入啟動時，不要同時用 Registry Editor 或其他管理工具修改同名的 `AiUsageDashboard` 項目。App 與解除安裝程式會避免彼此同時寫入，但無法保證不會與外部工具衝突。

### 套件與啟動

- [ ] 記錄候選版本、完整 commit SHA、Actions 執行網址、測試者與日期、Windows 版本、各服務已安裝的 CLI 完整版本，以及各版本是否等於本版相容性基準；不相同時先標記為未驗證，完成基本啟動與功能檢查後再記錄結果，不得直接標成不支援。
- [ ] 驗證 App ZIP、獨立更新程式與更新清單的三份相鄰 SHA-256，並確認清單內的版本、發布序號、大小、SHA-256、`sourceRevision` 與實際六個檔案一致。
- [ ] 確認根目錄說明可以開啟，且執行檔的 Product version 與候選版本紀錄一致。
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
- [ ] AGY 官方用量輸出：記錄 `1.1.11 <= version < 2.0.0` 的實際版本；保持既有 AGY 視窗開啟仍能完成連接。確認來源、Google 簽章、固定參數、四個用量週期，以及 `conversation`、`turn`、所有 `token` 都是零。
- [ ] AGY 安全拒絕：把官方執行檔設定改成無效路徑或不受信任測試檔，確認不會改用舊版設定檔。格式錯誤、多出資料組、非零 `turn`／`token` 或截斷輸出都必須拒絕，且不得保存原始 JSON。
- [ ] AGY 顯示資料：切換官方 AGY 登入後更新用量，確認畫面以狀態列的 `email` 與選填 `plan_tier` 更新顯示。
- [ ] 這些資料可保存於 `%LOCALAPPDATA%\AiUsageDashboard\private\antigravity-statusline\account-display-v1.json`，但不得匯出或改變卡片綁定。既有自訂狀態列不得覆寫。
- [ ] AGY 中斷續做：官方與舊版相容流程各測一次。按下 **完成連接** 後，在輔助程式關閉前結束或重開 AI Usage，並測試輔助程式回報結果不明。
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
- [ ] 保留一份較舊更新程式，在乾淨安裝目錄執行首次安裝，再讓更新清單提供新版更新程式與 App。確認舊版會交由已驗證的新版接手、固定啟動路徑不含版本、`previous` 保留舊版，且帳號資料仍只在 `%LOCALAPPDATA%\AiUsageDashboard`。若可攜版 AI Usage 尚未結束，首次安裝必須拒絕且不替換檔案。
- [ ] 分別在已是最新版、沒有服務工作、可正常等待工作結束，以及等待逾時時執行更新程式。可更新時只允許 App 自然退出；等待逾時須保留原 `current` 與失敗紀錄，不得強制結束程序。
- [ ] 再測更新清單／管道不符、降版、發布順序重播、SHA-256／大小錯誤、轉址到非 HTTPS、ZIP 路徑穿越或含重新解析點、同時執行更新程式、檔案鎖定及重啟失敗。
- [ ] 一般解除安裝：在 `%LOCALAPPDATA%\AiUsageDashboard` 建立測試檔，再從 Windows **已安裝的應用程式**解除安裝。確認 App 安全關閉、顯示完成通知、程式檔與 **AI Usage** 登錄項目已移除，測試檔仍保留。
- [ ] 安靜解除安裝：重新安裝候選版並建立同樣的測試檔，執行登錄項目中的 `QuietUninstallString`。確認沒有完成通知，其餘程式、登錄與使用者資料結果和一般解除安裝相同。
- [ ] 確認帳號設定從 v6 升級到目前的 v7。先用上一個已知正常的 v6 套件建立多個服務、暱稱與 Claude 風險同意，再用 v7 候選版本開啟。帳號 ID、順序、啟用狀態、暱稱與同意狀態都必須保留；新增的 **顯示組織**／**顯示 workspace** 預設為關閉。
- [ ] 在 v7 重新連接至少一張 Claude 與 Codex 卡片，分別選擇組織與 workspace，再開啟上述顯示選項。儲存並重新啟動後，確認綁定、顯示選項與對應資訊都保留。完全結束後，再用同一個 v6 套件開啟已寫成 v7 的主要與備份設定；舊版必須回報較新格式、停用編輯與儲存，且不得改變檔案。回到 v7 後，資料仍須完整。這是預期的安全拒絕，不是成功回復舊版。
- [ ] 確認診斷與 ZIP 不含密碼、token、Copilot Credential Manager 資料、原始 Grok 登入／帳號／連接資料、Grok 專用目錄、原始 AGY 終端或 JSON、帳號設定、用量快取、PDB 或開發用 Spike 工具。

## 支援與復原

- Claude／Codex CLI 遺失或不相容：先記錄偵測版本與完整原始錯誤。版本差異只是可能原因；若官方來源、簽章與必要功能都合格，先重新啟動 AI Usage，讓程式自行建立或更新受保護副本，再對照候選版試用紀錄。不得手動複製受保護執行檔或放寬目錄權限；既有帳號識別會保留。
- GitHub Copilot 要求重新連接：只重新連接受影響的卡片。若移除卡片後仍顯示清理警告，保持 AI Usage 開啟以完成背景重試；不要手動批次刪除 Windows Credential Manager 項目。
- Grok CLI 遺失或來源不受信任：只能從 xAI 官方來源安裝到 `%USERPROFILE%\.grok\bin\grok.exe`。重新啟動 AI Usage，讓程式自行更新受保護副本，並確認 `X.AI LLC` 簽章與路徑檢查通過。`1.0.3` 是相容性基準；不得只因版本不同而要求降版，也不得用 `PATH`、手動複製或替代執行檔繞過檢查。
- 服務暫時失敗：稍後重試；畫面會保留上次正常的用量資料。
- 明確的登入驗證失敗：只重新連接受影響的帳號。
- Grok 連接中斷：先重新啟動 AI Usage。若待處理紀錄已保存 `LoginCompleted`，程式會用新取得的帳號資料驗證後接續；否則卡片會要求重新連接。不要手動複製、編輯或刪除連接／待處理檔案。
- Grok 帳號衝突：同一個實際帳號不能綁定兩張卡片。確認登入終端使用預定帳號，再移除錯誤或重複卡片並重新連接；不得改寫公開連接 ID 或私密指紋。
- Grok 清理警告：保持 AI Usage 開啟以完成背景重試，確認警告消失且只有目標 `%LOCALAPPDATA%\AiUsageDashboard\grok\<account-id>` 被清除。不要遞迴刪除整個 `grok` 目錄，以免破壞其他帳號。
- AGY 帳號顯示不同：先讓 AI Usage 自動修復。若卡片仍要求操作，使用 **重新連接 Antigravity 帳號** 或 **連接 Antigravity 帳號**；不必關閉既有 AGY 視窗。若只在 AGY 內切換登入，先更新一次用量以取得新的 status-line email。這只更新顯示，不會改變這台電腦的卡片綁定；不要沿用其他電腦的設定。
- AGY 來源拒絕：確認核准設定指向官方安裝的一般 `.exe`，版本在 `1.1.11 <= version < 2.0.0`。不得改走舊版相容流程。執行 `agy --version`，並回報畫面上的完整安全診斷區塊。
- AGY 用量暫時失敗：程序逾時、取消、一般錯誤、非零結束碼或輸出格式錯誤時，程式會在背景以 1、2、4、8、最多 15 分鐘間隔重試。期間保留已驗證的 email 與上次正常用量，重開程式後仍會接續。
- AGY 用量安全問題：只有舊格式狀態、無法確認子程序已結束、偵測到用量活動，或安全狀態損壞／無法保存時才會停止，並顯示 **重新檢查 Antigravity 用量**。成功後才清除標記；不得手動刪除標記來繞過安全檢查。
- 診斷紀錄不得包含登入資料、原始 Grok OAuth／ACP／帳號／連接內容或 AGY 終端內容。支援時只分享必要且已遮蔽敏感資訊的最少行數。
- 支援回報必須包含套件 ZIP 檔名、Windows 檔案內容中執行檔的 **Product version**、Windows 版本、受影響服務的 CLI 版本與畫面上可見的錯誤。絕對不要要求對方提供整個本機應用程式資料目錄。

交付團隊前，必須在非開發用 Windows 電腦確認 AI Usage 已完全結束後再啟動，並記錄 Claude、Codex、GitHub Copilot、Grok 與 AGY 的試用結果。未實測的帳號組合必須繼續標示為未驗證。
