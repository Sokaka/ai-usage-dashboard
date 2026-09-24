# 實作與驗證清單

更新日期：2026-09-25

讀者：負責 AI Usage Dashboard 開發、測試與交付的人員。本文只列現況、驗證界線與尚待工作。

一般操作見 [使用說明](使用說明.md)；架構、資料格式與各服務的實作見 [技術總覽](docs/TECHNICAL_OVERVIEW.md)；候選驗收與公開規則見 [RELEASING.md](RELEASING.md)。

## 目前 source

標準安裝提供開始選單捷徑；浮窗與系統匣提供「關於 AI Usage」，可查看及複製完整版本、開啟使用說明與 Releases／問題回報入口。README 包含合成畫面預覽與安裝方式對照，並提供支援、安全回報文件及 Issue 表單。這些功能與 Copilot 訂閱資訊修正始於首個正式版本 `1.0.3 / sequence 1017`；目前正式版本為 `1.0.9 / sequence 1023`。

### AGY official-only 與封裝縮減

- [x] Production AGY 連接與背景更新只使用 user-installed、unmodified 官方 CLI 的 `agy -p /usage --output-format stream-json`；ConPTY、R0／R1 profile、private key、reviewed manifest 與 status-line capture 只保留在開發用 Spike，不會編入 production 路徑。
- [x] AGY 設定畫面改由 `AiUsageDashboard.Antigravity.Setup.dll` 在 App process 內顯示。正式套件不再包含 `AiUsageDashboard.Antigravity.Setup.exe` 或 `AiUsageDashboard.AntigravityCapture.exe`；App 仍保存可中斷續做的 setup attempt 與 approval receipt。
- [x] 核准來源保存於 `%LOCALAPPDATA%\AiUsageDashboard\antigravity\approved-source-v1.json`。舊 `AI_USAGE_DASHBOARD_ANTIGRAVITY_EXECUTABLE` 只在新檔缺少時一次性遷移；production 不讀取 `AI_USAGE_DASHBOARD_ANTIGRAVITY_PROFILE`。
- [x] Version probe 與 `/usage` 都直接執行完整核准路徑、不經 shell；環境採固定 allowlist，不含 `PATH`／`COMSPEC`，Windows Job Object 的 active-process limit 為 `1`。
- [x] 新版不安裝或讀取 AGY status line，也不要求 `/statusline off`。升級清理只接受精確 AI Usage ownership marker、預期私人路徑與 hash-named helper，並在 ACL、內容與並行變更檢查通過後移除；自訂或無法驗證的 status line 保持不變。
- [x] `1.0.7` exact frozen candidate 已完成 Release 建置、4,208 項完整測試、74.39% production line coverage、封裝 invariant、WithSecure 掃描，以及 portable／canonical 的實際官方 AGY `/usage` runtime 驗收。這是一台受管機器的 exact-bytes 結果，不代表其他端點政策或 AGY 登入流程已驗證。

### App 更新偵測與提示（`1.0.4` 起）

