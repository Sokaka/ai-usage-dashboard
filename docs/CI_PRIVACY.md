# CI 敏感資訊檢查

本文件供 AI Usage Dashboard 維護者確認檢查範圍、在本機重現失敗，以及判斷哪些內容仍須人工核對。程式碼、Markdown 文件與設定檔使用相同敏感內容規則；Windows CI 與候選建置另檢查 Git 歷史，失敗診斷檔須經檢查才可上傳。

## 檢查位置

| 檢查 | 執行時機 | 範圍 |
| --- | --- | --- |
| 目前 source | 完整測試中的 `RepositoryPrivacyTests` | checkout 的程式碼、文件、設定、腳本及敏感檔名；包含個人 email、家目錄路徑與疑似憑證 |
| Git 歷史 | 設定 .NET SDK 後、solution restore 與 build 前 | 所選 commits 的完整檔案版本、敏感檔名與 commit message；檢查較明確的 secret 特徵 |
| 失敗診斷檔 | history 檢查成功、Test 已執行且 job 失敗時 | 根目錄 `test-results.trx` 與子目錄中的 `*.cobertura.xml`；包含個資與 secret 檢查 |
| 分發封裝 | `Publish-Internal.ps1` 的封裝檢查 | 阻擋禁止交付的私人檔案；仍須依[發布流程](../RELEASING.md)核對實際成品 |

共用規則見 [PrivacyRules.cs](../tools/AiUsageDashboard.PrivacyCheck/PrivacyRules.cs)。它辨識私鑰、常見 provider API key、GitHub／AWS／Google／Slack token、URL 內嵌帳密與疑似硬編碼憑證。這些是明確規則，不能保證發現所有秘密；加密、壓縮、圖片與執行檔中的內容仍有檢查邊界。

`PrivacyCheck` 是開發用工具，不隨 App 分發；它由合成 fixtures 驗證，不列入 production line coverage 的分發模組統計。

目前 source 的個資檢查保留範例 email domain 與範例使用者名稱。第三方原約／notices 的個資例外只適用於與 `LegalCatalog` 所載 SHA-256 完全一致的文件，secret 檢查仍照常執行。不可只因檔案在第三方目錄，就自行略過檢查。

source 使用 Git 的 tracked 檔案與未被 ignore 的 untracked 檔案清單，排除 `.git`、`bin`、`obj`、`work` 目錄。強制加入 Git 的 `.env.*`、`auth.*.json`、`credentials.*.json`、`private-key*` 等敏感檔名仍會被阻擋。只有 `.env.example`、`auth.example.json`、`credentials.example.json` 這三個範例檔名有例外，內容照常檢查。

沒有已知副檔名的檔案也會嘗試解碼，純文字不會只因副檔名不同而略過。source 的錯誤報告最多列出 50 筆定位，但仍檢查完整清單；這個顯示上限不限制掃描範圍。

## Git 歷史的選取方式

兩個 build workflow 都使用 `fetch-depth: 0`，避免只取得最新 commit。一般 Windows CI 依事件選取範圍：

- PR：base SHA 之後、直到 checkout 的 merge HEAD 所引入的 commits。
- 既有分支 push：事件的 `before` SHA 之後、直到 checkout HEAD 所引入的 commits。
- 新分支 push 與手動執行：HEAD 及其全部祖先。
- 候選建置：`--all`，檢查本機可見 refs 與 HEAD 可到達的歷史。

事件值經 environment variable 傳入，base 必須是完整 40 字元 SHA。工具拒絕 shallow repository；Git 列舉／讀取失敗、缺少物件或超過上限都使檢查失敗，不會改成只掃目前檔案。

每個選定 commit 都檢查完整 tree，同一 blob 的內容只讀取一次；相同內容出現在不同路徑時，仍分別檢查敏感檔名。這能檢查先加入秘密、之後又刪除的檔案。歷史檢查也掃 commit message，但不掃作者／committer metadata 或個資，避免把原約聯絡資訊與 Git 作者 email 當成秘密。

`--all` 只代表本機可見 refs 的範圍，不涵蓋已刪除 refs、reflog、不可到達物件或尚未取得的遠端歷史。submodule/gitlink 會使掃描失敗，必須另行界定外部 repository。Git LFS 的 pointer 也不等於外部物件內容。公開前應核對待公開的 refs 與成品，不能只看一次本機結果。

