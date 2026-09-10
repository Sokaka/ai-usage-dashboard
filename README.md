# AI Usage Dashboard

[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)](#環境需求)
[![自有程式碼採 MIT 授權](https://img.shields.io/badge/Code_License-MIT-green)](LICENSE)
[![尚未正式公開](https://img.shields.io/badge/Status-Unreleased-orange)](#目前版本)

AI Usage 是 Windows 桌面工具，可在同一個浮窗查看多個 AI 服務的訂閱用量。

## 目前版本

`1.0.0` 尚未正式公開。正式版本公開後，可從 [GitHub Releases](https://github.com/Sokaka/ai-usage-dashboard/releases)下載。

## 支援平台

| 平台 | 使用前準備 | 帳號卡片 |
| --- | --- | --- |
| Claude | 安裝官方 Claude Code | 多帳號；同帳號依不同組織分卡 |
| Codex | 安裝官方 Codex CLI | 多帳號；同帳號依不同工作區分卡 |
| GitHub Copilot | 安裝包隨附 Copilot 所需工具 | 多帳號；同帳號限一張卡片 |
| Grok | 從 xAI 官方來源安裝 Grok Build CLI 至預設位置 | 多帳號；同帳號限一張卡片 |
| Antigravity | 安裝官方 Antigravity CLI | 限一個帳號 |

各工具的支援版本、帳號需求與安裝方法請見[使用說明](使用說明.md)。

## 主要功能

- **用量**：查看已用／剩餘額度與重置時間，自動或手動更新；讀取失敗時保留上次資料。
- **浮窗**：調整卡片順序、主題與位置；可收合或隱藏，再從系統匣開啟。
- **設定移轉**：匯入或匯出帳號卡片與顯示設定；換機後須重新連接帳號。

## 環境需求

- 仍在 Microsoft 支援範圍內的 Windows x64 電腦，以及網路連線。
- 安裝包包含所需的 .NET 執行環境，不必另外安裝。

## 使用與限制

1. 從正式 Release 取得 Updater 或完整 ZIP。
2. 依[安裝與第一次啟動](使用說明.md#安裝與第一次啟動)操作，閱讀並接受適用條款。
3. 新增帳號，透過服務的官方登入流程完成連接。

使用前請留意：

- **相容性**：五項服務串接仍屬實驗性功能，官方登入或用量格式變更可能影響讀取。
- **Claude 費用**：查詢可能增加用量或費用，請先閱讀[Claude 注意事項](使用說明.md#連接-claude-前必讀)。
- **本機資料**：帳號設定與上次用量保存在目前 Windows 使用者的電腦中。
- **匯出隱私**：匯出檔不含密碼或 token，但可能含電子郵件及暱稱，請存放在可信任位置。
- **Windows 安全**：自有 EXE 尚無 Authenticode 簽章，可能提示或受系統政策阻擋；feed 簽章不提供 Windows 已驗證發布者身分。不要關閉 SmartScreen 或安全防護。
- **服務條款**：各服務仍受供應商條款約束；Claude／AGY 的整合適用性尚未確認，詳見[發布限制](RELEASING.md#最後公開決策)。
- **舊版升級**：舊內部版本需手動升級一次，請使用原 Windows 使用者與原安裝位置，依[舊版升級說明](使用說明.md#舊-internal-安裝銜接)操作。

## 相關文件

| 需求 | 文件 |
| --- | --- |
| 一般使用、設定與問題排除 | [使用說明](使用說明.md) |
| 架構、資料保存與服務串接 | [技術總覽](docs/TECHNICAL_OVERVIEW.md) |
| 功能與驗收狀態 | [實作檢查清單](IMPLEMENTATION_CHECKLIST.md) |
| 安裝、更新、復原與解除安裝 | [Windows 分發與支援手冊](INTERNAL_DISTRIBUTION.md) |
| 版本發布與維護要求 | [發布流程](RELEASING.md) |

## 專案與授權

- **作者**：Sokaka 的個人作品，與公司或各服務供應商無隸屬關係。
- **授權**：自有程式碼採 [MIT](LICENSE)，第三方元件沿用[各自的授權與 notices](third-party-notices/component-manifest.json)。
- **服務名稱**：僅用於說明相容性。