- [x] Production App 與 Updater 共用解析後的 stable feed URL、channel 與 public trust keys。`Publish-Internal.ps1` 要求明確提供三項；`Publish-UpdateBundle.ps1` 預設 `stable` channel 與該 channel 的 GitHub latest download feed URL，但仍要求外部 trust file。正式 publish 會在缺少必要輸入或 URL／trust 格式無效時 fail fast，並從發布後的 App DLL 讀回 feed／channel metadata 與 embedded trust bytes 逐值核對。App 的 feed client 只讀取、限制大小、核對最終 HTTPS URI 並驗簽，不下載 artifact；Updater 安裝時仍獨立重新驗證。
- [x] Source 以 exact executable layout 與 adjacent installed manifest 分成 canonical managed、custom managed、unmanaged／portable。只有 canonical、固定 maintenance Updater 存在且 shutdown listener 已 ready 時提供 **更新並重新啟動**；其餘只開固定 Releases，portable 無法解析 `ProductVersion` 時明確顯示無法判斷。
- [x] 新增首次 non-modal 網路說明、30 秒初次 delay、24 小時成功節流、`15m → 1h → 4h → 24h` 失敗退避、manual join、resume／clock anomaly、auto-check 開關、24 小時 version＋sequence snooze 與每 release 一次的 balloon attempt。狀態使用獨立 strict JSON cache，不保存 URL、Updater path 或 install decision。
- [x] 展開浮窗有獨立 update banner；收合狀態有 high-contrast-aware `↑` badge 與同一 tooltip／automation composer；tray 永久保留手動檢查與 auto-check 開關，About 顯示更新狀態。背景失敗不開 modal；手動檢查在可見介面內顯示結果，從 tray 發起且浮窗隱藏／收合時，失敗會顯示結果 dialog，UpToDate 也會顯示完成提示。
- [x] 2026-09-15 本機 CI 等價驗證：restore 與 Release 建置成功，0 warnings／0 errors；完整測試 4,243／4,243 通過，0 failed／skipped；production line coverage 75.38%（53,119／70,465，門檻 70%）。另以合成 HTTPS feed URL 與臨時 3072-bit public trust 完成 production publish contract；成品共 328 files、ZIP 77,796,534 bytes，直接讀回的 feed／channel metadata 與 embedded trust resource 相符。此驗證包未安裝、簽署、上傳或發布。
- [x] 2026-09-15 唯讀 live probe 使用 production public trust 與產品 checker，以五次 logical feed request 經 GitHub HTTPS redirect 至 `release-assets.githubusercontent.com`，下載並驗證正式 stable feed `1.0.3 / sequence 1017`；較舊 portable 版本判為可更新、同版判為已最新，較高已見 sequence 會拒絕 replay。實際既有 canonical 安裝與暫存 custom／portable layout 的分類正確，且 capability probe 只有 canonical 可使用 maintenance Updater。此 probe 未啟動 App／Updater、未下載 artifact，也未改動既有安裝。
- [x] 另保留較低版 `1.0.2-verify.update-notification.20260915.1` 的 production-config 驗證包供隔離 VM 使用；328 files、ZIP 77,796,874 bytes、SHA-256 `a2e7c0b16b0de881c2d8517a2ae592c0f5489eddefb4228c6a952222856a4e2a`。已從 finalized package 核對 ProductVersion、正式 feed、`stable` channel、production public trust、sidecar、allowlist、dependency/license profiles 與四組 offline license exports。2026-09-15 在 `.57` 以 non-admin interactive token 受控啟動一次；14 項前置與正常關閉 assertion 通過，但該使用者尚未接受 candidate `2026-09-06.1` catalog，App 在任何更新 UI observation 前退出。原 portable、preferences 與 update state 已核對恢復，Task 與遠端專用目錄已刪除；這不是 portable UI E2E 通過。此包未安裝、簽署、上傳或發布。
- [x] 2026-09-16 在 `.57` 以當時 source 的 production-config 驗證包重跑更新提示觀察 `20260916-82b1fec6`；Portable 與 CanonicalManaged 共 130 項 assertion 通過、0 errors，環境安全還原，10 份受控 evidence 已收集後清除遠端專用目錄。使用者已確認最終珊瑚紅 badge 的人眼外觀；此結果只涵蓋提示 UI 與 badge，未按下真正更新，也不代表後續 source、自訂安裝、一鍵 Updater、自我更新、實際 balloon 或無障礙 E2E 通過。
- [x] 2026-09-16 目前 source 修正 auto-check 偏好儲存失敗提示、延後的無障礙更新公告，以及 maintenance Updater 被鎖定時的 generation promotion／重試。delegated child 會驗證 signed cache、direct parent 與 exact process identity 後接手 promotion；receipt lease 與 snapshot compare/rebind 會排除過期 promoter，registration repair 成功後才清除舊 generation，解除安裝也能辨識硬中止留下的自有 promotion temporary file。Release 建置成功，0 warnings／0 errors；完整測試 4,300／4,300 通過，0 failed／skipped，耗時 34 秒。這是 source 驗證，不代替正式候選的一鍵更新、自我更新或無障礙 E2E。
- [x] 2026-09-21 舊 source 候選 `1.0.5-verify.rc.20260921.1` 已完成 canonical managed 一鍵更新與 Updater 自我更新 E2E。候選集合 SHA-256 為 `c87d5d75a7679a76eca326c0097fd59148c335b37e475d718997b27d64a0ed56`，run `acceptance-20260921-2fab1524-canonical` 為 `passed`：17 項 assertion 通過、0 errors、scenario completed、環境還原成功；UI Automation 實際按下 `UpdatePrimaryAction`，核對精確 feed request、App／Updater 各下載一次、promoter 等待超過舊 2 分鐘界線仍存活、parent 結束後 canonical Updater 提升為 signed target，以及 already-current recovery。finalize 為 `finalized`，後續 cleanup verification 為 `verified-clean`。此結果只適用於該候選，不代表之後變更的目前 source 已驗收。
- [x] **前一版 source 候選的完整 E2E 未通過，失敗紀錄與復原證據已保存**：候選 `1.0.6-verify.rc.20260921.1` 的 source identity SHA-256 為 `6d6292c5395d54ea099852fc34487d417662a7c657ec56f93f8ffe8c6e41e640`、artifact set SHA-256 為 `48a9c4f846a643619290e9874cfccd33c0f005b5af8744f608e9d6e22e23e581`。run `acceptance-20260921-6ab1a070-canonical` 的一鍵更新與 Updater 自我更新 scenarioStatus 為 `passed`、16 項 assertion 通過；但原 terminal 為 `failed`（exitCode 1、2 errors、environmentRestored=false），憑證清理證明失敗。後續 recovery 遇 certutil observer race；原 failed result 已保留，經獨立復原後 finalize 為 `finalized`、environmentSafe=true，收集 main 69 份、當次 recovery 13 份、前兩次失敗 recovery 各 6 份 evidence；main cleanup 為 `cleaned`，postcleanup 為 `postcleanup-probed`，所有自有 root／task／process 已清除且 Root／My 回到 baseline。這些是失敗後的安全復原，不能當作修正後 source 的 E2E 結果；原失敗紀錄保持原樣，修正後驗證見下方 2026-09-22 新候選紀錄。
- [x] 2026-09-22 目前 source 會持續觀察更新安全關閉 listener；啟動後若意外停止，App 會清除 ready 狀態並記錄原始錯誤，畫面更新失敗則獨立記錄。新增真實 named pipe 故障回歸測試。Release 建置 0 warnings／0 errors，定點測試 17／17、完整測試 4,315／4,315 通過，0 failed／skipped；production line coverage 75.18%（54,125／71,995，門檻 70%）。這是修正後 source 的本機驗證；新候選的遠端結果另列於下一項。
- [x] 2026-09-22 修正後 source snapshot SHA-256 `fa9d7a09d3facb4d5a0a78007533a5cfffb0b250b7cb0b79771b3331e5cd1527` 的獨立 canonical managed 一鍵更新／Updater 自我更新 E2E 已通過。以 `1.0.6-verify.rc.20260922.1` 更新至 `1.0.7-verify.rc.20260922.1`；候選 target artifacts seal `fedfa4a87e550e01bfd74eb754e4823c8cb04081e013cf0c5c236fe74563a089`，harness seal `6ed1c1e1352a1e0f4e0e75b182015fcb5823d4fe385003abf6098fb75a110e9b`。`.57` run `acceptance-20260922-be0f804c-canonical` 有 17 項 assertion、0 errors，scenarioStatus／terminal 均為 `passed`、exitCode 0、environmentRestored=true；實際透過 UI Automation 按下 `UpdatePrimaryAction`，核對 App／Updater 各下載一次、delegated promotion 等待超過舊兩分鐘界線與 already-current recovery。獨立 finalize 為 `finalized`、environmentSafe=true、taskRemoved=true；69 份 evidence 已收集，cleanup 為 `cleaned`，其後唯讀探測確認遠端專用 root、task、測試憑證與產品程序均不存在。此驗證只涵蓋該隔離候選與 canonical managed 流程，未簽署、上傳或發布正式版本。
- [ ] custom managed／portable、實際 Windows balloon、200% text、High Contrast 與螢幕閱讀器 E2E 尚未完成；source build／單元測試不得改列為這些人工驗收通過。

### Copilot 訂閱資訊相容性修正

