using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

[Target(
	BuildTarget.TestCoreConsole,
	BuildTarget.Build
)]
public static class TestCoreConsole
{
	public static async Task OnExecute(BuildContext context)
	{
		context.BuildStep("Running .NET Core tests (via Console runner)");

		Directory.CreateDirectory(context.TestOutputFolder);

		// v3 (default bitness)
		var netCoreSubpath = Path.Combine("bin", context.ConfigurationText, "net6");
		var v3OutputFileName = Path.Combine(context.TestOutputFolder, "xunit.v3.tests-netcoreapp");
		var v3TestDlls =
			Directory
				.GetFiles(context.BaseFolder, "xunit.v3.*.tests.dll", SearchOption.AllDirectories)
				.Where(x => x.Contains(netCoreSubpath));

#if false
		await context.Exec(context.ConsoleRunnerExe, $"\"{string.Join("\" \"", v3TestDlls)}\" {context.TestFlagsParallel}-xml \"{v3OutputFileName}.xml\" -html \"{v3OutputFileName}.html\" -trx \"{v3OutputFileName}.trx\"");
#else
		foreach (var v3TestDll in v3TestDlls.OrderBy(x => x))
		{
			var fileName = Path.GetFileName(v3TestDll);
			var folder = Path.GetDirectoryName(v3TestDll);
			var outputFileName = Path.Combine(context.TestOutputFolder, Path.GetFileNameWithoutExtension(v3TestDll) + "-" + Path.GetFileName(folder));

			await context.Exec("dotnet", $"exec {fileName} {context.TestFlagsParallel}-preenumeratetheories -xml \"{outputFileName}.xml\" -html \"{outputFileName}.html\" -trx \"{outputFileName}.trx\"", workingDirectory: folder);
		}
#endif

		// Only run 32-bit .NET Core tests on Windows
		if (context.NeedMono)
			return;

		// Only run 32-bit .NET Core tests if 32-bit .NET Core is installed
		var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
		if (programFilesX86 == null)
			return;

		var x86Dotnet = Path.Combine(programFilesX86, "dotnet", "dotnet.exe");
		if (!File.Exists(x86Dotnet))
			return;

		// v3 (forced 32-bit)
		var netCore32Subpath = Path.Combine("bin", context.ConfigurationText + "_x86", "net6");
		var v3x86TestDlls =
			Directory
				.GetFiles(context.BaseFolder, "xunit.v3.*.tests.x86.dll", SearchOption.AllDirectories)
				.Where(x => x.Contains(netCore32Subpath));

#if false
		await context.Exec(context.ConsoleRunnerExe, $"\"{string.Join("\" \"", v3x86TestDlls)}\" {context.TestFlagsParallel}-xml \"{v3OutputFileName}-x86.xml\" -html \"{v3OutputFileName}-x86.html\" -trx \"{v3OutputFileName}-x86.trx\"");
#else
		foreach (var v3x86TestDll in v3x86TestDlls.OrderBy(x => x))
		{
			var fileName = Path.GetFileName(v3x86TestDll);
			var folder = Path.GetDirectoryName(v3x86TestDll);
			var outputFileName = Path.Combine(context.TestOutputFolder, Path.GetFileNameWithoutExtension(v3x86TestDll) + "-" + Path.GetFileName(folder) + "-x86");

			await context.Exec(x86Dotnet, $"exec {fileName} {context.TestFlagsParallel}-preenumeratetheories -xml \"{outputFileName}.xml\" -html \"{outputFileName}.html\" -trx \"{outputFileName}.trx\"", workingDirectory: folder);
		}
#endif
	}
}
