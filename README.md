# AI Usage Dashboard

AI Usage 是 Windows 桌面工具，可在同一個浮窗查看 Claude、Codex、GitHub Copilot、Grok 與 Antigravity 的訂閱用量。

Claude、Codex、GitHub Copilot 與 Grok 可加入多個帳號；Antigravity 目前限一個帳號。帳號設定與上次取得的用量保存在目前 Windows 使用者的電腦中；換機可匯出設定，登入仍須重新連接。

這是 Sokaka 的個人作品，與公司或各服務供應商無隸屬關係。自有程式碼採 [MIT](LICENSE)，第三方元件沿用[各自的授權與 notices](third-party-notices/component-manifest.json)。

## 目前版本

首個 stable 候選 `1.0.0` 已完成私有 Windows 驗收，尚未正式公開；正式下載、安裝與更新驗收會在公開後完成。

正式版本公開後，下載入口為 [GitHub Releases](https://github.com/Sokaka/ai-usage-dashboard/releases)。以 Release 的版本、檔案雜湊及實際驗收紀錄為準，不把開發機測試視為所有環境的保證。

## 環境需求

- 仍在 Microsoft 支援範圍內的 Windows x64 電腦，以及網路連線。
- 安裝包包含 .NET，使用者不必另裝 SDK 或 runtime。
- 使用 Claude、Codex、Grok 或 Antigravity 前先安裝對應的官方 CLI；安裝包附有 GitHub Copilot 所需工具。

各工具的支援版本、帳號需求與安裝方法請見[使用說明](使用說明.md)。

## 使用與限制

從正式 Release 取得 Updater 或完整 ZIP，依[安裝與第一次啟動](使用說明.md#安裝與第一次啟動)閱讀並接受適用條款，再新增帳號，透過服務的官方登入流程完成連接。

- 五個 provider 的串接仍屬實驗性功能；官方登入或用量格式變更可能影響讀取。
- Claude 查詢可能增加用量或費用，請先閱讀[Claude 注意事項](使用說明.md#連接-claude-前必讀)。
- Claude 同一帳號可依不同組織建立卡片；Codex 可依不同工作區建立卡片；Copilot 與 Grok 不可重複加入同一帳號。
- 匯出檔不含密碼或 token，但可能含電子郵件及暱稱，請存放在可信任位置。
- 自有 Windows EXE 尚無 Authenticode 簽章，可能出現安全提示或依系統政策阻擋。feed 簽章不提供 Windows 已驗證發布者身分；不要關閉 SmartScreen 或安全防護。
- 各服務仍受供應商條款約束；Claude／AGY 的整合適用性尚未確認，詳見[發布限制](RELEASING.md#最後公開決策)。
- 舊 internal 安裝需要以新 stable Updater 在原 Windows 使用者、原安裝路徑手動銜接一次。完整驗收狀態見 Release；只修改 `--feed-url` 不會改變舊 Updater 的 channel。

## 維護與開發

| 需求 | 文件 |
| --- | --- |
| 一般使用、設定與問題排除 | [使用說明](使用說明.md) |
| 架構、資料保存與服務串接 | [技術總覽](docs/TECHNICAL_OVERVIEW.md) |
| 功能與驗收狀態 | [實作檢查清單](IMPLEMENTATION_CHECKLIST.md) |
| 安裝、更新、復原與解除安裝 | [Windows 分發與支援手冊](INTERNAL_DISTRIBUTION.md) |
| 建立、驗收與公開同一候選成品 | [發布流程](RELEASING.md) |
| Antigravity 維護工具 | [Antigravity Spike](tools/AiUsageDashboard.AntigravitySpike/README.md) |

專案以 `global.json` 固定 SDK，以 `Directory.Build.props` 啟用 transitive NuGet audit。App 是 WPF；完整測試與成品驗收需要 Windows。正式封裝要求有效 source SHA 與乾淨工作樹；沒有 Git 歷史的原始碼匯出目錄可建置與測試，不能產生符合正式發布契約的候選包。

icon 由使用者請助手生成，實際生成工具與 receipt 未知；此說明不主張已證明排他權利。服務名稱用於說明相容性，第三方的 copyright、license 與 notices 均保留。
