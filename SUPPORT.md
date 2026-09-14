# 支援與問題回報

一般操作問題與功能建議請使用 [GitHub Issues](https://github.com/Sokaka/ai-usage-dashboard/issues/new/choose)。提交前，先查閱[使用說明](使用說明.md#常見問題)與[既有 Issues](https://github.com/Sokaka/ai-usage-dashboard/issues)，CLI 問題另見[相容性表](docs/CLI_COMPATIBILITY.md)。

本專案目前仍為 private，尚未正式公開。現階段 GitHub 入口需要儲存庫存取權；正式公開後供一般使用者回報。AI Usage 是個人維護的實驗性工具，不承諾回覆或修復期限。

## 回報內容

- **AI Usage 版本與安裝方式**：從浮窗或系統匣開啟 **關於 AI Usage**，按 **複製版本**，並註明 Updater 安裝或免安裝 ZIP；若 App 無法開啟，提供安裝檔名或 Windows 已安裝清單中的版本。
- **Windows 版本與組建**：只需系統版本，不需電腦名稱、Windows 使用者名稱或裝置識別碼。
- **受影響的服務與 CLI 版本**：不涉及服務時填「不適用」；無法確認版號時填「未知」，不必為了回報重新登入或查詢用量。
- **重現步驟、預期結果與實際結果**：包含何時開始發生，以及是否在 App／CLI 更新後出現。Claude 查詢可能增加用量或費用，不需為了回報反覆重試。
- **已遮蔽的錯誤文字**：保留錯誤代碼與描述，移除帳號、電子郵件、組織／工作區名稱、使用者路徑與其他個人資訊。若附畫面，先遮蔽同類資訊。

請勿上傳原始 logs、用量輸出、帳號匯出檔、App 資料目錄、密碼、token、授權碼、callback URL 或其他登入資料。初次回報只需要上述摘要；維護者若需要額外證據，會先說明最小範圍與遮蔽方式。

## 安全問題

疑似憑證外洩、帳號隔離失效或更新來源遭竄改等問題，請改讀 [SECURITY.md](SECURITY.md)，不要把漏洞細節放進公開 Issue。
