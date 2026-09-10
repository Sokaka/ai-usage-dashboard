# AGY 串接維護工具

> 本文件供 AGY 串接的維護與研究使用，不是一般使用者指南。一般操作請參閱[使用說明](../../使用說明.md)。

## 簡介

這個命令列工具用來維護 AGY 串接。一般連接由 AI Usage 內附的 Setup 處理；本工具主要供維護者驗證 AGY 官方 `/usage` JSON 輸出，也能重新校準舊版 ConPTY 終端畫面讀取方式。

## 環境需求

- Windows 10 版本 1809 以上。舊版 ConPTY 維護功能明確依賴此版本；新功能仍須依專案支援範圍測試。
- 專案指定的 .NET SDK 8.0.425。
- 從專案根目錄執行下方命令。
- 需要實際啟動 AGY 的命令，必須先取得測試帳號持有人的明確同意。

## 使用說明

一般使用者不需要執行本文件的命令。若要驗證目前正式使用的方式，請看[官方 `/usage` JSON 輸出流程](#官方-usage-json-輸出流程print-mode)。

更新舊版相容資料時，下列 R1 v3 草稿與核准流程只接受 120 欄 × 50 列，請照此尺寸執行：

1. [建立私有校準資料](#擷取私有-r1-校準資料)：使用已完成 prompt 校準的 120x50 私有 R0 profile，擷取至少兩筆額度或重置狀態不同的資料。
2. [人工審查與核准](#離線-r1-人工審查與核准)：產生待審草稿（draft），逐欄核對畫面，核准內容雜湊值（fingerprint），再產生畫面欄位規則（layout）與 R1 私有設定檔（profile）。
3. [執行 R1 v3 實機驗證](#r1-v3-實機驗證)：另行取得同意後，確認已核准的私有設定檔能安全讀取完整頁面。
4. [匯出套件允許資料](#匯出已審查的套件允許資料manifest)：擷取驗證通過後，匯出候選檔，再檢查內容並執行隱私測試。

若 AGY 執行檔已更換，但路徑、檔案版本（file version）、產品版本（product version）與簽章仍與原私有設定檔相同，請先[更新已核准的執行檔雜湊值](#更新已核准的執行檔雜湊值)，再從第 1 步開始。只要執行檔、畫面配置或設定檔內容雜湊值不同，就必須重新校準，不可沿用另一台電腦或另一版的結果。

## 注意事項

- 所有會啟動 AGY 的命令都必須先取得同意。`promote-executable-fingerprint` 與 `export-reviewed-package-manifest` 只執行固定的 `--version` 查詢；其他帶有 `--i-understand-*` 的命令可能真的擷取終端畫面或用量資料。
- 私有設定檔（profile）、驗證金鑰（key）、終端畫面資料包（screen bundle）、帳號、額度與本機路徑不得加入 Git、套件或審查附件。
- 任一來源、簽章、版本、輸出格式或清理結果無法確認時，工具會停止並拒絕採用結果。
- 下文保留命令、欄位與程式碼中的英文識別名稱。`fingerprint` 是用來確認內容是否相同的雜湊值；程式欄位中的 `gate` 表示該項檢查結果。

R0／R1 是舊版 ConPTY 終端讀取方式使用的畫面驗證格式，不是 AI Usage 的產品版本。R0 比對整個畫面；R1 依人工審查過的欄位結構驗證畫面內容。

新連接使用目前 Windows 使用者的 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 設定，指向已核准的 AGY 執行檔。只有既有 1.1.7／1.1.9 連接，才使用 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE` 指向已核准的 R1 私有設定檔。

兩項設定都優先讀取目前使用者的值；只有該值不存在時，才讀取目前程序的環境變數。

## 目前支援範圍

- AI Usage 預設使用 AGY 官方 `/usage` JSON 輸出。程式內仍把這個讀取元件標成 `official-experimental`。只有完全沒有設定官方執行檔路徑時，程式才可能使用既有的舊版 ConPTY 私有設定檔。只要官方路徑已有值，即使路徑無效、執行檔來源檢查失敗或用量擷取失敗，程式都會停止，不會自動改走舊版方式。
- 封裝內的 `AiUsageDashboard.Antigravity.Setup` 會先找出符合官方 `/usage` 執行檔檢查規則的版本。核准後只保存執行檔路徑。只有 SHA-256 完全符合內建紀錄的 1.1.7／1.1.9 執行檔，才會使用已審查的套件允許資料（manifest）、私有 key／profile 與 ConPTY 校準流程。
- 官方 `/usage` 輸出不含 email。正式用量讀取元件只回傳固定識別 `agy.local-session.v1` 與四個統一格式的額度區間，用來代表這台電腦目前的 AGY 登入來源。另一個正式帳號顯示工具只從官方 status-line JSON 取得 email 與選填欄位 `plan_tier`，供介面顯示。兩種資料都不會改變帳號綁定。
- 官方命令的標準輸出位元組只在記憶體中由嚴格解析器處理，完成後立即清除。原始 stream-json、標準錯誤輸出、執行檔本機路徑、例外內容及每次執行產生的值，都不會寫入介面、快取、診斷紀錄、Git 專案或套件。
- 下文的 R0／R1 畫面擷取、套件允許資料（manifest）、命令提示畫面（prompt）、設定檔內容雜湊值及私有 HMAC 流程，全都只供舊版 ConPTY 維護。R0 要求整個畫面的雜湊值完全相同，仍不得用於正式可用判定（`NO-GO`）。R1 依獨立的欄位規格判讀畫面，只為既有完全相符的雜湊值保留相容性。
- 正式版使用的舊版讀取元件只內嵌已審查執行檔的不含路徑資訊、prompt 結構雜湊、用量欄位格式規則（grammar／schema）及整頁雜湊值（page fingerprint）。每個 profile、同目錄 `.key` 與 screen bundle 都必須放在 Git 已忽略的 `work/` 或其他未追蹤位置；HMAC key 的原始位元組絕不可進入 profile JSON、報告、commit 或共用檔案。
- 自動測試只使用程式產生的 stream-json／終端資料、假元件（fake）及專案內的 console 測試程式（fixture）。測試通過只能證明程式符合已固定的輸入輸出規則，不能取代在非開發用 Windows 上實際啟動 AGY 的基本測試。

[AGY 1.1.11 的官方發行說明](https://antigravity.google/changelog)表示，官方 `/usage` 輸出命令不會建立 conversation／agent turn，也不會消耗 quota。這只是 AGY 對該命令的行為說明，不代表 Google 對 AI Usage 背書。正式讀取元件仍會檢查 turn 與 token 必須全為零；任一值不符就拒絕結果。

任何舊版 ConPTY 實機試驗仍須另行取得充分知情同意。收到政策警告後不得執行；執行檔、畫面規則、設定檔內容與校準時不同，或程序清理失敗時，也會停止。

## 官方 `/usage` JSON 輸出流程（print mode）

接受的版本號必須是三段整數且寫法固定，範圍為 `1.1.11 <= version < 2.0.0`。不接受 `v` 前綴、版本附加資訊、預發行標記、前導零、缺少段數或 2.x。Setup 與每次擷取都會重新執行相同的來源與檔案身分檢查：

- 執行檔必須使用完整絕對路徑，位於本機允許的目錄，而且是一般 `.exe`；執行檔與所有父路徑都不能是 reparse point；
- WinVerifyTrust 驗證必須成功，簽署者名稱（signer subject）與憑證指紋（thumbprint）必須完全符合程式內固定的 Google LLC 簽署者；
- 檢查執行檔能力（capability probe）前後，檔案大小、時間與身分等狀態必須相同；檢查期間會持有防止其他作業同時更換檔案的鎖（`lease`），避免檢查完成後、程序啟動前被換檔；
- 執行 `--version` 的版本檢查（version probe）只接受上述版本範圍。

上述來源與檔案身分檢查通過後，只能直接啟動下列固定參數（argv），不經 shell，也不接受呼叫端提供額外參數：

```text
agy -p /usage --output-format stream-json
```

程序啟動後立即關閉標準輸入（stdin）。環境變數只保留明確允許的少數項目（allowlist），並停用 CLI 自動更新。標準輸出（stdout）上限為 1 MiB、標準錯誤輸出（stderr）上限為 64 KiB，最長執行 60 秒。結束碼不是零、stderr 有任何內容、輸出超過上限而被截斷、逾時或程序清理失敗時，一律停止並拒絕結果。

輸出解析器只接受預期的 command event 與 final result event。command event 必須是有明確型別的 `usage` 資料，而且兩個 event 的用量內容必須完全一致。

final result 必須是 success、沒有 error、沒有 conversation ID、`num_turns == 0`，且 input／output／thinking／cache-read／total token 全部為零。任一條件不符時會拒絕擷取；若內容明確顯示可能觸發模型，還會記錄保護狀態並停止後續擷取。

command event 必須剛好包含兩個已知群組：Gemini Models、Claude and GPT models。每組必須剛好包含 weekly 與 rolling five-hour 兩個已知額度區間，合計四個固定類型的用量區間。

出現未知、額外或重複的群組／額度區間、未知欄位、`fraction` 不在 0–1、`reset time` 無效，或 command 與 result 內容不同時，都會拒絕結果。解析器只產生四個 `AntigravityProductionUsageWindow`；結果物件釋放時會清除原始位元組，不會保存。

官方 `/usage` 命令每次都使用獨立、執行時間很短的程序，不會附加或控制既有 AGY 工作階段（session）。因此建立新連接與定時更新用量（polling）時，不需要關閉已開啟的 AGY 視窗。

## 為不同終端尺寸建立私有設定檔

診斷或研究需要不同的終端欄數或列數（viewport）時，可使用這個只供開發的離線命令。
以下 160x60 範例不適用於上方 R1 v3 流程；`derive-private-r1-section-spec-draft`
與 `promote-private-r1-section-spec` 都只接受 120x50 profile，其他尺寸的擷取資料無法接續這兩個步驟。

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  derive-private-r0-viewport-profile `
  --profile C:\absolute\private\profile-r0.json `
  --output C:\absolute\private\profile-r0-160x60.json `
  --columns 160 `
  --rows 60
```

來源與輸出必須是不同的 `.json` 絕對路徑，且放在同一個既有私有 `.key` 檔旁。此命令會完整驗證來源，只變更終端欄數與列數，將兩個 prompt 雜湊值清為空字串、兩個舊版 usage 雜湊值清為 `null`，再以不會留下半成品的方式寫入私有輸出。

相同內容重複執行時直接視為成功；若輸出檔已存在但內容不同，絕不覆寫。prompt 畫面會隨終端大小改變，因此擷取用量前，必須先對新 profile 執行 `calibrate-prompt`，再執行 `pin-prompt`。此命令不會啟動 AGY，也不會寫入終端輸入。

## 更新已核准的執行檔雜湊值

只有下列情況適合使用 `promote-executable-fingerprint`：AGY 執行檔的 SHA-256 或 CLI 回報版本已變更，但絕對路徑、file version、product version、簽署者名稱（subject）與憑證指紋（thumbprint）仍與已審查的 R0 私有設定檔相同。若上述任一項也變更，請重新建立並審查 profile，不要用這個命令略過審查。

先由審查者確認執行檔來自預期來源，並人工核對要核准的完整 64 位 SHA-256。`--approved-sha256` 必須填入這個已核對的值，不得使用近似值、未審查的值，或只因工具在本機算出該值就直接核准。

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  promote-executable-fingerprint `
  --profile C:\absolute\private\profile-r0.json `
  --output C:\absolute\private\profile-r0-promoted.json `
  --approved-sha256 <reviewed-64hex-sha256>
```

來源與輸出都必須是絕對路徑。來源 profile、其 `.key`、輸出目錄及既有輸出，都必須通過「只有目前使用者可存取」的 Windows 權限檢查（private ACL）。輸出必須是同一個私有目錄中的另一個 `.json` 檔，不能與來源 profile 或 `.key` 同一路徑。命令不會修改來源 profile，也不會覆寫內容不同的既有輸出；若既有輸出完全相同，會直接視為成功。

命令會鎖定 profile 指定的執行檔，重新檢查 SHA-256、WinVerifyTrust、簽章與檔案狀態，再執行固定的 `--version` 查詢。任何值不符、路徑是 reparse point，或檢查期間檔案有變動時都會停止，不產生輸出。

通過後，新 profile 只更新實際量到的 CLI version、SHA-256、WinVerifyTrust 狀態與 signer thumbprint；stdout 只回傳 `WasWritten` 與 `ApprovedSha256`，不含私有路徑。

## 擷取私有 R1 校準資料

此測試工具可將終端畫面擷取成私有資料包（rendered-screen bundle），供後續離線建立欄位規則。這個命令每次都會真的啟動 AGY，因此必須另行取得同意。Git 專案不會保存私有擷取結果，也不能用自動測試結果冒充實機擷取證據：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  capture-usage-r1-private `
  --profile C:\absolute\private\profile-r0.json `
  --output C:\absolute\private\screen-bundle.json `
  --i-understand-live-private-r1
```

此命令會讀取既有的 R0 私有設定檔與同目錄 key，但只在記憶體中清除兩個舊版 usage fingerprint，不會修改 profile。輸出必須是同一私有目錄中的另一個 `.json` 檔。

AGY 啟動前，程式會在固定大小上限內讀取既有輸出，並確認它是 `agy-usage-r1-private-screen-bundle-v2` 文件。

每個 bundle 都會保存一個「本次擷取條件的雜湊值」，避免把不同執行檔、終端大小或設定下的畫面混在一起。這個雜湊值涵蓋：

- 執行檔、終端大小（viewport）與已核准的 prompt 雜湊值；
- key、環境變數與設定檔路徑；
- 擷取當時的設定檔內容雜湊值；
- 逾時、輸出上限與終端是否使用替代畫面（alternate-screen mode）。

只要兩次擷取之間有任何設定內容或檔案資訊（metadata）改變，就必須建立新的 bundle，不能混合舊觀察資料。

程序、執行檔能力、prompt 畫面，以及寫入前的設定檔檢查全部通過後，工具最多只能寫入一次固定的 `/usage\r`，並保留一張已解析的終端畫面。它不會傳送模型提示、換頁鍵、終端查詢或第二次輸入。原始文字行絕不會寫入 stdout 或安全報告。

只有整次實機執行成功，包括程序清理與最後一次設定檔檢查都完成，才會保存結果。保存時會使用同目錄的私有暫存檔、強制寫入磁碟、重新讀回確認，再以不留下半成品的方式搬移或取代檔案。相同畫面重複寫入時直接視為成功；不同畫面會新增一筆 observation，每個 bundle 最多 16 筆。

請在不同時間執行此命令，直到 bundle 至少包含兩筆額度或重置狀態確實不同的觀察資料。只有帳號文字改變，不足以作為校準證據。真實 bundle 含有原始帳號與額度文字，絕不可 commit、分享或附在審查中。

取得畫面不等於 R1 已獲核准。還要分別完成人工審查欄位規格、離線產生 layout、確認同一工作階段的帳號身分，以及證明沒有觸發模型；這四項檢查彼此不能互相取代。

## 離線 R1 人工審查與核准

R1 的人工審查與欄位規則建立全部離線進行：不會啟動 AGY、建立 ConPTY 工作階段、寫入終端輸入、修改實機使用的 profile，或傳送模型提示。

先將私有 120x50 bundle 轉成內容可重現的機器草稿（deterministic machine draft）。此命令會確認 bundle 仍屬於該 R0 私有設定檔，而且設定檔內容雜湊值沒有改變，再寫出由 HMAC 防止竄改、狀態為 `NeedsReview` 的私有檔案。機器不會自行把草稿標成 `Reviewed`：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  derive-private-r1-section-spec-draft `
  --profile C:\absolute\private\profile-r0.json `
  --input C:\absolute\private\screen-bundle.json `
  --output C:\absolute\private\section-draft.json
```

草稿產生器依固定的實體列配置與有明確型別的動態欄位解析畫面，不會因某筆觀察資料剛好沒有變化，就把該值誤認為固定文字。帳號身分、額度百分比、倒數時間、credit 數量及外層工作階段列，只會轉成不保存實際值的格式規則。欄位起訖位置無法確認或內容不符合規則時，一律停止。

draft、bundle、輸出與 key 必須留在同一個私有目錄，而且目錄不能是 reparse point。工具會以不覆寫既有檔案的方式建立新檔；內容完全相同時，才把既有檔視為成功。命令完成時會在 stdout 輸出 JSON 檔案資訊；人工審查時必須記下其中的 `DraftFingerprint`。

Git 專案不包含原始私有用量畫面 bundle，也不包含機器產生的審查草稿。正式版本只包含已匯出、不含本機路徑且經人工審查的相容條件與畫面欄位規則（layout）。不得自行編造標題文字、區段或額度區間 ID、帳號標記或欄位格式規則。

審查新的 AGY build 時，只能從先前擷取的私有終端畫面 bundle 與人工審查過的 v3 區段規格（section spec）開始。私有 bundle 與任何狀態為 `NeedsReview` 的機器草稿，都必須放在 Git 已忽略的私有 `work/` 儲存區；其中可能含有帳號或 session 資料，絕不可 commit 或作為審查附件分享。

審查者確認 draft 與實際終端畫面、section／window 對應、欄位格式及各項安全限制一致後，使用原封不動的 draft 與剛才記錄的精確雜湊值產生 `Reviewed` spec。`--approved-fingerprint` 不接受近似值，也不接受重新計算後未經人工核對的值；profile、bundle、draft 與輸出必須位於同一個私有目錄：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  promote-private-r1-section-spec `
  --profile C:\absolute\private\profile-r0.json `
  --input C:\absolute\private\screen-bundle.json `
  --draft C:\absolute\private\section-draft.json `
  --output C:\absolute\private\reviewed-spec.json `
  --approved-fingerprint <64hex>
```

接著把私有 bundle 與剛核准的 spec 轉成已審查的畫面欄位規則（reviewed layout）：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  calibrate-usage-layout-offline `
  --input C:\absolute\private\screen-bundle.json `
  --reviewed-spec C:\absolute\private\reviewed-spec.json `
  --output C:\absolute\private\reviewed-layout.json
```

私有 v2 bundle 必須包含一個擷取條件雜湊值，以及 2–16 筆單頁觀察資料。每筆資料都必須符合：

- 完全相同的終端欄列數（viewport）與替代畫面模式（alternate-screen mode）；
- 每一列屬於哪個欄位的完整規則（physical-row ownership），以及必須固定的文字；
- 依序排列的 section／window 對應；
- 進度條、百分比與重置狀態（progress meter、percentage、reset state）的已審查格式；
- `RequireFullyVisible`。

`NoneWhenFullyVisible` 模式不得出現頁碼；`Counter` 模式必須包含完整頁碼，而且頁碼必須能證明整頁都已顯示。只有帳號身分改變，不足以作為校準證據；至少兩筆 observation 必須呈現不同且已確認的額度狀態，例如倒數時間改變，或經審查的「倒數轉成可用」狀態。

輸出是相同輸入就會得到相同內容的 `agy-usage-r1-layout-v3` JSON，只包含：

- 不再自動變更且經人工審查的 section spec；
- `SchemaFingerprint` 與預期的 `PageFingerprint`；
- 擷取條件雜湊值；
- 人工明確核准的草稿雜湊值（draft fingerprint）。

會隨帳號或時間變動的欄位只保留格式規則，不保存帳號身分、額度、重設時間、credit 或外層畫面的實際值。產生 layout 的程式不會自行把機器草稿升級為 `Reviewed`。

若 layout 記錄的擷取條件與目前執行檔、已核准的 prompt 雜湊值、終端大小、設定檔內容雜湊值或其他輸入不符，組合 profile 的程式（profile composer）會拒絕。檔案以不留下半成品的方式建立；內容相同的既有檔視為成功，內容不同時絕不覆寫。

最後將原始 R0 私有設定檔與 reviewed layout 組合成實機驗證用的 v3 section profile。三個檔案必須位於同一個私有目錄；組合前會重新核對目前設定檔內容雜湊值與擷取條件：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  compose-private-r1-section-profile `
  --profile C:\absolute\private\profile-r0.json `
  --layout C:\absolute\private\reviewed-layout.json `
  --output C:\absolute\private\profile-r1-v3.json
```

## R1 v3 實機驗證

`capture-usage-r1-section` 只接受格式完全符合 `agy-live-r1-profile-v3` 的 profile。它只能在上述人工審查、草稿核准、離線產生 layout 與組合 profile 全部完成後，另行取得同意進行實機擷取驗證。離線校準期間不得執行：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  capture-usage-r1-section `
  --profile C:\absolute\private\profile-r1-v3.json `
  --i-understand-live-r1
```

v3 section profile 會保存兩個經審查的 prompt 雜湊值：一個比對畫面結構（structural），一個用本機 key 精確比對整個畫面（keyed exact）。寫入 `/usage\r` 前兩者都必須相符。舊版 usage structural／exact pin 不會帶入。

執行器只會寫入一次固定的 `/usage\r`。之後依經審查的 v3 區段欄位規格（section schema）解析畫面，並比對欄位規格、整頁雜湊值（page fingerprint）及已確認額度內容的記憶體雜湊值，判斷連續讀到的結果是否穩定。整個畫面的 exact HMAC 只供本機診斷，不參與是否接受結果的判斷。

只接受完整顯示的單一頁面。`NoneWhenFullyVisible` 模式不得出現頁碼；`Counter` 模式必須有完整頁碼，而且 `start`、`end` 與 `total` 必須能證明整頁可見。其他情況一律停止，執行器也不會傳送換頁鍵或第二次輸入。

擷取驗證成功時，輸出必須是 `IsCaptureSuccessful=true`。`IdentityGate` 與 `ModelInvocationGate` 仍維持 `NotVerified`，所以 `IsR1Go` 必須是 `false`；擷取成功不代表這條路徑已核准正式使用。

### 舊版 R1 v1 實機驗證

舊版 `agy-live-r1-profile-v1` profile 仍使用 `capture-usage-r1`；不要將這個命令或 profile 欄位格式用於新的 v3 section 流程：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  capture-usage-r1 `
  --profile C:\absolute\private\profile-r1-v1.json `
  --i-understand-live-r1
```

R1 v1 只接受經審查的單一頁面。出現頁面頂端、中段或底端標記（top／middle／bottom marker）時會停止，執行器也不會傳送換頁鍵或第二次輸入；`IdentityGate`、`ModelInvocationGate` 與 `IsR1Go` 的限制和 v3 section 相同。

## 匯出已審查的套件允許資料（manifest）

將已完成審查的新 AGY build 加入封裝內的 Setup 精靈，是維護者專用操作。開始前，必須備妥完整的 R1 私有設定檔，並確認前述人工審查與擷取驗證都已通過：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  export-reviewed-package-manifest `
  --profile C:\absolute\private\profile-r1.json `
  --output C:\absolute\temporary\reviewed-package-manifest.json `
  --contract-id <stable-reviewed-contract-id> `
  --i-understand-public-manifest-export
```

輸出路徑必須指向既有目錄中的新 `.json` 檔。建立輸出前，匯出程式會：

- 完整載入私有 profile 與 key，並確認兩者只有目前使用者可存取（private ACL）；
- 執行固定的 `--version` 查詢；
- 重新確認執行檔能力與雜湊值，以及目前設定的擷取條件；
- 檢查已審查的 layout；
- 拒絕已知的本機私有欄位與看起來像私有值的格式。

輸出只包含不帶路徑的執行檔版本與簽章等資訊（executable metadata），以及 prompt／layout 雜湊值與欄位格式規則。此操作不會修改來源 profile，也不會寫入終端輸入。

先人工檢查匯出的 JSON，再執行套件允許資料與 Git 專案隱私測試。全部通過後，才能取代 `src/AiUsageDashboard.Antigravity/Resources` 內嵌的 manifest。

私有 profile、key、screen bundle、機器草稿、設定檔內容雜湊值、原始終端內容、帳號身分、實際額度、本機路徑或擷取條件雜湊值，絕不可加入 Git 專案或套件。尚未完成整套審查的 build 一律拒絕使用。

若封裝內的 Setup 改用不同的工作目錄規則，即使執行檔與用量 layout 未變，AGY 等待命令時的 prompt 結構仍可能不同。人工獨立確認官方 AGY prompt 後，維護者可在完全相同的 Setup 環境與 `%USERPROFILE%` 工作目錄規則下，執行不寫入任何終端輸入的校準：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  calibrate-reviewed-package-prompt `
  --profile C:\absolute\private\profile-r1.json `
  --i-understand-live-r0
```

此命令會啟動 AGY，但不會寫入任何終端輸入。它只輸出可公開的 prompt 結構雜湊值（structural fingerprint），不會輸出原始 prompt 或本機 exact HMAC。若要在不變更已審查執行檔或用量 layout 的情況下產生新的可攜式 manifest 候選檔，請執行：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  reanchor-reviewed-package-prompt `
  --input C:\absolute\reviewed-package-manifest.json `
  --output C:\absolute\temporary\reanchored-manifest.json `
  --contract-id <stable-reviewed-contract-id> `
  --structural <reviewed-64hex-structural-fingerprint> `
  --i-understand-reviewed-prompt-reanchor
```

`reanchor` 命令需要既有且格式完全正確的可攜式 manifest。它只會變更已選定的 prompt 結構雜湊值，以及由此計算出的整份相容條件雜湊值，並建立新檔案。只執行 reanchor 不足以證明安全；還必須重新執行封裝內的 Setup、確認正式 `/usage` 預覽通過、人工核對帳號身分與額度，再重跑所有測試與套件隱私檢查。

## 舊版 R0 實際診斷：需同意且只供人工檢查

先建立本機 HMAC key，再視需要從 Git 專案根目錄執行會操作實機的命令。每個命令都必須帶有明確同意參數：

```powershell
dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  init-hmac-key `
  --output C:\absolute\path\profile.key `
  --i-understand-live-r0

dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  pin-prompt `
  --profile C:\absolute\path\profile.json `
  --structural <64hex> `
  --exact <64hex> `
  --i-understand-live-r0

dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  repin-prompt `
  --profile C:\absolute\path\profile.json `
  --expected-structural <64hex> `
  --expected-exact <64hex> `
  --structural <64hex> `
  --exact <64hex> `
  --i-understand-live-r0

dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  pin-usage `
  --profile C:\absolute\path\profile.json `
  --structural <64hex> `
  --exact <64hex> `
  --i-understand-live-r0

dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  calibrate-prompt `
  --profile C:\absolute\path\profile.json `
  --i-understand-live-r0

dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  observe-prompt `
  --profile C:\absolute\path\profile.json `
  --i-understand-live-r0

dotnet run --project .\tools\AiUsageDashboard.AntigravitySpike -- `
  capture-usage `
  --profile C:\absolute\path\profile.json `
  --i-understand-live-r0
```

`init-hmac-key` 會建立 `.key` 檔，但不會啟動 AGY。請將 profile 放在同一個目錄，並把 `key-path` 欄位設為該檔案的絕對路徑；不得將產生的 key 複製到 JSON。

`pin-prompt` 與 `repin-prompt` 只編輯本機 profile，不會啟動 AGY。

- `pin-prompt` 只寫入 JSON 中兩個 prompt 雜湊值；已有另一組值時拒絕覆寫。
- `repin-prompt` 只有在目前值與傳入的預期值組（expected pair）完全相符時才更新。目標值組（target pair）已存在時不重寫，直接視為成功。
- expected pair 與 target pair 必須不同。任一條件不符時不會修改 profile，也不會留下暫存檔。

兩個命令都會原封不動保留 profile 的其他位元組。回報成功前，會檢查私有目錄、繼承的 Windows 存取權限（ACL）、JSON 結構、檔案鎖定、取代、備份與復原狀態。

`pin-usage` 也只編輯本機 profile，不會啟動 AGY。它要求兩個 prompt 雜湊值都已固定，而且 JSON 中兩個 usage fingerprint 必須是字面值 `null`。通過後，只會以不留下半成品的方式，把兩個 `null` 替換成經審查的 64 位十六進位 usage 雜湊值。

已有不同的 usage 雜湊值時，命令會停止；值已相符時不重寫，直接視為成功。寫入 prompt 與 usage 雜湊值時，共用既有的鎖定檔 `.pin-prompt.lock`，並使用相同的原始位元組保留、ACL、備份與復原流程，避免兩個命令同時修改檔案。Usage exact fingerprint 必須與 prompt exact fingerprint 不同。

完成這項一次性的本機設定後，依三個階段執行實機命令：

1. `calibrate-prompt` 會檢查程序、執行檔能力、設定檔及程序清理結果，任一項無法確認就停止。它會忽略 profile 內已保存的 prompt fingerprint，等待畫面穩定，但不寫入終端輸入。輸出只包含遮蔽過的畫面結構與使用本機 key 計算的完整畫面 HMAC，不會修改 profile。人工審查這些值後，若既有雜湊值已不相符，請用目前值作為 expected pair 執行 `repin-prompt`。
2. `observe-prompt` 要求所有已設定的 prompt fingerprint 相符。它會啟動互動式 CLI 並等待 prompt 畫面穩定，但不寫入終端輸入。擷取用量前，先用此命令確認經審查的 prompt 雜湊值仍相符。
3. `capture-usage` 要求兩個預期的 prompt fingerprint 都相符。寫入前一刻，它會再次確認沒有其他符合條件的程序，而且 prompt 畫面狀態沒有改變。檢查項目包括游標狀態（cursor state），以及 DEC private mode 25、1004、2004 與 9001。

   全部通過後，命令只會寫入一次 `/usage\r`。校準時可暫不設定兩個 usage fingerprint；命令會回傳用量畫面結構雜湊值與 `UsageExactLocalFingerprint`，但擷取是否已核准的欄位 `UsageCaptureGate` 仍是 `NotVerified`。

   人工審查兩個回傳值後，執行一次 `pin-usage` 再重新擷取。完成前不得把該畫面規則（layout）視為已驗證。

終端欄列數（viewport）改變時，必須建立另一份已審查 profile。在某個終端大小校準的 R0 畫面結構雜湊值或 R1 欄位內容雜湊值，都不能拿來允許另一個終端大小。

舊版 R0 profile 是本機診斷用的嚴格允許清單。R1 profile 另外固定下列項目，任一項不同都會停止：

- 執行檔絕對路徑、CLI 與 PE 版本；只有確實沒有 PE 版本時才能使用 `<absent>`；
- SHA-256、WinVerifyTrust 結果、簽署者名稱（subject）與憑證指紋（thumbprint）；
- 終端欄列數、環境變數、同目錄 `.key` 的絕對路徑與 prompt fingerprint；
- 經審查的畫面欄位規則、逾時設定，以及擷取前後都要確認未變更的設定檔。

`NonCredentialSettingsFiles` 只能包含目前 Windows 使用者真實 `USERPROFILE` 下的 `%USERPROFILE%\.gemini\antigravity-cli\settings.json`。檔案必須已存在、是一般檔案且不是 reparse point；指向其他使用者或格式不符時一律停止。

## 舊版 ConPTY 實際執行的安全控制

- 只保留明確允許的少數環境變數（allowlist）。每次檢查執行檔行為及建立互動式工作階段時，都會強制設定 `AGY_CLI_DISABLE_AUTO_UPDATE=true`。
- 檢查執行檔能力前、程序啟動前及啟動後，只要發現另一個符合條件的 AGY 程序，舊版操作就會立即停止。唯一例外是工具剛建立且已納入控制的工作階段。工具不會附加或終止使用者原本開啟的 AGY 工作階段。
- 已解析的終端文字只在記憶體中處理，不會輸出到 JSON 報告。報告只包含終端尺寸、是否使用替代畫面、非空白列編號、粗略寬度分類、畫面結構雜湊值，以及校準用的本機完整畫面 HMAC。
- 用量查詢逾時時，報告只會增加停止原因分類、true／false 狀態、有限的計數、位元組數、既有的遮蔽畫面結構，以及本機完整畫面 HMAC。報告絕不包含終端文字或原始終端位元組，也不會放寬任何畫面雜湊值或穩定性檢查，更不允許再次寫入輸入。
- 執行前後都會用檔案大小、時間等資訊及 SHA-256，記錄目前使用者 Antigravity CLI 必要設定檔的快照。無法完成檢查、路徑被替換、出現 reparse point 或檔案內容有任何變動時，設定檔檢查就會失敗。credential 與 token 檔絕不能列入這種前後比對。
- 實機程序由 Windows Job Object 控制。清理時會明確終止工作階段、等待程序結束並釋放資源；任何清理失敗都會使安全檢查失敗。
- `calibrate-prompt` 與 `observe-prompt` 不會寫入輸入。每個 R0／R1 擷取命令，包括私有 R1 校準擷取，都只能寫入一次 `/usage\r`。寫入前一刻還必須確認沒有另一個符合條件的程序，並再次比對 prompt 的畫面結構與完整畫面雜湊值。這些命令都不會傳送模型提示。
- `IdentityGate` 與 `ModelInvocationGate` 刻意維持 `NotVerified`；`IsR0Go` 與 `IsR1Go` 一律為 `false`。舊版正式讀取元件不會因帳號身分或模型呼叫證據仍未驗證，就略過自己的安全檢查。

  它仍要求擷取結果未超過界線、所有已實作的安全檢查通過、執行檔來源可信、只寫入一次、完整頁面可見、四個額度區間的內容與順序正確，而且同一個已驗證頁面中只能有一個格式統一的帳號身分欄位。這個欄位只供顯示帳號，不代表任何未驗證項目已通過。

## 舊版 ConPTY 離線元件

- 精確檢查執行檔支援的功能與 Authenticode 簽章雜湊值。
- 建立暫停狀態的 ConPTY 程序，再交由 Windows Job Object 控制。
- 依固定上限保存輸出；超過保存上限後仍繼續讀完並丟棄，避免程序被塞住。
- 分段驗證 UTF-8，並依虛擬終端控制碼還原畫面。
- 嚴格解析測試用額度頁面，並限制可接受的頁數範圍。
- 確認同一工作階段在用量畫面前後顯示相同帳號身分。
- 比較確定會觸發模型的測試案例（positive control）與 `/usage` 不應觸發模型的案例（negative case），作為模型是否被呼叫的證據。
- 依經審查的 R1 spec 產生欄位規則、解析完整畫面、計算欄位規格與整頁雜湊值，再以不留下半成品的方式輸出已遮蔽實際值的 layout。

舊版正式讀取元件只能使用這台電腦上明確核准的 R1 私有設定檔。Profile、執行檔、prompt、已審查的 layout、設定檔內容雜湊值、執行檔來源、完整頁面規則或額度欄位結構，只要有一項不符就會停止。完成重新校準與人工審查前，不得恢復舊版讀取。官方 `/usage` 讀取元件完全不會讀取這份私有 profile；只要官方執行檔路徑已有值，即使官方方式失敗，也不會改走舊版 ConPTY 路徑。
