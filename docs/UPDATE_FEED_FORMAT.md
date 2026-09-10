# 更新 feed 格式與金鑰管理

供維護發布工具與 Updater 的開發者閱讀。線上更新先驗證 feed 的專案簽章，才使用其中的 URL、版本、sequence、大小及 SHA256。正式 Updater 的信任公鑰在編譯時提供；feed 本身不能新增可信鍵。

## Envelope v1

外層是 UTF-8 JSON object，只接受下列五個欄位。缺件、未知欄位、重複欄位、無效簽章或未知 signer 都會拒絕；最大長度為 96 KiB。

| 欄位 | 值 |
| --- | --- |
| `formatVersion` | 整數 `1` |
| `algorithm` | `RSA-PSS-SHA256` |
| `signerKeyId` | 1–64 個 ASCII 英數字、`-` 或 `_` |
| `payload` | canonical payload bytes 的標準 Base64，保留 padding，不含空白 |
| `signature` | RSA-PSS 簽章 bytes 的相同 Base64 編碼 |

簽署的 bytes 依序為 ASCII `AiUsageDashboard.UpdateFeed`、LF、`1`、LF、`RSA-PSS-SHA256`、LF、`signerKeyId`、LF，最後接 payload 的原始 bytes，結尾不另加 newline。固定 domain separator 避免同一金鑰在不同格式間誤用；format 與 algorithm 必須符合常數，key ID 也在受簽內容內。

RSA 只接受 3072 或 4096 bits；hash 為 SHA256，PSS 使用 .NET `RSASignaturePadding.Pss`（MGF1 SHA256、salt 長度等於 SHA256 digest 長度）。PSS 的隨機 salt 使重簽產生不同 bytes；正式候選凍結後不重簽。

## Canonical payload

payload 是現有 `UpdateReleaseFeed` schema v1 的完整資料，最大 64 KiB。簽章涵蓋 schema、channel、sequence、minimumUpdaterVersion 與 App／Updater 的所有 artifact 欄位。

signed v1 另要求 `minimumUpdaterVersion` 等於該 feed 的 Updater version。正式 bundle 的 App 與 Updater 使用同一版本，讓安裝新 App 前先更新並接受該候選 installer 交付的條款範圍；不得把 minimum override 調低來略過這一步。

Canonical v1 由 `SignedUpdateFeed.Sign` 的固定 serializer 產生：UTF-8 無 BOM、無額外空白、數值為十進位整數，字串使用 `System.Text.Json` 的預設 JSON escaping。root 順序為 `schemaVersion`、`channel`、`releaseSequence`、`minimumUpdaterVersion`、`package`、`updater`；每個 artifact 的順序為 `artifactId`、`version`、`runtimeIdentifier`、`fileName`、`downloadUrl`、`sizeBytes`、`sha256`、`sourceRevision`。使用專案 signing tool 產生 bytes，勿用其他 JSON formatter 代替。

驗證者先對解碼後的原始 payload bytes 驗簽，再解析 schema、拒絕重複／未知欄位，並以同一 canonical serializer 重新編碼比較。只有 bytes 相符且 channel 符合預期時，才交給線上版本與反降級政策。委派的 Updater 重新取得並驗證 feed，不能沿用未驗證的欄位。

若要改 canonical encoding、演算法或 payload schema，必須設計新的格式版本與客戶端相容策略；不能改 v1 serializer 行為後沿用原格式。

## 公鑰配置與簽署

外部提供的 trust JSON 含 `schemaVersion: 1` 及 `keys` 陣列。每個 key object 只有 `keyId` 和 `publicKeyPem`；後者是 `BEGIN PUBLIC KEY` 的 SPKI PEM。最多 8 個、不允許重複 key ID、不接受私鑰 PEM。`Publish-Updater.ps1` 的 `TrustedKeysFile` 先以 runtime validator 驗證此檔，再透過 `UpdateTrustedKeysFile` 嵌入 Updater。正式 publish 缺少檔案即失敗。一般開發 build 可不放 trust store，但此 build 的線上更新會失敗。

`tools/AiUsageDashboard.FeedSigning` 提供 `validate-trust`、`sign`、`verify`。`sign` 從單獨私鑰檔讀取 signing key；輸出前再以外部 trust store 驗證簽章與 channel，key ID／公私鑰不符時不寫出 feed。`verify` 只在驗證成功後寫出 canonical payload。各命令輸出檔案都不可已存在。

