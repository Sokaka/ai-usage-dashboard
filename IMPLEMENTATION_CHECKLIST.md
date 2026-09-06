# 實作檢查清單

更新日期：2026-09-05

讀者：負責 AI Usage 開發、測試與交付的人員。一般使用者請閱讀 [`使用說明.md`](使用說明.md)。

此文件記錄 `ai-usage-dashboard` 的實際實作進度。只有具備程式碼與驗證證據的項目才標記為完成；尚未實測的項目維持未完成。

各項安全機制與資料格式請查閱 [`docs/TECHNICAL_OVERVIEW.md`](docs/TECHNICAL_OVERVIEW.md)。目前可下載版本則以 [`README.md`](README.md#目前版本) 為準。

## 目前狀態

- Windows WPF 浮窗是唯一主介面，可完成用量查看、帳號管理、排序、連接與錯誤恢復。
- Claude、Codex、GitHub Copilot 與 Grok 已支援多帳號；Copilot 限不同的 `github.com` 帳號，AGY 目前只使用這位 Windows 使用者的一組本機設定。
- 帳號設定與用量快取只保存在目前 Windows 使用者的本機範圍。Copilot token 依卡片存入 Windows Credential Manager；所有服務的 token 都不寫入帳號設定、快取、匯出的設定檔或診斷紀錄。
- 2026-08-25 版（版本尾碼 `.21`）是最後一個在兩台既有試用機完成相同基本測試的候選版。它不是目前最新原始碼的產物，也不是穩定版。
- 尚未完成的功能與驗證以本文未勾選的 `[ ]` 項目為準。交付前還要取得 Anthropic 分發許可、用更多真實帳號測試多帳號的失敗與復原流程、量測長時間資源使用、在沒有 SDK 與舊資料的非開發用電腦首次啟動，並完成人工介面與無障礙檢查。

## 各服務現況

| 服務 | 用量從哪裡讀取 | 已確認 | 接下來要做 |
| --- | --- | --- | --- |
| Claude | 每張帳號卡執行 Claude Code `/usage`；舊版 status line 資料只作備用 | 在隔離的 Claude.ai 測試環境完成背景查詢與介面基本測試；功能仍屬實驗性 | 等待上游提供穩定格式或正式介面，並取得 Anthropic 分發核准 |
| Codex | 官方 `codex app-server` 的 `account/read`、`account/rateLimits/read` | 在測試用的本機登入環境完成登入、多個額度區間、介面及首次啟動測試；功能仍屬實驗性 | 確認資料格式相容、依服務要求暫停重試，並測試更多帳號切換情境 |
| Grok | 官方 Grok Build CLI 的 billing 與 auth-info | 多帳號功能已實作，仍屬實驗性；自動重試已驗證 | 以第二個真實帳號完成完整測試與資源量測 |
| GitHub Copilot | `GitHub.Copilot.SDK` 1.0.11；隨附 CLI 1.0.79 | 已用 Pro 與 Business 兩個不同帳號完成介面測試 | 補測重複帳號、`401` 後重新連接，以及移除單張卡片等失敗情境 |
| Antigravity／AGY | 官方 CLI `/usage` 與 status-line 顯示資料；舊版 ConPTY 相容路徑 | 同一位 Windows 使用者目前只能建立一張卡片；背景查詢仍屬實驗性 | 持續檢查資料來源與零 token，等待可辨認不同帳號的可靠資料 |

## 1. 浮窗與應用程式基礎功能

- [x] 建立 Windows WPF 浮窗，作為查看用量、帳號管理與恢復操作的唯一主介面；不再維護另一套完整 Dashboard 版面。
- [x] 支援新增、編輯、啟用、停用與移除多張帳號卡片。
- [x] 以版本化 JSON 在本機保存帳號設定，並以原子寫入避免半成品；保留上次正常備份與 account ID，供自動復原。
- [x] 同一個 Windows 使用者一次只會執行一份程式；再次啟動會帶回既有浮窗。系統匣可顯示、隱藏、置頂與結束程式，雙擊也能帶回浮窗。
- [x] 保存浮窗顯示、收合、置頂、螢幕與角落偏好。舊版 `Dashboard`、`Widget`、`DashboardAndWidget` 啟動值會顯示浮窗，`Tray` 則維持隱藏；舊版排序仍會保留，舊程式也不會覆寫較新版設定。
- [x] 提供經典藍、曜石黑、柔霧灰與櫻花粉四種可切換主題，保留各服務的識別色與警告／危險色。主題會隨格式 4 的設定檔匯入或匯出；匯入更舊格式時，保留目標電腦目前的主題。
- [x] 將新增 Claude／Codex／GitHub Copilot／Grok 帳號與官方連接流程串成單一步驟；CLI 或本機用量來源故障時保留既有帳號識別，不誤導使用者重新登入。
- [x] 浮窗支援完整帳號管理、排序、連接與復原。展開時預設使用螢幕可用高度，也可依卡片內容自動縮短並維持原本停靠角落；鍵盤、螢幕閱讀器動態提示、ProgressBar 語意與 Windows 高對比模式也已補齊。
- [x] 由 `IUsageProvider` 登記各服務，並明確處理尚未設定與不支援的情況。
- [x] 建立每張卡片各自的快取、避免重複查詢、最短更新間隔，以及查詢失敗時保留上次成功資料的機制。
- [x] 用量沒有改變時不重寫快取，也不重建相同的介面資料。快取會在背景寫完後一次替換，避免留下半成品。
- [x] 串接啟動、手動與背景刷新，並避免重複建立等待中的計時器工作。

## 2. 背景查詢與失敗重試

- [x] Claude 會由每張帳號卡定期查詢用量，不必等到使用者與 Claude 互動後才取得資料。
- [x] Claude 帳號只提供浮窗登入入口，不要求使用者複製指令或手動開啟 PowerShell；舊版 status line 資料只保留相容讀取。
- [x] 每張 Claude 帳號卡最多每分鐘查詢一次，並保留快取；單次命令超過 30 秒便停止等待。
- [x] 可重試錯誤統一等待 1、2、4、8、15 分鐘後重試；成功或資料明確失效後重設，並保留上次確認的帳號識別。
- [ ] 將各服務的限流資訊（例如 `Retry-After`）傳入統一的暫停重試機制。
- [x] 每次用量結果會明確區分可用、尚未設定、不支援、舊資料與錯誤。
- [x] 登入失效、服務暫時故障與本機工具需要重新設定會顯示不同提示，不會把所有錯誤都叫使用者重新登入。
- [ ] 確保用量查詢不建立模型 turn、不發送 prompt，也不消耗推論額度。目前只有受控帳號的試用結果與回傳後零 turn／token／cost 檢查；檢查發生在命令執行後，無法事前保證或撤回已產生的用量與費用。
- [x] 登入資料由各服務的官方流程或作業系統認證儲存區管理；AI Usage 不會把 token 寫入、記錄或匯出。

## 3. Claude

- [x] 每張帳號卡片使用獨立 `CLAUDE_CONFIG_DIR`，由 GUI 啟動官方登入並確認實際帳號。
- [x] 舊版 `ClaudeCapture` helper 僅保留既有設定相容；新版不再提供 setup 或手動 CLI 啟動入口。
- [x] Claude Code 通過本機固定磁碟、無 reparse point、大小上限、Windows 信任與 `Anthropic, PBC` 簽章檢查後，依 SHA-256 複製到只開放目前使用者與系統管理帳號的資料夾。AI Usage 執行這份受保護副本，不直接執行原始安裝檔。
- [x] Claude 操作結束前持續鎖定副本。無法確認整個子程序樹已結束時，相關鎖定保留到 App 重開；不得用父程序已結束代替完整確認。
- [x] Claude 副本正常只保留目前與前一個有效版本。安全檢查、檔案鎖或刪除失敗時先保留，後續再重試清理。
- [ ] 確認 Claude Pro／Max／Team 是否提供有版本號、可供程式穩定讀取的帳號用量 API；目前 `/usage` 是官方內建命令，但結果仍是給人閱讀的文字。
- [ ] 正式分發前取得 Anthropic 授權確認。目前只允許使用者自行登入未修改的官方 Claude Code；AI Usage 不代登入、不保存 Claude.ai token，也不代付或轉售用量。
- [x] 實作帳號層級 `/usage` 查詢。先確認 Claude Code `>=2.1.169`、Claude.ai Pro／Max／Team 登入及必要安全參數，再於回傳後強制檢查 turn、所有 token 與 cost 都是零。
- [x] `2.1.169` 只作比較基準；更高的官方三段式版本不會只因版號不同而拒絕。只有與版本可能相關的失敗才顯示版本提示。
- [x] 將 Claude `/usage` 風險同意保存於帳號格式 v4；新卡片與舊格式預設未同意。選擇同意時必須先成功保存才會登入；拒絕時不啟動登入或用量查詢。
- [x] 在隔離的 Claude.ai 測試環境，以正式背景查詢流程完成基本測試。程式可讀取 session 與 weekly all-models quota，且每次結果的 turn、input token、output token、cache token 與 cost 都是零。
- [ ] 新增並驗證額外的 Claude 帳號情境。

## 4. Codex

- [x] 建立 `codex app-server` JSONL client，完成 `initialize`／`initialized` handshake。
- [x] 每張帳號卡使用獨立 `CODEX_HOME` 與 `CODEX_SQLITE_HOME`，隔離 auth、config 與 state。
- [x] Codex CLI 通過本機固定磁碟、無 reparse point、大小上限、Windows 信任與 `OpenAI OpCo, LLC` 簽章檢查後，依 SHA-256 建立目前使用者專用的受保護副本。
- [x] Codex 版本檢查、登入或用量查詢結束前持續鎖定副本。無法確認整個子程序樹已結束時，所有新 Codex 操作停止到 App 重開。
- [x] Codex 版本檢查只在安全路徑建立 `validation-v1\probe-<guid>`，確認程序已結束後才清理。副本正常只保留目前與前一個有效版本；安全清理失敗時先保留並稍後重試。
- [x] 實作官方 `account/login/start` browser flow 與 `account/login/completed` 等待流程；應用程式不讀取 token，背景刷新不觸發登入。
- [x] 在測試用的本機登入環境完成瀏覽器登入與介面基本測試。
- [x] 從系統匣結束程式再重開後，卡片專用目錄內的登入資料仍可使用，帳號會自動恢復為可用狀態。
- [ ] 使用額外的授權 ChatGPT 測試帳號，完成帳號切換與重新登入測試。
- [x] 使用 `account/read` 驗證 ChatGPT account type 與 plan。
- [x] 定期呼叫 `account/rateLimits/read`，映射 `usedPercent`、window duration 與 reset time。
- [ ] 視 UI 需求使用 `account/usage/read` 顯示 token activity；不得與 rate-limit quota 混用。
- [x] 可讀取 `rateLimitsByLimitId` 內的多個額度區間，不假設永遠只有一個 `codex` 區間。
- [x] 處理 app-server 啟動失敗、process exit、timeout、取消與明確重新登入操作。
- [x] CLI 缺失、登入失敗、相容性問題、暫時錯誤與未知錯誤都有對應的安裝、連接或重試動作。官方三段式版本不設固定允許清單；`0.144.1` 只作比較基準。
- [x] 同一帳號的登入與背景查詢不會重疊；不同帳號仍可同時執行。
- [ ] Codex 回傳 `429`／`Retry-After` 時，依服務指定時間暫停查詢。其他可重試錯誤已由共用刷新流程逐步拉長等待時間。
- [x] 加入 JSONL 解析、服務資料對應、多帳號目錄隔離、登入與子程序管理測試。
- [x] 在測試用的本機登入環境，以正式背景查詢流程完成基本測試；查詢沒有建立 thread 或 turn。

## 5. Grok

- [x] Grok 只接受 `%USERPROFILE%\.grok\bin\grok.exe`，並檢查本機固定磁碟、無 reparse point、大小上限、Windows 信任及 `X.AI LLC` 簽章，再依 SHA-256 建立目前使用者專用的受保護副本。
- [x] Grok 版本檢查、登入與用量查詢結束前持續鎖定副本。無法確認整個子程序樹已結束時，所有新 Grok 操作停止到 App 重開。
- [x] Grok 副本正常只保留目前與前一個有效版本；安全清理失敗時先保留並稍後重試。`1.0.3` 只作比較基準，其他可信任版本仍以實際 ACP 相容性決定是否可用。
- [x] 每張卡片使用獨立的 `GROK_HOME` 與空白工作目錄；同一帳號的操作不會重疊。只有使用者明確連接時才執行 `grok --no-auto-update login --oauth`，背景更新不會自行登入。
- [x] 直接執行 `grok --no-auto-update agent stdio` 讀取 ACP billing 與 auth-info；限制輸出大小、回應數量與資料格式，未知或矛盾資料會拒絕使用。
- [x] 每張卡以不同 salt、SHA-256 帳號指紋與隨機公開 ID 綁定；同一實際帳號不能綁定兩張卡。原始帳號、token 與 email 不寫入 `accounts.json`、快取或匯出的設定檔。
- [x] Grok 連接會把登入開始、登入完成與綁定完成分段寫入磁碟。只有登入完成已安全保存時，重開程式才會重新驗證帳號並接續；連接、移除與匯入不會互相誤寫或誤刪。
- [x] 移除 Grok 卡片時會在背景清除連接資料、待處理工作與該卡專用目錄；失敗時保存重試狀態，不在介面執行緒遞迴刪除。
- [x] 加入執行檔來源、OAuth、ACP 格式、多帳號隔離、重複帳號、啟動復原、程序管理、清理與設定匯入／匯出測試。
- [x] 以與 PR #5 合併提交相同的原始碼重新建置並重啟，完成 Grok 真機基本測試。暫時檔案鎖解除後，排程器可自動重試並再次更新快取。
- [ ] 使用第二個不同的真實 Grok 帳號，測試兩張卡片分別更新、程式重啟、拒絕重複帳號，以及移除一張後另一張仍可使用。
- [ ] 以代表性的多帳號資料量，量測每張卡片專用目錄增加的磁碟用量、實際刪除時間，以及長時間執行時的 CPU、記憶體與程序數。

## 6. GitHub Copilot

- [x] 使用 `GitHub.Copilot.SDK` 1.0.11 的 Experimental `account.getQuota` 與隨附的 Copilot CLI 1.0.79；不以 organization Billing REST API 代替一般帳號用量。
- [x] 只支援不同的 `github.com` 帳號。以 host 與 GitHub `node_id` 摘要建立不受登入名稱變更影響的識別，同一帳號不能重複綁定；同帳號的多個 organization／enterprise／subscription 不拆成多張卡片。
- [x] 互動連接使用官方 `login --web-flow`，並在登入後驗證唯一帳號。相關程序都受 Job Object 管理；啟動失敗、取消或無法確認子程序已結束時，鎖定持續保留到確認安全為止。
- [x] 連接用的官方 CLI 登入資料可能由該 CLI 共用。AI Usage 只在確認唯一且正確的帳號後，才把 token 移到該卡片專用的 Windows Credential Manager 項目；空白、多筆或帳號不符時不寫入。
- [x] 清除暫用登入目錄時，只允許 Windows 建立的精確 `INetCache\Content.IE5` → sibling `IE` junction，且只刪除連結本身。其他 reparse point 或越界目標一律拒絕。
- [x] 背景更新使用每張卡片自己的登入資料與隔離目錄，不讀取共用 CLI 登入，也不借用其他卡片或目前環境的登入資料。
- [x] 每次查詢用量前，以 GitHub `GET /user` 重新確認帳號。`401` 要求重新連接；`403`、`429`、回應過大、格式無效或帳號改變都會拒絕使用資料。
- [x] 只有 SDK 明確回報 `token_based_billing` 時才顯示 AI Credits。資料不足時顯示 `Premium usage`，不依方案名稱猜測。
- [x] 個人額度顯示 AI Credits 已用量與上限。Business／Enterprise 若由共用額度與預算控制，不宣稱為無上限或固定個人額度。付費方案的 code completions 另行顯示。
- [x] Token 不寫入 `accounts.json`、用量快取、匯出的設定檔或診斷。匯入只有在新設定確認提交後，才清除舊登入資料，範圍包含不再保留的卡片，以及匯入後保留相同 account ID 的 Copilot 卡片；保留卡片也須重新連接。中斷復原會先保留原資料，避免誤刪。
- [x] 移除 Copilot 卡片只清除該 account ID 的目前／暫存登入資料、快取與專用目錄。失敗時由背景紀錄重試，不影響其他卡片。
- [x] 建置與封裝時，將官方 CLI 與 SDK 的第三方授權聲明放入 `third-party-notices`。Copilot 專項測試、完整 Release 測試、目前原始碼的 Release 建置，以及內含 .NET 的 `win-x64` 封裝均已通過。
- [ ] 補測拒絕重複帳號、`401` 後重新連接，以及移除一張卡片後另一張仍可使用。

## 7. Antigravity／AGY

- [x] AGY 1.1.11 起使用官方 print 功能，固定執行 `agy -p /usage --output-format stream-json`，讀取四個用量週期。
- [x] 每次讀取前重新確認執行檔完整路徑、Windows 信任、Google LLC 簽章、無 reparse point、檔案未改變，以及版本在 `1.1.11 <= version < 2.0.0`。
- [x] 解析器只接受兩組、每組兩個用量週期，並要求執行成功、conversation／turn 與所有 token 都是零。原始 JSON 只在記憶體中處理，不寫入快取或診斷。
- [x] `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 有值時只走官方流程；路徑、來源或資料驗證失敗時停止，不得改走舊版相容流程。
- [x] AGY 1.1.7／1.1.9 只保留已審查 SHA-256 與私密 R1 profile 的 ConPTY 相容路徑；每次仍會重新驗證執行檔、畫面與設定。
- [x] 只能新增一個 Antigravity 帳號，避免把目前 Windows 使用者同一份全域 profile 重複顯示成多個帳號。
- [x] 舊設定若有多張啟用中的 Antigravity 卡片，只允許第一張查詢；其餘卡片保留但提示停用或移除。
- [x] 優先使用目前使用者核准的官方執行檔或舊版 profile；只有使用者設定缺少時才使用程序環境的備用值。新的官方連接不要求關閉既有 AGY 視窗。
- [x] 正式 App 不攜帶開發用 Spike 執行檔。
- [x] `Antigravity.Setup` 會預覽目前登入與四個用量週期，人工確認後才保存設定。舊版相容流程則保存私密 profile 路徑。
- [x] 回傳後仍強制檢查 turn 與 token 都是零；上游說明不代表 Google 對本工具背書。
- [x] 官方用量資料不含 email。私有 status-line helper 另讀取 email 與選填 `plan_tier`，保存於 `%LOCALAPPDATA%\AiUsageDashboard\private\antigravity-statusline\account-display-v1.json` 供本機顯示；兩者不作為多帳號安全識別，也不會匯出。
- [ ] 等待可驗證的每帳號識別或 profile 後，再實作並驗證多帳號隔離；目前仍維持單一卡片。
- [x] 不分析或模仿未公開的額度 API，也不把 Google OAuth 登入資料交給非官方中介服務。

## 8. 品質與交付

- [ ] 逐步拆分仍集中的 App 組裝、生命週期與流程控制責任。Grok 啟動復原已抽出；其他部分維持小步調整，不一次大改。
- [x] 加入背景更新、Claude 解析、設定、服務與資料讀取測試。
- [x] 驗證服務自行拋出 `OperationCanceledException` 時的快取與暫停重試行為。
- [x] 完成 WPF 建置與程式啟動基本測試。
- [ ] 在標準安裝版完成 Windows 登入啟動驗收：實際登出／登入後依浮窗或 Tray 偏好啟動，且不搶焦點。
- [ ] 在 Windows **啟動應用程式**停用後不得啟動，重新啟用後應恢復。已有執行中程式時，`--startup` 應安靜結束且不喚回浮窗。
- [ ] 更新後須保留已登錄、未登錄與被 Windows 停用的狀態。解除安裝只清除完全相符的 `HKCU Run` 值；衝突值須保留並顯示警告。
- [x] 完成 Claude PowerShell／Git Bash statusline 基本測試。
- [x] Claude 背景查詢完成後，重新執行整個 solution 建置、完整測試、正式查詢流程與新版介面基本測試。
- [x] Codex 背景查詢與登入流程完成後，重新執行整個 solution 建置、完整測試、受控本機登入、介面與首次啟動測試。
- [x] 加入 AGY 官方功能檢查、嚴格的 stream-json 解析、程序啟動、設定流程、資料來源優先順序及來源信任測試；Release 建置與相關測試已通過。
- [x] 加入 Grok CLI 來源、ACP 通訊、卡片專用目錄、帳號綁定、啟動復原、清理、多帳號與設定匯入／匯出測試；提交前 Release 建置為 0 warnings／0 errors，完整測試 2,452 項通過。
- [ ] 完成人工 UI 驗證：系統匣、顯示／隱藏與雙擊、舊偏好相容、帳號編輯、服務連接、對話框前景順序，以及匯入／還原與排隊中的更新狀態。
- [ ] 完成可存取性驗證：鍵盤、螢幕閱讀器、一般主題與 Windows High Contrast。
- [x] 建立 Windows CI workflow，在乾淨 checkout 上以 `global.json` 固定 SDK，自動執行 restore、Release build、完整測試與內部套件檢查。
- [x] 兩個 checkout step 均設定 `persist-credentials: false`，不將 GitHub credential 留給後續步驟。
- [x] 建立只接受 `main`、完整 commit SHA 與人工候選確認的候選版 workflow。候選版本綁定來源 SHA、run ID 與 attempt，不從 PR 合併暫存版本上傳套件。
- [x] 建立有版本、內含 .NET 的 `win-x64` 內部套件規則、禁止檔案清單與 SHA-256。
- [x] 補齊內部安裝、登入、Grok 多帳號、AGY 單機核准、資料清除範圍、隱私與問題排除文件。
- [x] 升級測試工具以移除已知 vulnerable transitive packages，並以 `Directory.Build.props` 對所有專案啟用 transitive NuGet audit；moderate 以上 advisory 視為 restore error。
- [ ] 在沒有開發用 SDK、專案原始碼或既有 AI Usage 資料的乾淨 Windows x64 電腦，完成所有服務的首次啟動、重啟、更新與回復舊版測試。
- [ ] 在下一個含 Copilot 的候選套件完成整組人工驗收：Claude、Codex、Grok 要測試登入切換及帳號隔離；GitHub Copilot 要測試兩個不同帳號、拒絕重複帳號、`401` 後重新連接、逐張更新，以及移除一張後另一張仍可用；AGY 要重新確認官方 print 連接與資料顯示。
- [ ] 公開候選最後決策保留 Claude／AGY 條款適用不確定性、使用者查詢風險同意及 Microsoft publisher 義務；不另設個別廠商回函 gate。
- [ ] 在 .NET 8 於 2026-11-10 結束支援前完成受支援 runtime 遷移與封裝回歸；到期後不得繼續散發目前的 .NET 8 package。
- [ ] 將只用於測試、目前無已知漏洞但官方已列為舊版的 xUnit v2 遷移到 xUnit v3。此套件不會進入正式 App 安裝包，因此不阻擋目前的內部試用。

## 官方參考

- [Claude Code statusline](https://code.claude.com/docs/en/statusline)
- [Claude Code `/usage`](https://code.claude.com/docs/en/costs#using-the-usage-command)
- [Claude Code CLI reference](https://code.claude.com/docs/en/cli-usage)
- [Claude Code legal、產品內執行與 credential 使用條件](https://code.claude.com/docs/en/legal-and-compliance)
- [Codex App Server](https://learn.chatgpt.com/docs/app-server)
- [Codex `CODEX_HOME`](https://learn.chatgpt.com/docs/config-file/environment-variables#core-locations)
- [GitHub Copilot SDK usage and billing](https://docs.github.com/en/copilot/how-tos/copilot-sdk/features/usage-and-billing)
- [GitHub Copilot individual usage-based billing](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-individuals)
- [GitHub Copilot organization／enterprise usage-based billing](https://docs.github.com/en/copilot/concepts/billing/usage-based-billing-for-organizations-and-enterprises)
- [GitHub Copilot seat assignment](https://docs.github.com/en/copilot/reference/copilot-billing/seat-assignment)
- [GitHub Copilot CLI 第三方授權聲明](third-party-notices/GitHub-Copilot-CLI-LICENSE.md)
- [GitHub Copilot SDK 第三方授權聲明](third-party-notices/GitHub-Copilot-SDK-LICENSE.md)
- [Antigravity CLI `/usage`](https://antigravity.google/docs/cli/commands/usage)
- [Antigravity CLI 1.1.11 發行說明](https://antigravity.google/changelog)

## 首次公開候選驗收

以下尚未完成實機驗收；上方既有功能的歷史測試不代表本版已通過。

- [ ] 精確第三方交付、single-file 離線匯出與缺件 gate。
- [ ] 首次接受／拒絕／條款變更、portable／Setup／Updater／helper／委派與非互動入口。
- [ ] 五 provider 的登入、帳號隔離、背景用量、冷卻、失敗及資料保留。
- [ ] 舊 internal→stable、有值／null 序號、至少一次後續更新與 Updater 自更新。
- [ ] maintenance bytes、feed、channel、version、Windows registry 及非 canonical 安裝。
- [ ] 簽章與成品竄改拒絕、金鑰更替／遺失／洩漏演練、首次 Windows 提示。
- [ ] 每個持久化／rename 邊界終止、復原再中斷、rollback 失敗、磁碟不足與未知檔案。
- [ ] 解除安裝、maintenance 自清理與使用者資料保留。
- [ ] 完整 source／新歷史／logs/artifacts／圖片／binaries 公開面掃描。
- [ ] 相同 source/run/attempt 六件凍結於同一 private 候選 Release。
- [ ] 公開授權後，轉正同一 Release，匿名驗簽／hash／size 與正式安裝更新 smoke。