在 repository 根目錄執行目前 HEAD 的全部祖先檢查：

```powershell
dotnet run --project tools/AiUsageDashboard.PrivacyCheck --configuration Release -- history --repository . --revision HEAD
```

檢查所有本機可見 refs：

```powershell
dotnet run --project tools/AiUsageDashboard.PrivacyCheck --configuration Release -- history --repository . --all
```

需要重現增量檢查時，使用 `--revision` 指定 HEAD 或完整 SHA，並加上 `--base` 與事件中的完整 base SHA。工具不會自行 fetch，也不會更動 Git 歷史。

## 失敗診斷檔的上傳邊界

診斷工具只選取根目錄 `test-results.trx` 與遞迴目錄中的 `*.cobertura.xml`。其他 logs、attachments、dump、圖片及 binaries 不會被複製到 upload 目錄。所選檔案會檢查原始文字及 XML 解碼後的 attribute／text，避免敏感字串透過 XML entity 編碼繞過；DTD 與 external entity 不予接受。

所有選定檔案通過後，工具將同一份已檢查 bytes 寫入新目錄，才交給 `upload-artifact`。不會自動遮蔽或改寫內容。任何檢查失敗都阻擋整批上傳；既有 output 目錄、reparse point、無法解析的 XML 或超過上限也會失敗。input 不存在或沒有符合檔案時，回傳成功且不建立 output，workflow 的 `if-no-files-found: ignore` 不上傳任何檔案。

本機重現時，將 `test-results` 指向已產生的測試結果，`safe-test-diagnostics` 必須尚不存在：

```powershell
dotnet run --project tools/AiUsageDashboard.PrivacyCheck --configuration Release -- diagnostics --input test-results --output safe-test-diagnostics
```

診斷檔沿用 source 的個資規則與範例例外，沒有第三方 notices 例外。workflow 只上傳 `safe-test-diagnostics`，retention 為 7 天；原始 `test-results` 不直接上傳。檢查器只回報定位與規則，不印命中值，但無法收回測試／build 已經寫入 Actions console 的內容；產生日誌的程式仍須避免輸出憑證、真實帳號與原始用量資料。

## 上限與結果判讀

| 邊界 | 上限 |
| --- | --- |
| 歷史 commits／unique blobs | 5,000／50,000 |
| 歷史 distinct blob-path／單一 tree 列舉輸出 | 250,000／16 MiB |
| 歷史單一 blob／累計內容（commit messages 與 unique blobs） | 16 MiB／256 MiB |
| Git 指令／history 取消期限 | 60 秒／10 分鐘 |
| 每次 regex 比對 | 2 秒 |
| 單次文字比對／歷史違規集合 | 各 10,000 筆；超過即失敗 |
| 診斷目錄 entries／選定檔案 | 10,000／100 |
| 診斷目錄層數／XML 深度 | 16／128 |
| 診斷單一檔案／累計內容 | 64 MiB／256 MiB |

文字採嚴格 UTF-8 或 UTF-16 解碼，UTF-16 依 BOM 或可辨識的 ASCII byte 排列判定；不自動猜測其他編碼。被辨識為 binary 的內容不做文字規則檢查，預期為文字卻無法解碼時失敗。歷史結果列出 commits、unique blobs、text blobs、binary blobs、總 bytes 與違規數；必須連同 binary 數及選取範圍一起判讀。超限是未完成，不是掃描通過。上表是 CLI 上限，source 測試本身沒有總檔數或 bytes 上限。

命中時先依檔案／行號／規則，在受控本機檢視；不要把原始值貼到 issue 或 CI log。若確認憑證已進入 Git 或公開 logs，先停用或輪替該憑證，再決定如何處理已曝光內容。修改例外須有具體證據，不能以略過文件、關閉檢查或降低測試門檻解決。

## 合併與公開前的設定

啟用 Actions 會讓符合事件的 workflow 執行；若未設定 required status checks，失敗結果本身不會強制阻擋合併。GitHub Free 的 private repository 不提供 protected branches；公開後，應將實際成功執行的 Windows CI job 設為必要檢查，並核對 bypass 設定。[GitHub protected branches 說明](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)

workflow 與規則本身也屬於受審查的程式碼。公開、分支保護與 Git 歷史修正各自依[發布流程](../RELEASING.md)及維護者授權執行，不由掃描工具自動修改遠端設定。