`Publish-UpdateBundle.ps1` 要求 `TrustedKeysFile`、`SigningKeyId`、`SigningPrivateKeyFile`、`ReleaseSequence` 與 `PreviousReleaseSequence`。後兩者都由完成既有分發盤點的維護者提供，工具僅驗證新值較大，不能證明盤點完整。不得使用新 repo 的 run number 當全域序號。

封裝順序：最終 binaries → App ZIP → ZIP／Updater EXE hash 與 size → 正式 URL payload → 簽署並驗證 feed → feed SHA256 sidecar。發布仍是 ZIP、EXE、signed feed 及三份 sidecars，共六件。sidecar 只供完整性核對，沒有獨立的發布者信任效力。

## CI 與私鑰保管

候選 workflow 讀取外部 provision 的 `UPDATE_FEED_TRUST_JSON`、`UPDATE_FEED_SIGNING_KEY_ID` repository variables，以及 `UPDATE_FEED_SIGNING_PRIVATE_KEY_PEM` repository secret。執行前依[發布流程](../RELEASING.md)核對 runner、費用限制與 secret 設定；PR 與一般 CI 不取得私鑰。

簽署只在指定 main SHA 的手動候選流程、測試 gates 通過後執行。私鑰只在該 step 的 runner-owned temp directory 暫存；寫檔後清掉 child-process 環境中的私鑰值，結束時刪除檔案及空目錄。source、成品、diagnostics 與 freeze receipt 都不應包含私鑰。runner 工作目錄與 repository workflow 修改權限必須限於可信維護者；GitHub repository secrets 的可用保護能力須依實際帳號設定核對，不假設已配置付費環境保護。

production key 應由維護者在受控環境外部建立，依[簽章私鑰保管](SIGNING_KEY_CUSTODY.md)保留 Google Drive 加密異地備份，將解密憑證與備份分開管理，並實際驗證重新下載後的還原。先用 disposable test key 演練簽署、驗證、復原與輪替，再 provision 正式 key；不要把測試 key 用於發布。

## 金鑰輪替與失效

1. 正常輪替：用舊 key A 簽署包含 A＋B 公鑰的新 Updater；舊客戶端驗 A 並更新到 bridge 版本。確認目標安裝群已取得 bridge 後，再改用 B 簽署後續 feed。尚未取得 bridge 的 A-only 安裝會拒絕 B，需從正式來源手動取得新版 Updater。
2. 移除 A：以 B 簽署只信任 B 的更新版本，驗收新 key 可用且 A 已拒絕。已分發的 A-only Updater 不會因遠端公告自動撤銷 A。
3. A 私鑰遺失：依[簽章私鑰保管](SIGNING_KEY_CUSTODY.md)從已驗證的受控備份還原；若無可用備份及事先 provision 的替代鍵，走正式來源下載新 Updater 的人工銜接，不降低驗簽要求。
4. A 疑似洩漏：停止使用 A、保留事件與候選證據，提供只信任新鍵的 Updater，通知使用者從正式來源手動更新。持有 A 的攻擊者可以簽署任意 feed；單靠同一 key 簽署撤銷宣告或提高 sequence 不能修復舊客戶端的信任。

自有 feed 簽章不提供 Windows CA 認證的 publisher 身分，也不保證消除 SmartScreen。第一次下載仍依賴正式 repo／HTTPS 與可核對發布資訊。`apply-local` 保留人工選擇可信 ZIP 與 SHA256 sidecar 的離線復原能力；它沒有線上 feed 簽章與相同的線上反降級保護。

自動測試涵蓋受簽欄位竄改、缺少／未知簽章、格式、channel 與 bridge trust policy。建鑰、備份還原、不同版本 Updater 的換鍵及公開 HTTPS 下載，以各自的實際操作紀錄為準；版本狀態見 [README](../README.md#目前版本)。

## 安裝者與條款範圍

signed online 安裝 App 前，執行中的 Updater 必須與 feed.Updater 的版本、size、SHA256 全部相同，包含委派的 `--skip-updater-refresh` 路徑。較新的 Updater 也不能直接安裝另一候選的 App，因為它內嵌的條款清單未必涵蓋該版本；請從正式 Release 取得匹配的 Updater。此檢查不更動人工 `apply-local` 復原政策。

原本互動啟動的 Updater 會以 `--prompt-for-licenses` 將接受新條款的互動意圖交給新版；此旗標不啟用子程序的成功通知。一般非互動命令仍須先接受 exact digest，未接受時回 5；授權文件或紀錄讀取失敗回 4。
