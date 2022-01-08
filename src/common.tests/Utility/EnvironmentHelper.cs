using System;

static class EnvironmentHelper
{
	static readonly Lazy<bool> isMono = new Lazy<bool>(() => Type.GetType("Mono.Runtime") != null);

	/// <summary>
	/// Returns <c>true</c> if you're currently running in Mono; <c>false</c> if you're running in .NET Framework.
	/// </summary>
	public static bool IsMono => isMono.Value;

	/// <summary>
	/// Returns <c>true</c> if you're currently running on Windows; <c>false</c> if you're running on
	/// non-Windows (like Linux or macOS). (Note: we do this by detecting Mono; this is not normally a good
	/// verification strategy, since you can run Mono on Windows, but in our case we know that we only use
	/// Mono with our unit tests on non-Windows machines. This would be a bad assumption for production code.)
	/// </summary>
	public static bool IsWindows =>
#if NETFRAMEWORK
		!IsMono;
#else
		OperatingSystem.IsWindows();
#endif
}
