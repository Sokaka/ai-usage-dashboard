namespace AiUsageDashboard.Licensing;

public static class LegalCallbackGate
{
	public static int CheckAcceptance(
		Func<LegalCatalog> createCatalog,
		Func<LegalAcceptanceStore> createStore)
	{
		ArgumentNullException.ThrowIfNull(createCatalog);
		ArgumentNullException.ThrowIfNull(createStore);
		try
		{
			LegalCatalog catalog = createCatalog();
			return createStore().IsAccepted(catalog)
				? 0
				: LegalCommandLine.AcceptanceRequiredExitCode;
		}
		catch (Exception)
		{
			// callback 協定僅以退出碼表示授權失敗；不輸出 receipt 路徑或例外細節。
			return LegalCommandLine.FailureExitCode;
		}
	}
}
