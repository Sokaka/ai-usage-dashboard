# Windows 候選版本與發布流程

供 AI Usage Dashboard 維護者建立、驗收及公開候選版本。版本狀態見 [README](README.md#目前版本)，一般操作見[使用說明](使用說明.md)。

## 建置前

使用乾淨 `main` 的完整 source SHA。Windows CI 必須通過同一 SHA 的 Git 歷史敏感資訊檢查、restore、Release build、完整測試、production line coverage 70% 與封裝 gates。NuGet moderate 以上 advisory 會使 restore 失敗。App、Setup 使用同一組實際 runtime 與相依版本，不把歷史結果當成本次通過。

程式碼與文件共用 source 隱私規則；候選建置檢查本機可見 refs 的全部歷史，失敗 diagnostics 通過檢查後才可上傳。掃描範圍、限制、本機指令與公開後 required status checks 設定見 [CI 敏感資訊檢查](docs/CI_PRIVACY.md)。

SDK 由 `global.json` 固定，transitive NuGet audit 由 `Directory.Build.props` 啟用。沒有 Git 歷史的原始碼匯出目錄可建置與測試，不能產生符合正式發布契約的候選包。

每次發布須依[升級與相容性原則](docs/COMPATIBILITY_POLICY.md)確認舊資料、既有安裝與 CLI 的受影響範圍及驗證結果。必要的不相容變更須在 Release 說明原因、受影響版本、使用者步驟與復原限制；不以要求刪除設定、全部重新登入或強制換 CLI 取代相容性處理。

.NET 8.0.31 是目前固定的 runtime；依 [Microsoft 支援政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)，.NET 8 於 2026-11-10 結束支援。每次候選重新核對 patch 與 advisory；逾期或出現未處理風險時先修正，再建候選。

`Microsoft.Extensions.AI.Abstractions` 固定為 10.9.0，適用另行更新的 [Platform Extensions 支援政策](https://dotnet.microsoft.com/en-us/platform/support/policy/extensions)。App、Setup 與 ClaudeCapture 共用的 `System.Text.Json` 固定為 10.0.11；各自的 dependency manifest 必須涵蓋相同版本及其依賴，並通過合併成品的實際離線授權匯出。

private repo 的 Actions 額度由 owner 共用。首次 push 前關閉新 repo Actions 並回讀；按 [GitHub 計費規則](https://docs.github.com/en/billing/concepts/product-billing/github-actions)核對帳號額度、已用量、artifact/cache 儲存、預定成功／失敗執行量及超額停止措施，再受控啟用。無法確認免費範圍時只做本機驗證，不啟用付費 runner 或更動全帳號預算。候選 artifact 與失敗 diagnostics 的 retention 也計入前檢。

## 版本、序號與信任鍵

完整盤點已知分發來源的 feeds／manifests，包括所有 Release 和 assets 的分頁。首個 stable 的 `releaseSequence` 必須高於已核對上界，之後持續遞增；跨 channel 也不豁免。不得用新 repo 從 1 開始的 `run_number` 作序號。來源無法核對時明列未知，不宣稱已取得完整上界。

App 與 Updater 版本都須高於舊版。重新建置造成 bytes 改變時使用新版本與新序號，不替換同版本的檔案。舊 installed manifest 的 sequence 有值與 null 兩種情況都要驗收。

feed 使用 [固定簽章格式](docs/UPDATE_FEED_FORMAT.md)。正式 build 從受控外部檔案嵌入可信公鑰，簽署使用對應私鑰；未知 signer 或無效簽章必須在信任 feed 欄位前失敗。公鑰不可從同一未驗證 feed 學習。私鑰不進 source、Git、成品或 logs。

| 情況 | 處理 |
| --- | --- |
| 日常簽署 | 使用現有免費受控簽署環境，權限只給發布工作；PR 與一般 build 不取得私鑰。 |
| 加密備份 | 保留以獨立口令加密、存於維護者個人 Google Drive 的異地備份，口令分開保管；從 Drive 重新下載後，實際驗證備份還原與 signer 整合。雲端備份不記為離線媒體，保管與還原要求見 [簽章私鑰保管](docs/SIGNING_KEY_CUSTODY.md)。 |
| 換鍵 | 分兩次發布：先用舊鍵簽署，交付同時信任新舊鍵的 Updater；確認銜接後才改用新鍵。 |
| 私鑰遺失 | 沒有可用備份時，須透過正式 repo/HTTPS 重新取得可信 Updater。 |
| 私鑰洩漏 | 停止使用該鍵，發布移除其信任的新 Updater，並說明使用者必須手動銜接的範圍。不能依賴已洩漏的唯一信任根證明新鍵安全。 |

首次發布不購買自有 EXE 的 Authenticode 憑證；第三方官方 CLI 的來源與簽章檢查仍保留。feed 簽章不提供 Windows CA 發布者身分，也不保證消除 SmartScreen 提示。

## 候選建置與凍結

所有 workflow、版本、條款及簽章變更先完成，再建立候選。候選 workflow 必須綁定指定 main SHA、原始 build run 與 attempt；原 workflow 檔名保留為相容入口，實際參數以 repository 中的 workflow 為準。

build job 保存原始 run／attempt 及上傳的 artifact ID／digest。發布 job 先核對來源，再依該 ID 下載；只重跑發布 job 時，Release 說明與凍結紀錄仍沿用原 build attempt。來源 outputs 缺失或 artifact 核對失敗時停止，不以目前發布 attempt 補值；Actions artifact 名稱包含 build run／attempt，六件正式成品名稱不變。

封裝順序固定為：最終 binaries → App ZIP → ZIP／Updater EXE 的 size 與 SHA256 → 帶正式 stable URL 與完整版本／sequence 的 feed → 簽署 → 最終 feed 的 SHA256。任何受簽欄位改變都須重簽。

封裝工具會建立多層暫存目錄，請使用較短的 `OutputRoot`。未啟用 long paths 的 Windows 若使用過深目錄，可能在 Copilot 成品檢查時失敗；先縮短輸出路徑，不移除缺件檢查或要求使用者變更全機設定。

正式候選只有六件，全部來自同 source/run/attempt：

- `AiUsageDashboard-<version>-win-x64.zip` 及 `.sha256`。
- `AiUsageDashboard-Updater-<version>-win-x64.exe` 及 `.sha256`。
- `AiUsageDashboard-update-stable.json` 及 `.sha256`。

ZIP 包含自有 LICENSE、精確第三方原約/notices 及元件交付清單。Updater、maintenance 副本、capture helper 的適用文字須由真正 trim/single-file 成品離線匯出後核對；缺件或 digest 不符就停止。接受紀錄按同 Windows 使用者、條款版本與範圍共用，不上傳；首次接受、拒絕、條款變更、portable／Setup／Updater／helper／委派及非互動入口都要測試。

凍結前把六件保存為 private 候選 Release，使用最終 tag 與 asset 名稱，維持 draft／prerelease。保留 repo identity、Release identity、tag、source SHA、原 build run／attempt、每個 asset ID／名稱／size／SHA256。凍結後不得重建補件、重新打包、重簽或替換 assets。

## Private 驗收

用受控 HTTPS 測試 feed 與同一候選 App／Updater bytes 驗收。測試 feed 的 URL／簽章另存，不覆蓋正式 feed；保持正式 channel、TLS 與簽章檢查，不增加產品 GitHub token 或略過驗證。private GitHub Release 不能證明匿名下載成功。

在乾淨 Windows x64 記錄 OS、每個官方 CLI 實際版本、使用版本／序號、步驟與結果：

1. 五個 provider：Claude、Codex、GitHub Copilot、Grok 與 AGY 的官方登入、帳號隔離、用量、背景刷新、取消／失敗、安全冷卻及設定保留。
2. fresh install、舊 internal→stable 一次手動銜接、installed sequence 有值／null、後續至少一次更新與 Updater 自更新。
3. 拒絕舊 sequence、舊版本及同版本異 bytes；feed／artifact 竄改、未知鍵、缺簽、壞簽，以及換鍵。
4. canonical maintenance EXE bytes／feed／channel／version 與 Windows 登錄；exit 0 仍須檢查登錄 warning。自訂 install root 維持不登錄行為。
5. 每個 journal／rename 邊界終止程序、復原再中斷、rollback 失敗、磁碟不足、檔案鎖定與未知檔案保留。單元測試不等於真實斷電驗收。
6. 解除安裝、maintenance 自清理、使用者資料與 Credential Manager 保留；接受提示不阻擋控制、唯讀與復原路徑。
7. 首次下載的 Windows 提示、啟動／重新啟動、浮窗、鍵盤與 High Contrast。不得關閉全域防護來通過。
8. source、新歷史、logs/artifacts、圖片與 binaries 的公開面掃描，文件連結、license/source provenance 與六件 bytes 核對。

安裝、支援及資料保留細節見 [Windows 分發與支援手冊](INTERNAL_DISTRIBUTION.md)。沒有測試環境就記為待驗；本機 build、單元測試或 `apply-local` 都不代替正式線上 E2E。

候選發布前依 [CLI 相容性維護](INTERNAL_DISTRIBUTION.md#cli-相容性維護)執行，並在 Release 引用 [CLI 相容性](docs/CLI_COMPATIBILITY.md) 中該 App 版本的唯一實測表，公開實際 CLI 版本、範圍及未驗項目的去識別摘要。詳細證據保存在受控紀錄，不公開本機路徑或帳號；歷史或部分實測不得寫成本候選完整通過。

## 最後公開決策

候選可檢閱後，由維護者確認具體成品、驗收結果與尚未執行的正式 URL smoke，再決定公開。同時保留 Claude／AGY 個案條款適用的不確定性，以及 Microsoft 原約的 publisher 義務。個人作品、MIT 免責、使用者按同意或外部 AI 審核，都不等於供應商許可；不另設個別廠商回函門檻。

Claude 保留未修改官方 binary、內建 auth、使用者自己的憑證與直接計費，不自建 Claude.ai 登入、不代管 session token、不代付或轉售用量；`--claudeai`、`CLAUDE_CONFIG_DIR` 及 child-process 隔離仍保留。這些判斷項不代表已確認完全符合 [Anthropic 產品整合條款](https://code.claude.com/docs/en/legal-and-compliance)。AGY 使用 user-installed、unmodified 官方 CLI；headless 技術支援不排除條款限制。新增證據若明確影響功能，先列精確適用事實再由維護者決定。

圖示由使用者請助手生成，實際工具與生成紀錄未知，尚未證明排他權利。服務名稱僅說明相容性；第三方 copyright、license 與 notices 均須保留。

## 轉正同一 Release 與正式端點驗收

公開前重新核對凍結記錄與遠端六件。任何 identity、source、run/attempt、asset ID、size 或 digest 不符，立即停止。缺件不重建補件。

確認授權後，公開新 repo，僅把同一候選 Release 改為非 draft、非 prerelease 且為 latest，再回讀。此步不 dispatch build、不改 bytes、不把當前操作的 run/attempt 取代原始 build 來源。

不帶 cookie/token，匿名下載正式 feed：`https://github.com/Sokaka/ai-usage-dashboard/releases/latest/download/AiUsageDashboard-update-stable.json`。驗簽後核對 App／Updater URLs、版本／sequence、hash／size 和全部 sidecars，確認與凍結候選相符。再走正式下載的 fresh install、舊 internal→stable 與線上 smoke，通過才宣告發布完成。

失敗時停止後續公開操作、保留實際已公開狀態，按當次授權的範圍修正或撤回。source 改回 private 不能消除曝光；修正 bytes 時另定新版本／sequence，再驗收，不用降級或同版本換檔回復服務。
