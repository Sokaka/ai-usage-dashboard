# AGENTS.md

本文件適用於整個 repository，供 coding agent 與開發者使用。AI Usage Dashboard 是 Windows x64 的 .NET／WPF 桌面程式。

## 文件入口

| 工作 | 先讀 |
| --- | --- |
| 使用者功能與操作 | [使用說明](使用說明.md) |
| 架構、帳號資料與平台串接 | [技術總覽](docs/TECHNICAL_OVERVIEW.md) |
| 資料格式、CLI 與跨版升級 | [升級與相容性原則](docs/COMPATIBILITY_POLICY.md)及 [CLI 相容性](docs/CLI_COMPATIBILITY.md) |
| 安裝、復原與發布 | [分發手冊](INTERNAL_DISTRIBUTION.md)、[發布流程](RELEASING.md) |
| 已完成與尚待驗證項目 | [實作與驗證清單](IMPLEMENTATION_CHECKLIST.md) |

## 開發要求

- 以最小改動完成任務，沿用現有分層、資料存取、程序執行與測試方式；避免順手重構。
- C# 沿用檔案慣例：Allman braces、tab 縮排、明確 access modifiers、nullable 檢查；型別與成員命名依周邊程式碼，不任意更名持久化或協定欄位。
- 一般註解用繁體中文，保留必要的理由、限制與跨系統契約；機制細節放文件。
- 錯誤須保留原因及可處理的情境，不靜默忽略。非同步工作須觀察結果，資源與子程序須有完整清理路徑。
- 修改產品行為時同步更新相關文件；使用者文件描述現行產品，開發過程與私人驗證紀錄不放入一般說明。

## 相容性與安全

- 後續開發必須遵循[升級與相容性原則](docs/COMPATIBILITY_POLICY.md)：優先保留舊資料、設定、有效連接及仍安全相容的 CLI，減少升級時的手動操作。
- 修改持久化格式、登入綁定、provider、CLI／SDK 依賴或 Updater 時，須核對既有使用者的升級路徑並提供相關驗證；必要的中斷相容變更須附理由、影響與遷移／復原方案。
- 不以相容性為由放寬來源、簽章、帳號隔離、協定或用量安全檢查；不匯入瀏覽器 cookie、不共用不同卡片的憑證。
- token、私鑰、真實帳號資料與原始用量輸出不進 Git、成品或公開 logs。一般測試使用合成資料，不讀個人登入狀態。
- 真實 CLI 登入／用量查詢依[受控驗收流程](INTERNAL_DISTRIBUTION.md#cli-相容性維護)執行，先確認已授權的平台、操作與次數；Claude 查詢可能產生用量或費用。

## 建置與驗證

- 使用 Windows x64 與 [global.json](global.json) 固定的 SDK。還原、Release build、測試及 coverage 檢查方式以 [Windows CI](.github/workflows/windows-ci.yml) 為準，不降低警告、依賴 audit 或驗證門檻。
- 日常改動執行受影響的現有測試；相容性行為變更須補去識別的舊格式／舊回應 fixtures 及跨版回歸測試。測試保持可重現，不依賴個人帳號或即時外部回應。
- 新增或修改文件時檢查連結、UTF-8 與隱私；文件改動通常不需要新建只比對文字的測試。
- 回報實際執行的檢查與未驗項目；單元測試、安裝測試與 CLI 實測分開記錄，不將歷史結果套用到新候選。

## Git 與發布

- Git commit、branch、tag、push 及公開發布須有使用者明確指示；一般「繼續」不視為新的 Git 寫入授權。
- 使用 Conventional Commits，type 為英文、簡短主旨為繁體中文，不附 AI attribution；禁止破壞性 Git 操作。
- CI、基礎設施或發布設定的大幅調整先提出方案，取得明確同意後實作。一般文件與程式修正沿用當次授權範圍。
- 正式候選須依 [RELEASING.md](RELEASING.md) 驗證；不得替換已凍結成品、重用同版本發布不同 bytes，或把 push 當成公開 Release 的授權。
