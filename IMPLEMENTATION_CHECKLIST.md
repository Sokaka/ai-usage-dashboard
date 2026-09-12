# 實作與驗證清單

更新日期：2026-09-12

讀者：負責 AI Usage Dashboard 開發、測試與交付的人員。本文只列現況、驗證界線與尚待工作。

一般操作見 [使用說明](使用說明.md)；架構、資料格式與各服務的實作見 [技術總覽](docs/TECHNICAL_OVERVIEW.md)；候選驗收與公開規則見 [RELEASING.md](RELEASING.md)。

## 目前候選

本節記錄既有候選成品的結果；本輪文件修訂尚未納入新的來源與發布檢核。

- [x] `1.0.0 / sequence 1014` 的私有 Windows 驗收通過 27/27：全新情境 17 案、舊版 null sequence 與 sequence 24 銜接各 5 案。
- [x] App、Setup、Updater、Claude capture、AGY capture 與 maintenance Updater 共六組實際離線 license exports 通過；文件集合、原文 hashes 與 acceptance digest 均已核對。
- [x] 驗證候選首次拒絕／接受、非互動拒絕、浮窗／鍵盤／系統匣正常退出，以及 HTTPS 安裝、already-current、錯誤簽章／未知 signer／較低 sequence 拒絕。
- [x] 兩種舊版均由原入口自然建立後升級；三個安裝情境的解除安裝均自然 exit 0，maintenance 清理及使用者資料保留通過。卸載使用 debugger 觀測，會改變時序。
- [x] 同 source、原 build run／attempt 的原六件成品已建立獨立正式 freeze，沒有重建、重簽或替換。原候選 workflow 仍為 failure，原 workflow freeze 缺失；獨立驗收不改寫該結果。
- [x] 原候選指定範圍的 source、歷史、logs／artifacts、圖片與 binaries 公開面掃描已完成；不宣稱涵蓋未知外部分發或證明所有敏感內容都不存在。
- [x] 已登錄同候選 Copilot CLI `1.0.79`／SDK `1.0.11` 的手動實測：8 項本人回報符合、3 項部分證據。範圍與未量測細項見 [CLI 相容性](docs/CLI_COMPATIBILITY.md#github-copilot-手動實測範圍)；其餘平台與版本沒有因此改列通過。
- [ ] 維護者決定是否公開同一候選 Release。
- [ ] 公開後完成原六件匿名下載、hash／size／簽章核對，以及 10 案正式網址驗收。私有測試 feed 的成功不能取代這一步。

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
| GitHub Copilot | 使用官方 SDK 與隨附 CLI；只支援不同的 `github.com` 帳號，登入資料按卡片存於 Windows Credential Manager，不把 organization／subscription 拆成不同帳號。 |
| Antigravity／AGY | 使用 user-installed、unmodified 官方 CLI `/usage`；每位 Windows 使用者只允許一張卡片，保留受限的舊版相容路徑，回傳後檢查 turn／token 為零。 |

## 候選與歷史驗證的界線

五 provider 登入／帳號隔離、較完整的人工 UI、固定 Windows 18 案與 50 案中斷復原已有歷史驗證；本候選沒有整套重跑。SDK／runtime 與成品 bytes 已改變，歷史 PASS 不能改標為本候選結果。

後續版本更新、Updater 自更新、HTTPS→HTTP 降級拒絕、磁碟不足／reapply、持久化與 rename 邊界中斷、再次復原失敗等範圍，仍按既有 source 機制證據與適用性評估處理。本候選的 27 案不代表已重驗這些範圍，也不代表已涵蓋全部非 canonical 安裝、未知檔案或 Windows 政策環境。

以下保留待辦及未涵蓋的驗證範圍；不把它們一律列為本次必須重跑的門檻。每次交付依 [發布流程](RELEASING.md) 判斷必要補驗範圍。

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
- [ ] 補齊人工 UI 與無障礙驗證：系統匣顯示／隱藏／雙擊、舊偏好、帳號編輯與連接、對話框前景、匯入／還原、排隊更新、螢幕閱讀器及 High Contrast；本候選的基本鍵盤觀測不代表整組已驗。
- [ ] 完整核對標準安裝版的實際登出／登入啟動、浮窗／Tray 偏好與不搶焦點；Windows 停用／重新啟用後行為，以及已有程序時 `--startup` 安靜結束。
- [ ] 核對更新保留已登錄、未登錄與 Windows 停用狀態；卸載只移除完全相符的 `HKCU Run`，衝突值保留並提示。
- [ ] 保留未涵蓋的條款版本變更、Setup／helper 互動、委派入口、缺件拒絕與非 canonical 安裝驗證；不得由六組 export PASS 推論全部入口已驗。
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