- [x] 已定位 `1.0.2` 搭配 CLI `1.0.82` 的訂閱解析失敗：受控診斷的 `GET /user` 與 quota 成功，訂閱回應因缺少 SDK `1.0.11` 要求的 token 而解析失敗。範圍見 [1.0.2 歷史實測表](docs/CLI_COMPATIBILITY.md#102-候選版)，不列為完整 provider 驗收通過。
- [x] Source 新增不讀取回應 token 的訂閱 reader，支援舊格式含 token 與新格式省略 token，保留 host／login 核對、billing 三態與 quota 欄位優先權；未知 auth type、錯誤型別與重複關鍵欄位會拒絕採用。
- [x] Source 以固定 SDK 契約的 reflection adapter 讀取 `account.getCurrentAuth`，沿用既有 transport／lifecycle；訂閱失敗保留已驗證 quota，顯示安全警告並寫入不含原始回應或 exception message 的診斷。SDK 相依版本、CLI 支援範圍與帳號／憑證／快取格式未變更。
- [x] 2026-09-14 修正版 restore／Release 建置成功，0 warnings／errors；Copilot 相關測試 102/102、完整測試 4,115/4,115 通過，0 skipped，production line coverage 75.79%（51,100／67,421，門檻 70%）。涵蓋新舊合成回應、SDK 記憶體 RPC、既有 schema 7 帳號及 schema 3 用量快取、憑證沿用與失敗提示；不包含真實 CLI 或安裝升級驗收。
- [x] 修正版 `1.0.3-local.20260914.1`（source `88a3b8dfd7dc188392ad078b83e33184199459b3`）已由乾淨 source 完成封裝、四組實際離線授權匯出及本機 `1.0.2` → 新版 `apply-local` 更新；328 個 payload、完整 App 版本、Windows 登錄、開始選單與 previous 備份核對通過，安全退出及重新啟動成功，無更新警告。沿用原 maintenance Updater；更新工具未讀取帳號設定或 credentials，未另驗收卡片內容及有效登入狀態。
- [x] Source `88a3b8dfd7dc188392ad078b83e33184199459b3` 的首個 [Windows CI attempt 1](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/34819937211) 為 4,114 passed／1 failed；既有 Claude 登入逾時測試等待模擬 runner 啟動時逾時，後續 coverage／package gates 未執行。修正測試時序後，exact candidate source `0b7a5d922e56b9590370d776a99642d5dd671b2c` 的 [Windows CI](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/34822636429) 完成 4,115 passed／0 failed／0 skipped，production line coverage 75.78%（51,094／67,423），並通過後續 package gates；原失敗紀錄仍保留。
- [x] 2026-09-14 使用者回報 `1.0.3-local.20260914.1` 更新後「看起來有正常了」；記為本機畫面觀察，尚未逐項核對訂閱欄位或完成受控 provider 驗收。
- [ ] `1.0.3` 尚未另做受控真實 CLI／訂閱顯示驗收；本機更新成功不代表 provider 功能已驗收。維護者已決定在保留這項未驗證界線的前提下公開；`1.0.2` 歷史凍結成品保持原件。

### Claude 登入逾時測試穩定性

- [x] 測試改以非同步訊號等待模擬 runner 進入，再明確觸發登入及帳號鎖等待的 deadline，排除固定計時先取消尚未啟動工作的競態。計時控制沿用既有 Grok 測試做法；`ClaudeAccountLogin` 的內部建構子可注入 `TimeProvider`，正式入口仍預設系統計時與原有逾時設定。
- [x] 2026-09-14 Release 建置通過，0 warnings／errors；Claude 登入測試 18/18、完整測試 4,115/4,115 通過，0 failed／skipped，production line coverage 75.78%（51,092／67,423，門檻 70%）。
- [x] 獨立副本在 `DOTNET_PROCESSOR_COUNT=1` 下的原版測試通過；分別忽略 deadline token、移除 runner 的 `Task.Run`、移除 cleanup tracking 的三個故障版本均被測試抓出，且完成清理後自行退出，未觸發外層 watchdog。驗證涵蓋逾時返回、同步阻塞及清理前保留帳號鎖。
- [x] `0.0.0-verify-claude-timeout.20260914.1` 通過封裝 gate、三組 dependency profiles 與四組實際離線授權匯出。這是帶未提交 source 標記的驗證包，未安裝或發布。
- [x] Exact candidate source 的 [Windows CI](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/34822636429) 與[候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/34839329802) 均包含此修正並通過完整測試；上述結果不改寫先前失敗的 CI 紀錄。

### 候選建置前的 source 驗證

以下保留各階段當時的結果及限制；`1.0.2` 的成品與安裝驗證另列於 [1.0.2 歷史候選](#102-歷史候選)，目前成品狀態列於[目前正式版本](#目前正式版本)。

2026-09-14 開始選單路徑修正驗證：

- Programs 路徑改在捷徑操作時才解析；空路徑、無效路徑與存取失敗會回報警告，自訂安裝解除安裝不查詢 Programs。新增 10 案回歸，117 案定向測試通過；Release 建置為 0 warnings／0 errors，完整回歸為 4,027 passed／0 failed／0 skipped，production line coverage 75.75%（51,002／67,332），門檻維持 70%。
- 修正後的相同捷徑 source 再次通過獨立 trimmed、single-file EXE 的 20 項檢查。空路徑與例外使用合成注入，未改動 Windows 的實際 Programs 目錄，也未重跑正式 Updater 安裝／升級；原本機驗證包保持原件。

2026-09-14 公開入口初版驗證（上述路徑修正前）：

- Release 建置通過，0 warnings／0 errors；修正測試後的完整回歸為 4,017 passed／0 failed／0 skipped，production line coverage 75.73%（50,980／67,318），門檻維持 70%。
- 首輪為 4,012 passed／5 failed／0 skipped：4 案是新增 About 後的測試預期或 Dispose 觀測方式錯誤，已修正並通過 61 案定向回歸；另 1 案為下述 Codex fixture 清理失敗再次發生，第二輪通過仍不代表根因已修復。清理例外可能覆蓋測試 body 的原始結果，不能宣稱首輪 body 已通過。
- About 以合成資料完成 5 組 palette × 2 種寬度的 10 張離線預覽，包含窄視窗與長版號。High Contrast 只套用 palette，未切換 Windows 模式；未實際操作剪貼簿、瀏覽器或檢查多螢幕焦點。
- 捷徑原始碼在獨立的 .NET 8.0.31／win-x64 trimmed、single-file EXE 中通過 20 項檢查，包含建立、欄位回讀、重入保留、衝突保留、舊安裝補建與匹配移除。這是相同 source 的隔離驗證，未執行正式 Updater 安裝或操作使用者開始選單。
- 本機驗證包 `0.0.0-verify-publication.20260914.1` 封裝、四組實際授權匯出及 ZIP 內容檢查通過；共 328 files、77,738,999 bytes，checksum sidecar 與實際 SHA256 相符。版本 metadata 保留 dirty source 標記；未安裝、簽署、上傳或建立正式候選。

Copilot 改為使用本機安裝的官方 CLI，主包不再隨附第三方 CLI；`GitHub.Copilot.SDK` 保持 `1.0.11`，CLI `1.0.79` 保留為相容性基準，接受正式版 `>=1.0.79` 且 `<2.0.0`。帳號綁定、Credential Manager 與程序隔離要求維持不變。

CLI 來源須通過官方身分及受保護副本檢查，缺少或不符時保留既有卡片與 token，安裝／更新後重試。其他 `1.x` 尚未宣稱真人實測通過，精確來源規則見 [CLI 相容性](docs/CLI_COMPATIBILITY.md)。

連接與用量查詢的 CLI 搜尋、驗簽及副本準備已移到背景；取消後晚到的執行檔鎖會清理。這項程式修正完成時，Release 建置通過（0 warnings／0 errors），404／404 項相關回歸測試通過，0 failed／0 skipped，包含阻塞解析器時呼叫先返回、取消、晚到檔案鎖釋放及未啟動 CLI／SDK 的測試。

目前封裝只保留繁中、簡中、英文及日文的執行環境資源，App 自身介面仍為繁中。語系設定後 Release 建置通過（0 warnings／0 errors），62／62 項封裝、授權及隱私相關測試通過；該次調整未重跑完整套件、coverage 或真人 UI／CLI 驗收。

語系調整時的驗證包 `0.0.0-verify-locales.20260913.1` 封裝及四組實際授權匯出通過。ZIP 從前一驗證包的 82,248,336 bytes 降到 77,735,352 bytes，減少 5.49%；完整列舉共 328 files，只移除其他 10 種語系的 170 個資源檔。保留的 294 個相依元件 binary 與 15 個授權／manifest 檔案均與前包 bytes 相同；繁中、簡中及日文各保留 17 個資源檔，英文內建資源亦保留。主包維持不含第三方 CLI。

背景準備修正前的完整測試首輪為 3,982／3,983 通過、1 failed、0 skipped，production line coverage 為 75.79%（50,754／66,966）。唯一失敗為既有 `CodexVersionProbe_WhenRootSpawnsChild_ConfirmsTreeEmptyBeforeGateRelease` 在清理合成 fixture DLL 時遭遇存取拒絕；該案例單獨重跑（未收集 coverage）通過，但首輪失敗原因尚未確定。背景準備修正後只重跑上述相關測試，未重跑完整套件或 coverage，不宣稱完整測試一輪全過。

後續捲動條提交 `8a564747` 的 [Windows CI](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/34764614043) 完整測試為 3,985 passed／0 failed／0 skipped，production line coverage 75.77%；這是另一份遠端驗證，沒有改寫上述本機失敗或其未知原因。對應本機 `0.0.0-verify-scrollbar.20260913.4` 已完成 90 案定向回歸、封裝與安裝，尚非正式凍結候選；這些結果也不代替本次新增功能的驗證。

上述公開入口及 Copilot 相容性修正已在 `1.0.3` 凍結，真人 CLI 登入／用量驗收仍暫緩；歷史候選的成品與驗收只適用於各自 source，不代替目前正式版本的結果。

## 目前正式版本

`1.0.9 / sequence 1023` 已沿用原凍結候選正式公開（[source `51d7cfd5d6ef31775b862967afc95a20223a5774`](https://github.com/Sokaka/ai-usage-dashboard/commit/51d7cfd5d6ef31775b862967afc95a20223a5774)）。[v1.0.9 GitHub Release](https://github.com/Sokaka/ai-usage-dashboard/releases/tag/v1.0.9) 現為 immutable stable／latest；Release ID `395846934`，tag target、source 與六件 asset identity 均與凍結候選一致。變更與六件成品資料見 [1.0.9 版本說明](docs/releases/1.0.9.md)。本版 App 的 `src/` 與 `1.0.8` 相同；沿用前版已簽署的 Updater 原始 bytes，維護 Updater 版本仍為 `1.0.8`。

- [x] 同 source 的 [Windows CI](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/36022467564) 通過。[候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/36023166191) attempt 1 通過 Release 建置（0 warnings／0 errors）、完整測試（4,262 passed／0 failed／0 skipped）與 production line coverage 74.46%（48,278／64,841）。
- [x] 已從 draft 候選獨立核對 freeze receipt、Release ID、run／attempt、source、sequence，以及六件成品的 asset ID、大小、SHA256 與三份 sidecar；production public trust 對 stable feed 的 RSA-PSS-SHA256 驗簽通過。
- [x] ZIP 有 323 個項目，隨包指南與候選 source 相符。正式公開後，latest Release metadata 的 tag、source、Release ID 與六件 asset identity 已回讀確認。
- [x] 正式公開後，以無 GitHub token 或 cookie 的 client 從 latest 端點匿名下載六件原成品。Release ID、tag target、六件 asset ID／名稱／大小／SHA256、三份 sidecar、stable feed `1.0.9 / sequence 1023`／source／正式 URL、RSA-PSS-SHA256 簽章及 latest feed bytes 均與原凍結候選一致；六件合計 106,791,195 bytes。
- [x] 以 Updater `CreateHttpClient()` 設定對正式 latest feed 執行唯讀 GET，取得 HTTP 200、1,916 bytes，SHA256 與凍結 feed 相同。這沒有啟動 `1.0.8` App 按鈕或安裝流程。

`1.0.9` 尚未完成實際官方 CLI 登入／用量、舊版與最新版 CLI 逐版實測、安裝、由 `1.0.8` App 按鈕發起的 signed online update、舊版 Updater 自我交接及完整 UI／無障礙驗收。匿名下載與簽章 smoke 不代替 App 更新的端到端驗證。歷史版本的實機結果不沿用為本版結果。逐平台狀態見 [1.0.9 CLI 實測表](docs/CLI_COMPATIBILITY.md#109)。

### 1.0.8 歷史正式版

`1.0.8 / sequence 1022` 已沿用原凍結候選正式公開（[source `2241852d355767f2922a0e933b7c29d294e3ef00`](https://github.com/Sokaka/ai-usage-dashboard/commit/2241852d355767f2922a0e933b7c29d294e3ef00)）。[v1.0.8 GitHub Release](https://github.com/Sokaka/ai-usage-dashboard/releases/tag/v1.0.8) 當時為 immutable stable／latest；Release ID `395529211`，tag target、source 與六件 asset identity 均與凍結候選一致。變更與六件成品資料見 [1.0.8 版本說明](docs/releases/1.0.8.md)。

- [x] 同 source 的 [Windows CI](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/35980432912) 通過。[候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/35981500721) attempt 1 通過 Release 建置（0 warnings／0 errors）、完整測試（4,257 passed／0 failed／0 skipped）與 production line coverage 74.47%（48,286／64,841）。
- [x] 已從 draft 候選獨立核對 freeze receipt、Release ID、run／attempt、source、sequence，以及六件成品的 asset ID、大小、SHA256 與三份 sidecar；production public trust 對 stable feed 的 RSA-PSS-SHA256 驗簽通過。
- [x] ZIP 有 323 個項目，隨包指南與候選 source 相符。正式公開後，latest Release metadata 的 tag、source、Release ID 與六件 asset identity 已回讀確認。
- [x] 正式公開後，以無 GitHub token 或 cookie 的 client 從 latest 端點匿名下載六件成品。Release ID、tag target、六件 asset ID／名稱／大小／SHA256、三份 sidecar、stable feed `1.0.8 / sequence 1022`／source／正式 URL、RSA-PSS-SHA256 簽章及 latest feed bytes 均與原凍結候選一致；六件合計 106,791,242 bytes。

`1.0.8` 的 Codex 舊版 count-only、逐券狀態／到期時間及 Claude 新舊格式已有合成回歸測試；本版仍未完成實際官方 CLI 登入／用量、安裝、signed online update、舊版 Updater 自我交接、完整 UI／無障礙驗收。歷史版本的實機結果不沿用為本版結果。逐平台狀態見 [1.0.8 CLI 實測表](docs/CLI_COMPATIBILITY.md#108-歷史正式版)。

### 1.0.7 歷史正式版

`1.0.7 / sequence 1021` 已沿用原凍結候選正式公開（[source `c4ad25f41e973b508583252945aafa05572a4299`](https://github.com/Sokaka/ai-usage-dashboard/commit/c4ad25f41e973b508583252945aafa05572a4299)）。同一筆 [v1.0.7 GitHub Release](https://github.com/Sokaka/ai-usage-dashboard/releases/tag/v1.0.7) 當時為 immutable stable／latest；Release ID `394622165`、tag target 與六件成品的 asset ID、大小及 SHA256 均維持凍結值。變更與下載檔案資訊見 [1.0.7 版本說明](docs/releases/1.0.7.md)。

- [x] [候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/35857959043) attempt 1 通過 Release 建置與完整測試：0 warnings／0 errors，4,208 passed／0 failed／0 skipped，production line coverage 74.39%（47,907／64,400）。同一個 attempt 完成更新清單簽署、驗簽與六件成品凍結。
- [x] 發布前已核對 freeze receipt、Release ID、run／attempt、source、sequence、六件 asset identity／大小／SHA256 與三份 sidecar；production public trust 對 stable feed 的 RSA-PSS-SHA256 驗簽通過。
- [x] 正式 ZIP 展開為 323 個檔案，App 目錄只有 App 與 ClaudeCapture 兩個 EXE；Setup DLL 存在，standalone Setup／AGY Capture、Spike、provider CLI、`createdump` 與 Setup runtime/deps 檔均不存在。
- [x] WithSecure Client Security `26.2.158` 對正式 ZIP 掃描 306 項、對展開內容掃描 305 項，harmful 0、skipped 0。Exact candidate 啟動與兩次 AGY refresh 期間沒有新的 DeepGuard／infection event；此結果只涵蓋本次一台受管機器與 exact bytes。
- [x] 同一 exact candidate 以 portable 路徑啟動，並以 `apply-local --no-restart` 套用至既有標準安裝。交易為 `Committed`、沒有 staging；323 個 ZIP payload 逐檔相符，previous 舊版完整保留。App、維護 Updater、Windows 安裝紀錄與開始選單均已核對，canonical App 重新啟動且正常回應。
- [x] Portable 與 canonical 啟動都取得新的 AGY 用量 snapshot；直接 child 只有已核准且具有效簽章的官方 `agy.exe`，沒有 `cmd.exe`、PowerShell、standalone Setup 或 AGY Capture helper，也沒有重新建立舊 status line/helper。
- [x] 2026-09-23 正式公開後，以不帶 GitHub token 或 cookie 的 client 經 latest Release API 核對並下載六件 Release assets；tag target、Release／asset identity、大小、SHA256、三份 sidecar，以及 latest stable feed 的 version、sequence、source、正式網址與 RSA-PSS-SHA256 簽章均與原凍結候選一致。六件共 106,781,209 bytes。

`1.0.7` 的 exact frozen 成品已完成上述本機封裝、安全掃描、AGY runtime、離線安裝與公開端點 smoke，但仍未完成乾淨 Windows 初裝、App 內一鍵 signed online update、舊版 Updater 自我交接、custom install root、High Contrast、多螢幕／混合 DPI、完整 UI／無障礙，以及 Claude、Codex、GitHub Copilot、Grok 的真人登入與五個 provider 的完整帳號流程驗收。離線 `apply-local` 不等於 signed online update；既有 AGY 登入下的 `/usage` 成功也不等於登入流程已驗證。逐平台狀態見 [1.0.7 CLI 實測表](docs/CLI_COMPATIBILITY.md#107)。

### 1.0.6 歷史正式版

`1.0.6 / sequence 1020` 已沿用原凍結候選正式公開（[source `abbcb67bebdd5e97e4414e66ed5329518b0a15b3`](https://github.com/Sokaka/ai-usage-dashboard/commit/abbcb67bebdd5e97e4414e66ed5329518b0a15b3)）。[v1.0.6 GitHub Release](https://github.com/Sokaka/ai-usage-dashboard/releases/tag/v1.0.6) 是歷史 immutable stable 版本；Release ID `393937190`、tag target 與六件成品的 asset ID、大小及 SHA256 均維持凍結值。變更與下載檔案資訊見 [1.0.6 版本說明](docs/releases/1.0.6.md)。

- [x] 原始[候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/35752107845) attempt 2 通過 Release 建置與完整測試：0 warnings／0 errors，4,307 passed／0 failed／0 skipped，production line coverage 75.09%（54,119／72,071）。同一個 attempt 完成更新清單簽署、驗簽與六件成品凍結。attempt 1 在簽署與上傳前停止，未建立 Release 或候選成品。
- [x] 發布前已下載 freeze receipt 與六件 draft assets，逐一核對 Release／asset identity、大小、SHA256、run／attempt、source、sequence 與三份 sidecar；以 production public trust 驗證 feed 簽章與正式網址。Release API 與 asset URL 在 draft 狀態下均對匿名請求回傳 404。
- [x] 2026-09-23 正式公開後，以未帶 GitHub Authorization、cookie 或 proxy 的 client，透過 latest Release API 核對六件 Release assets，再從正式 asset URL 匿名下載；stable feed 另由 latest URL 取得。完整 assets 清單、tag target、Release／asset identity、大小、SHA256、三份 sidecar，以及 feed 中的 version、sequence、source、正式網址與 RSA-PSS-SHA256 簽章均與原凍結候選一致；六件共 113,100,482 bytes。
- [x] 2026-09-23 以 exact candidate Updater 的 `apply-local --no-restart` 套用至既有標準安裝。Updater 自然關閉 App，transaction `Committed`、無 staging；新 current 的 328 個 ZIP payload 逐檔相符，previous 完整保留。App 與維護 Updater 的 ProductVersion、Windows 安裝紀錄及開始選單 target／working directory 均已核對，App 從 canonical 路徑重新啟動且正常回應。離線 installed manifest 的 source／release sequence 依設計為 null，不列為 signed online update。
- [x] 同一 exact candidate 在單螢幕 100% DPI、四個停靠角各完成高頻展開取樣；中間 resize／移動狀態保持 hidden，第一個可見 expanded frame 均已在最終位置，錯位可見 frame 為 0。經典藍、曜石黑、柔霧灰、櫻花粉四個一般主題的收合箭頭皆無 accent 背景／外框；捲動條實際像素分別符合 `#26344B`、`#24242B`、`#D7D1C8`、`#E5D2D9`。

`1.0.6` 的 exact frozen 成品已完成既有標準安裝的 `apply-local` 與指定 UI smoke，但仍未完成乾淨 Windows 初裝、App 內一鍵更新、舊版 Updater 自我交接、custom／portable、High Contrast、多螢幕／混合 DPI、完整 UI／無障礙，以及五個 provider 的真人登入與用量驗收。離線 `apply-local` 不等於 signed online update；歷史合成候選的 canonical managed E2E 也不能改列為本版正式成品的實機驗收。逐平台狀態見 [1.0.6 CLI 實測表](docs/CLI_COMPATIBILITY.md#106)。

### 1.0.5 歷史正式版

`1.0.5 / sequence 1019` 已沿用原凍結候選正式公開（[source `9f3a77a6cb340eb7ea5a89398bb8f774fce8a2dc`](https://github.com/Sokaka/ai-usage-dashboard/commit/9f3a77a6cb340eb7ea5a89398bb8f774fce8a2dc)）。同一筆 [v1.0.5 GitHub Release](https://github.com/Sokaka/ai-usage-dashboard/releases/tag/v1.0.5) 當時成為 immutable stable／latest；Release ID 與六件成品的 asset ID、大小及 SHA256 均維持凍結值。變更與下載檔案資訊見 [1.0.5 版本說明](docs/releases/1.0.5.md)。

- [x] 原始[候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/35698661125) attempt 1 通過 Release 建置與完整測試：0 warnings／0 errors，4,305 passed／0 failed／0 skipped，production line coverage 75.17%（54,119／71,995）。同一個 run 完成更新清單簽署、驗簽與六件成品凍結。
- [x] 從 draft 候選 Release 獨立下載六件成品並核對 Release／asset identity、大小、SHA256 與三份 sidecar；以 production public trust 驗證 feed 簽章與 version、sequence、source、正式網址及兩個主要成品的大小／SHA256。草稿狀態的 Release API 與 asset URL 均已確認匿名回傳 404。
- [x] 正式公開後，以未帶 GitHub 憑證或 cookie 的 client，從 latest URL 匿名下載六件成品。六件共 113,099,733 bytes，asset ID、大小、SHA256、sidecar 與原凍結候選一致；feed 簽章再次驗證通過。
- [x] 下載的 ZIP 基本結構已核對：單一頂層目錄與 328 個項目，包含 LICENSE、元件清單、App 與 Setup EXE。這不是安裝驗收。
- [x] 2026-09-22 在既有標準安裝上，以正式 `1.0.5` Updater 的 `update-online` 經 stable HTTPS feed，將 `1.0.5-icon-verify.6` 本機預覽版更新為正式 `1.0.5`。Updater 退出碼 `0`，交易 `Committed`；manifest 的版本、sequence、source、大小及 SHA256、Windows 安裝紀錄和 App 版本均相符，維護 Updater SHA256 與正式成品相同，App 重新啟動且正常回應。詳細範圍見 [1.0.5 版本說明](docs/releases/1.0.5.md)。

`1.0.5` 的 exact frozen 成品仍未完成乾淨 Windows 初裝、App 內一鍵更新、舊版 Updater 自行下載並交接新版、custom／portable 操作、完整 UI／無障礙，以及五個 provider 的真人登入與用量驗收。本次正式線上更新由 `1.0.5` 獨立 Updater 發起，不等於 App 內一鍵流程或舊版 Updater 自我交接已驗證。上方合成候選的 canonical managed E2E 雖已通過，使用的不是本版正式成品，不能改列為 `1.0.5` 的實機驗收。逐平台狀態見 [1.0.5 CLI 實測表](docs/CLI_COMPATIBILITY.md#105-正式版)。

### 1.0.4 歷史正式版

`1.0.4 / sequence 1018` 已沿用原凍結候選正式公開（[source `4fa5b3c578c2e9a8bd22f53ff72a8ef8b91d200b`](https://github.com/Sokaka/ai-usage-dashboard/commit/4fa5b3c578c2e9a8bd22f53ff72a8ef8b91d200b)）。當時同一筆 [v1.0.4 GitHub Release](https://github.com/Sokaka/ai-usage-dashboard/releases/tag/v1.0.4) 為 stable／latest；Release ID 與六件成品的 asset ID、大小及 SHA256 均維持凍結值。變更與下載檔案資訊見 [1.0.4 版本說明](docs/releases/1.0.4.md)。

- [x] 原始[候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/35682687386) attempt 1 通過 Release 建置與完整測試：0 warnings／0 errors，4,304 passed／0 failed／0 skipped，production line coverage 75.17%（54,119／71,995）。同一個 run 完成更新清單簽署、驗簽與六件成品凍結。
- [x] 從候選 Release 獨立下載六件成品並核對 asset ID、大小、SHA256 與三份 sidecar；以 production public trust 驗證 feed 簽章與 version、sequence、source、正式網址及兩個主要成品的大小／SHA256。
- [x] 正式公開後，以未帶 GitHub 憑證、cookie 或 application proxy handler 的 client，從 latest URL 匿名下載 feed 與其餘五件成品。六件共 113,099,569 bytes，asset ID、大小、SHA256、sidecar 與原凍結候選一致；feed 簽章再次驗證通過。
- [x] 下載的 ZIP 基本結構已核對：單一頂層目錄與 328 個項目，包含 LICENSE、元件清單、App 與 Setup EXE。這不是安裝驗收。
- [x] 2026-09-22 在既有標準安裝上，以正式 `1.0.4` Updater 的 `update-online` 從 `1.0.3` 經 stable HTTPS feed 更新成功。Updater 退出碼 `0`，交易 `Committed`；manifest 的版本、sequence 與 source、Windows 安裝紀錄和 App 版本均相符，維護 Updater SHA256 與正式成品相同，App 重新啟動且正常回應。舊版 `1.0.3` 保留在交易中；詳細範圍見 [1.0.4 版本說明](docs/releases/1.0.4.md)。

`1.0.4` 的 exact frozen 成品仍未完成乾淨 Windows 初裝、App 內一鍵更新、舊版 Updater 自我交接、custom／portable 操作、完整 UI／無障礙，以及五個 provider 的真人登入與用量驗收。本次正式線上更新由 `1.0.4` 獨立 Updater 發起，維護 Updater 已升至 `1.0.4`，不等於上述兩種發起流程通過。上方 `1.0.6-verify` → `1.0.7-verify` canonical managed E2E 雖通過 17 項 assertion，但使用不同的合成候選與成品，不能改列為 `1.0.4` 的實機驗收。逐平台狀態見 [1.0.4 CLI 實測表](docs/CLI_COMPATIBILITY.md#104-正式版)。

### 1.0.3 歷史正式版

`1.0.3 / sequence 1017` 已沿用原凍結候選正式公開（[source `0b7a5d922e56b9590370d776a99642d5dd671b2c`](https://github.com/Sokaka/ai-usage-dashboard/commit/0b7a5d922e56b9590370d776a99642d5dd671b2c)）。當時同一筆 `v1.0.3` GitHub Release 為 stable／latest；成品未重建、重簽或替換。變更與下載檔案資訊見 [1.0.3 版本說明](docs/releases/1.0.3.md)。

- [x] [候選 workflow](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/34839329802) 的原始 attempt 1 通過 source 歷史隱私檢查、restore、Release 建置與完整測試：0 warnings／0 errors，4,115 passed／0 failed／0 skipped，production line coverage 75.79%（51,103／67,423），門檻維持 70%。
- [x] 同一 workflow 完成更新清單簽署與驗簽，並在 private repository 內以 draft／prerelease 凍結正好六件成品。正式發布沿用同一筆 Release；三個主要檔案的 size 與 workflow 紀錄 SHA256 已列於 [1.0.3 版本說明](docs/releases/1.0.3.md)。
- [x] 已從 Release 獨立下載六件成品；asset ID、size、SHA256、三個 sidecar、build artifact 與原始 workflow freeze 全部一致。下載的 feed 另以 pinned production 公鑰完成驗簽，內容的 source、version、sequence、網址與兩個主要成品的 size／SHA256 均相符。
- [x] 2026-09-15 正式公開後，以未帶 GitHub Authorization／cookie，且停用 application proxy handler 的 client 先匿名下載 latest stable feed 並驗簽，再下載其餘五件檔案。完整 assets pagination 已耗盡；六個 frozen asset ID、總計 113,016,854 bytes、逐件 SHA256、三份 sidecar、tag target、source、version、sequence 與正式網址均相符。
- [x] 六件成品、全部 328 個 ZIP 檔案與候選 workflow log 的有界靜態隱私檢查通過，0 findings；ZIP 亦通過完整 CRC、路徑、重複名稱、symlink、dependency profile、runtime pin 與五組離線 license export 檢查。Binary 檢查有可擷取字串與容器邊界，未遞迴拆解單檔 EXE 內嵌內容，也未對圖片做 OCR。
- [x] 已在既有 Windows 主機使用 exact frozen Updater／ZIP 執行 `apply-local`。Updater 自然關閉 App，transaction 狀態為 `Committed`；328 個 payload、candidate archive、previous 完整舊樹、App 與 maintenance Updater 版本、Windows 登錄及開始選單均核對相符。App 已重新啟動並正常回應，未強制終止程序或產生更新警告；離線 installed manifest 的 source／release sequence 依設計為 null，不冒充線上更新結果。

人工 UI、無障礙、首次 Windows 提示與五個 provider 的完整真人登入／用量驗收暫緩，不列為通過，也不作本輪文件及自動檢查的前置條件。Copilot 的舊版診斷及先前修正版畫面觀察範圍見 [1.0.3 實測表](docs/CLI_COMPATIBILITY.md#103-正式版)。

本版本尚未執行乾淨 Windows／VM 安裝、解除安裝／復原、正式 HTTPS 更新與反降級及 Updater 自更新；這些項目維持暫緩。已完成的匿名下載與簽章 smoke 不代替完整線上更新驗收；候選 feed 的 sequence 1017 與 workflow 驗簽也不能代替線上更新驗收。本次成品掃描與安裝驗證未讀取帳號設定或 credentials，也未執行 provider 驗收；既有帳號與設定的保留尚未另做真人驗收。

### 1.0.2 歷史候選

`1.0.2 / sequence 1016` 私有候選（[source `ea92eda861a80894b1d8c4d12d0cffd655ff92a7`](https://github.com/Sokaka/ai-usage-dashboard/commit/ea92eda861a80894b1d8c4d12d0cffd655ff92a7)）未公開，Release 保持 draft／prerelease；變更與下載檔案資訊見 [1.0.2 版本說明](docs/releases/1.0.2.md)。以下僅適用於該版成品，不代替 `1.0.3` 驗收。

- [x] [候選 CI](https://github.com/Sokaka/ai-usage-dashboard/actions/runs/34795351636) 的原始 attempt 1 通過歷史隱私檢查、restore、建置與完整測試：4,027 passed／0 failed／0 skipped，production line coverage 75.75%（51,006／67,332），門檻維持 70%。
- [x] 同 source、原 build run／attempt 的六件成品已簽署、上傳及凍結；下載 bytes、asset ID、size、SHA256、build artifact 與原始 freeze 相符。下載的 feed 另以 pinned production 公鑰完成本機驗簽。
- [x] 既有 Windows 主機由 `.2` 驗證版以凍結 Updater／ZIP 執行 `apply-local` 換成 `1.0.2`；328 個檔案、App／maintenance 版本、Windows 登錄與開始選單捷徑核對通過，安全退出及重新啟動成功，未回報更新警告，previous 備份相符。
- [x] App、Setup、Updater、ClaudeCapture、AGYCapture、已安裝 App 與 maintenance Updater 共七組實際離線 license exports 通過；元件、文件、版本及原文 hashes 已核對，acceptance digest 格式有效。這不包含條款接受／拒絕的互動驗收。
- [x] App／Setup／ClaudeCapture 的三組 dependency profiles 核對通過；App／Setup 的 runtimeconfig 與 deps 固定為 .NET 8.0.31，套件沒有隨附第三方 CLI。
- [x] 成品有界靜態隱私檢查通過：六件成品與全部 328 ZIP entries，0 findings。可讀文字、檔名及可擷取的 binary strings 已檢查；未解壓 EXE 內嵌壓縮內容或做圖片 OCR，不代表已證明完全沒有敏感資料。

人工 UI、無障礙、首次 Windows 提示與五個 provider 的完整真人登入／用量驗收暫緩。Copilot 已另做部分受控診斷，quota 成功、訂閱解析失敗；逐平台結果見 [1.0.2 歷史實測表](docs/CLI_COMPATIBILITY.md#102-候選版)。

此歷史候選未執行乾淨 Windows／VM 安裝、解除安裝／復原、正式 HTTPS 更新與反降級、Updater 自更新及匿名下載驗收。本機 `apply-local` 的 installed manifest 為 null sequence；候選 feed 的 sequence 1016 與簽章核對不代替線上更新驗收。安裝工具沒有讀取帳號設定或 credentials，設定保留未另做真人驗收。

### 1.0.1 歷史候選

`1.0.1 / sequence 1015` 私有候選（[source `d639a8ad7cc1956c7628ae07037051859e25fe53`](https://github.com/Sokaka/ai-usage-dashboard/commit/d639a8ad7cc1956c7628ae07037051859e25fe53)）包含 Claude `/usage` 回應相容性、Antigravity 程序路徑查詢與防毒阻擋提示修正。以下僅適用於該版成品，不代替後續候選驗收。

- [x] Source CI 與候選 CI 均完成建置及完整測試，各 3,913／3,913 通過，0 failed／0 skipped；production line coverage 分別為 75.63%／75.65%。
- [x] 同 source、原 build run／attempt 的六件成品已完成簽署、上傳及 workflow freeze。下載回讀的 bytes、asset ID、size 與 SHA256 均與 build artifact、freeze 相符，未重建或替換成品。
- [x] 候選 workflow 的簽署與驗簽通過；下載回讀僅核對 bytes、來源與 metadata，未另做密碼學驗簽。
- [x] 既有 Windows 測試 profile、自訂 install roots 的離線驗收通過 13／13：下列六組授權匯出，以及 `apply-local` 全新安裝、錯誤 checksum 拒絕、reapply、`1.0.0` 安裝、null sequence 離線升級至 `1.0.1`、兩次 custom uninstall。
- [x] App、Setup、Updater、ClaudeCapture、AGYCapture 與安裝後 App 共六組實際離線 license exports 通過；components、documents、原文 hashes 與 acceptance digest 均完整核對。這六組不包含 maintenance Updater。
- [x] reapply 與解除安裝保留測試用未知檔案；兩次解除安裝均保留 synthetic user-data sentinel，驗收後只移除本次建立的 sentinel。驗收排程已刪除，本次產品程序剩餘 0；VM 維持 Running、網路斷線，7 個 checkpoints 未變。
- [x] 有界靜態公開面檢查通過：六件成品、ZIP 的 500 files（共 502 entries，含兩個目錄）及本次三份文件，0 findings。檢查可讀文字、檔名與 binary strings；未解壓 EXE 內嵌內容或做圖片 OCR，不代表已證明完全沒有敏感資料。共用規則與限制見 [CI 敏感資訊檢查](docs/CI_PRIVACY.md)。

`1.0.1` 未公開，正式網址的匿名下載、安裝及線上驗收未執行。

本候選尚未驗證乾淨 OS／profile、正式 HTTPS 更新與反降級政策、canonical 登錄／maintenance 清理、Updater 自更新，以及真人 UI／provider 登入與用量。離線 `apply-local` 不代替上述範圍；逐平台狀態見 [CLI 相容性](docs/CLI_COMPATIBILITY.md#101-候選版)。

### 1.0.0 歷史候選

以下結果只適用於 `1.0.0 / sequence 1014`、source `8eb14fe`，不代替 `1.0.1` 的成品驗收。

- [x] `1.0.0 / sequence 1014` 的私有 Windows 驗收通過 27/27：全新情境 17 案、舊版 null sequence 與 sequence 24 銜接各 5 案。
- [x] App、Setup、Updater、Claude capture、AGY capture 與 maintenance Updater 共六組實際離線 license exports 通過；文件集合、原文 hashes 與 acceptance digest 均已核對。
- [x] 驗證候選首次拒絕／接受、非互動拒絕、浮窗／鍵盤／系統匣正常退出，以及 HTTPS 安裝、already-current、錯誤簽章／未知 signer／較低 sequence 拒絕。
- [x] 兩種舊版均由原入口自然建立後升級；三個安裝情境的解除安裝均自然 exit 0，maintenance 清理及使用者資料保留通過。卸載使用 debugger 觀測，會改變時序。
- [x] 同 source、原 build run／attempt 的原六件成品已建立獨立正式 freeze，沒有重建、重簽或替換。原候選 workflow 仍為 failure，原 workflow freeze 缺失；獨立驗收不改寫該結果。
- [x] 原候選指定範圍的 source、歷史、logs／artifacts、圖片與 binaries 公開面掃描已完成；不宣稱涵蓋未知外部分發或證明所有敏感內容都不存在。
- [x] 已登錄同候選 Copilot CLI `1.0.79`／SDK `1.0.11` 的手動實測：8 項測試者回報符合、3 項部分證據。範圍與未量測細項見 [CLI 相容性](docs/CLI_COMPATIBILITY.md#github-copilot-手動實測範圍)；其餘平台與版本沒有因此改列通過。

## 已實作的產品範圍

- Windows WPF 浮窗是唯一主介面，提供帳號管理、排序、連接、用量查看、主題、系統匣及錯誤恢復。
- 設定、偏好與用量快取保存在目前 Windows 使用者範圍，具原子寫入、備份、匯入／匯出及中斷復原。
- 每張卡片有獨立快取與刷新狀態；登入失效、暫時故障、本機工具問題分別提示。背景查詢不自行觸發登入。
- 各服務依支援範圍檢查官方 CLI 來源並隔離帳號；不能確認程序或檔案安全時停止後續操作。token 不寫入帳號設定、用量快取、設定匯出或診斷。

| 服務 | 目前支援與限制 |
| --- | --- |
| Claude | 以未修改的官方 Claude Code、每卡獨立設定與官方登入查詢 `/usage`；仍屬實驗性，回傳後檢查 turn／token／cost 為零。 |
| Codex | 以官方 `app-server` 查詢帳號及多個 rate-limit 區間；每卡隔離登入與 state，仍須持續驗證上游格式相容。 |
| Grok | 以官方 Grok Build CLI 的 auth／billing 支援多帳號、重複帳號拒絕與連接復原；仍屬實驗性。 |
| GitHub Copilot | `1.0.3` 使用固定版本的官方 SDK 與本機官方 CLI，並相容新舊訂閱回應；只支援不同的 `github.com` 帳號，登入資料按卡片存於 Windows Credential Manager，不把 organization／subscription 拆成不同帳號。 |
| Antigravity／AGY | 只使用 user-installed、unmodified 官方 CLI `/usage`；每位 Windows 使用者只允許一張卡片，回傳後檢查 turn／token 為零。設定 UI 在 App process 內，不安裝 status line 或附帶 AGY Setup／Capture EXE。 |

## 候選與歷史驗證的界線

五 provider 登入／帳號隔離、較完整的人工 UI、固定 Windows 18 案與 50 案中斷復原已有歷史驗證；`1.0.1` 至 `1.0.8` 都沒有整套重跑。歷次 SDK／runtime 與成品 bytes 的變更仍須按適用性評估，歷史 PASS 不能改標為本版結果。

後續版本更新、Updater 自更新、HTTPS→HTTP 降級拒絕、磁碟不足／reapply、持久化與 rename 邊界中斷、再次復原失敗等範圍，仍按既有 source 機制證據與適用性評估處理。`1.0.0` 的 27 案不代表已重驗這些範圍，也不代表已涵蓋全部非 canonical 安裝、未知檔案或 Windows 政策環境。

以下保留長期待辦及未涵蓋的驗證範圍；不把它們一律列為本次必須重跑的門檻，本次不安排人工補測。每次交付依 [發布流程](RELEASING.md) 判斷必要補驗範圍。

## 尚待工作

### 服務相容性與額外帳號

- [ ] 將服務限流資訊接入共用暫停重試機制，包含 Codex 的 `429`／`Retry-After`；目前其他可重試錯誤已有逐步延長等待。
- [ ] 持續驗證用量查詢不建立模型 turn 或發送 prompt。受控帳號與回傳後零 turn／token／cost 檢查不能事前保證零推論用量，也不能撤回已產生的費用。
- [ ] 確認 Claude Pro／Max／Team 是否提供穩定、有版本的帳號用量 API；目前 `/usage` 仍是面向使用者的文字格式。
- [ ] 補測額外 Claude、ChatGPT 與第二個 Grok 真實授權帳號，涵蓋切換、重新登入、重啟、逐張更新、拒絕重複帳號，以及移除一張後其他卡片仍可用。
- [ ] 補齊 Copilot 的重複帳號拒絕、`401` 後重新連接及移除後底層登入資料清理查核，並持續確認 AGY 官方 print 連接與資料顯示。
- [ ] 等 AGY 有可靠的每帳號識別或 profile 後，再實作及驗證多帳號隔離；目前維持單一卡片。
- [ ] 若介面需要 token activity，再評估 Codex `account/usage/read`，不得與 rate-limit quota 混用。

### 介面、安裝與長期使用

- [ ] 以代表性多帳號資料量量測長時間 CPU、記憶體、程序數、每卡磁碟用量與實際清理時間。
- [ ] 補齊人工 UI 與無障礙驗證：系統匣顯示／隱藏／雙擊、舊偏好、帳號編輯與連接、對話框前景、匯入／還原、排隊更新、螢幕閱讀器及 High Contrast；`1.0.0` 的基本鍵盤觀測不代表整組已驗。
- [ ] 完整核對標準安裝版的實際登出／登入啟動、浮窗／Tray 偏好與不搶焦點；Windows 停用／重新啟用後行為，以及已有程序時 `--startup` 安靜結束。
- [ ] 核對更新保留已登錄、未登錄與 Windows 停用狀態；卸載只移除完全相符的 `HKCU Run`，衝突值保留並提示。
- [ ] 保留未涵蓋的條款版本變更、App 內 AGY setup 互動、Claude helper、委派入口、缺件拒絕與非 canonical 安裝驗證；不得由既有 export PASS 推論全部入口已驗。
- [ ] 在無 SDK、原始碼與舊資料的非開發 Windows 環境補齊所有服務首次連接、重啟、更新及回復驗證；既有候選 VM 的安裝測試沒有登入 provider。
- [ ] 依發布範圍補驗成品竄改、金鑰更替／遺失／洩漏演練及首次 Windows 提示；feed 簽章拒絕測試不涵蓋全部情境。

### 維護

- [ ] 逐步拆分 App 組裝、生命週期與流程控制責任，維持小步調整。
- [ ] 在 .NET 8 於 2026-11-10 結束支援前完成受支援 runtime 遷移與封裝回歸；到期後不繼續散發目前的 .NET 8 package。每次候選仍需刷新 patch／advisory 檢查。
- [ ] 將測試用 xUnit v2 遷移至 xUnit v3；它不進入正式 App 安裝包，與產品 runtime 的交付風險分開處理。

## 授權、信任與公開限制

自有 source 採 MIT，第三方元件保留各自原約與 notices。授權文件交付、使用者同意和技術驗收通過不等於供應商許可。Microsoft publisher 義務仍由發行者承擔，不能透過使用者按同意轉嫁。

Claude／AGY 的個案條款適用性仍未定論；維持官方未修改 CLI、使用者自己的登入與直接計費，不代管 session token、不代付或轉售用量。公開決策依 [RELEASING.md](RELEASING.md#最後公開決策)，不另設個別廠商回函門檻。

正式 feed RSA key 的建立、Google Drive 加密備份及同機還原已完成；未實測跨機還原，也不是離線媒體。首發不購買 Authenticode 憑證，feed 驗簽不等於 EXE 發行者簽章，Windows 提示或封鎖限制仍保留。保管方式見 [簽章私鑰保管](docs/SIGNING_KEY_CUSTODY.md)，支援範圍見 [Windows 分發手冊](INTERNAL_DISTRIBUTION.md)。
