# AI Usage Dashboard

AI Usage 是 Windows 桌面工具，可在同一個浮窗查看 Claude、Codex、GitHub Copilot、Grok 與 Antigravity 的訂閱用量。

Claude、Codex、GitHub Copilot 與 Grok 可加入多個帳號；Antigravity 目前限一個帳號。帳號設定與上次取得的用量保存在目前 Windows 使用者的電腦中；換機可匯出設定，登入仍須重新連接。

這是 Sokaka 的個人作品，與公司或各服務供應商無隸屬關係。自有程式碼採 [MIT](LICENSE)，第三方元件沿用[各自的授權與 notices](third-party-notices/component-manifest.json)。服務名稱用於說明相容性。

## 目前版本

`1.0.0` 尚未正式公開。正式版本公開後，可從 [GitHub Releases](https://github.com/Sokaka/ai-usage-dashboard/releases)下載。

## 主要功能

- 在同一個浮窗查看各帳號的用量、剩餘額度與重置時間。
- 自動更新用量，也可手動檢查；暫時讀取失敗時保留上次資料。
- 調整卡片順序、主題與浮窗位置；浮窗可收合或隱藏，並可從系統匣重新開啟。
- 匯入或匯出帳號卡片與顯示設定，方便換機後重新連接。

## 環境需求

- 仍在 Microsoft 支援範圍內的 Windows x64 電腦，以及網路連線。
- 安裝包包含所需的 .NET 執行環境，不必另外安裝。
- 使用 Claude、Codex、Grok 或 Antigravity 前先安裝對應的官方 CLI；安裝包附有 GitHub Copilot 所需工具。

各工具的支援版本、帳號需求與安裝方法請見[使用說明](使用說明.md)。

## 使用與限制

從正式 Release 取得 Updater 或完整 ZIP，依[安裝與第一次啟動](使用說明.md#安裝與第一次啟動)閱讀並接受適用條款，再新增帳號，透過服務的官方登入流程完成連接。

- 五項服務的串接仍屬實驗性功能；官方登入或用量格式變更可能影響讀取。
- Claude 查詢可能增加用量或費用，請先閱讀[Claude 注意事項](使用說明.md#連接-claude-前必讀)。
- Claude 同一帳號可依不同組織建立卡片；Codex 可依不同工作區建立卡片；Copilot 與 Grok 不可重複加入同一帳號。
- 匯出檔不含密碼或 token，但可能含電子郵件及暱稱，請存放在可信任位置。
- 自有 Windows EXE 尚無 Authenticode 簽章，可能出現安全提示或依系統政策阻擋。feed 簽章不提供 Windows 已驗證發布者身分；不要關閉 SmartScreen 或安全防護。
- 各服務仍受供應商條款約束；Claude／AGY 的整合適用性尚未確認，詳見[發布限制](RELEASING.md#最後公開決策)。
- 舊內部版本需要手動升級一次，請使用原 Windows 使用者與原安裝位置，依[舊版升級說明](使用說明.md#舊-internal-安裝銜接)操作。

## 相關文件

| 需求 | 文件 |
| --- | --- |
| 一般使用、設定與問題排除 | [使用說明](使用說明.md) |
| 架構、資料保存與服務串接 | [技術總覽](docs/TECHNICAL_OVERVIEW.md) |
| 功能與驗收狀態 | [實作檢查清單](IMPLEMENTATION_CHECKLIST.md) |
| 安裝、更新、復原與解除安裝 | [Windows 分發與支援手冊](INTERNAL_DISTRIBUTION.md) |
| 版本發布與維護要求 | [發布流程](RELEASING.md) |
