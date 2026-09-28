namespace AiUsageDashboard.Licensing;

public sealed record LegalDisplayLabels(
	string Title,
	string TermsVersionFormat,
	string ScopeFormat,
	string InstallerScope,
	string CodeAndAcceptance,
	string SourceFormat);
