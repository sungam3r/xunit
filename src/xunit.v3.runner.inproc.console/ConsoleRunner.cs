using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit.Internal;
using Xunit.Runner.Common;
using Xunit.Runner.v3;
using Xunit.Sdk;
using Xunit.v3;

namespace Xunit.Runner.InProc.SystemConsole;

/// <summary>
/// This class is the entry point for the in-process console-based runner used for
/// xUnit.net v3 test projects.
/// </summary>
public class ConsoleRunner
{
	readonly string[] args;
	volatile bool cancel;
	readonly object consoleLock;
	bool executed = false;
	bool failed;
	IRunnerLogger? logger;
	IReadOnlyList<IRunnerReporter>? runnerReporters;
	readonly Assembly testAssembly;
	readonly TestExecutionSummaries testExecutionSummaries = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="ConsoleRunner"/> class.
	/// </summary>
	/// <param name="args">The arguments passed to the application; typically pulled from the Main method.</param>
	/// <param name="testAssembly">The (optional) assembly to test; defaults to <see cref="Assembly.GetEntryAssembly"/>.</param>
	/// <param name="runnerReporters">The (optional) list of runner reporters.</param>
	/// <param name="consoleLock">The (optional) lock used around all console output to ensure there are no write collisions.</param>
	public ConsoleRunner(
		string[] args,
		Assembly? testAssembly = null,
		IEnumerable<IRunnerReporter>? runnerReporters = null,
		object? consoleLock = null)
	{
		this.args = Guard.ArgumentNotNull(args);
		this.testAssembly = Guard.ArgumentNotNull("testAssembly was null, and Assembly.GetEntryAssembly() returned null; you should pass a non-null value for testAssembly", testAssembly ?? Assembly.GetEntryAssembly(), nameof(testAssembly));
		this.consoleLock = consoleLock ?? new object();
		this.runnerReporters = runnerReporters.CastOrToReadOnlyList();
	}

	/// <summary>
	/// The entry point to begin running tests.
	/// </summary>
	/// <returns>The return value intended to be returned by the Main method.</returns>
	public async ValueTask<int> EntryPoint()
	{
		if (executed)
			throw new InvalidOperationException("The EntryPoint method can only be called once.");

		executed = true;

		var globalInternalDiagnosticMessages = false;
		var noColor = false;

		try
		{
			var commandLine = new CommandLine(testAssembly, args, runnerReporters);

			if (commandLine.HelpRequested)
			{
				PrintHeader();

				Console.WriteLine("Copyright (C) .NET Foundation.");
				Console.WriteLine();
				Console.WriteLine($"usage: [:seed] [path/to/configFile.json] [options] [filters] [reporter] [resultFormat filename [...]]");

				commandLine.PrintUsage();
				return 2;
			}

			var project = commandLine.Parse();

			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

			Console.CancelKeyPress += (sender, e) =>
			{
				if (!cancel)
				{
					Console.WriteLine("Canceling... (Press Ctrl+C again to terminate)");
					cancel = true;
					e.Cancel = true;
				}
			};

			// Validate options that aren't legal with -tcp (runners are validated in CommandLine.ChooseReporter)
			if (project.Configuration.TcpPort.HasValue)
			{
				if (project.Configuration.Output.Count != 0)
					throw new ArgumentException($"cannot specify -{project.Configuration.Output.Keys.First()} when using -tcp");
				if (project.Configuration.DebugOrDefault)
					throw new ArgumentException("cannot specify -debug when using -tcp");
				if (project.Configuration.NoAutoReportersOrDefault)
					throw new ArgumentException("cannot specify -noautoreporters when using -tcp");
				if (project.Configuration.PauseOrDefault)
					throw new ArgumentException("cannot specify -pause when using -tcp");
				if (project.Configuration.WaitOrDefault)
					throw new ArgumentException("cannot specify -wait when using -tcp");
			}

			if (project.Configuration.PauseOrDefault)
			{
				Console.Write("Press any key to start execution...");
				Console.ReadKey(true);
				Console.WriteLine();
			}

			if (project.Configuration.DebugOrDefault)
				Debugger.Launch();

			var globalDiagnosticMessages = project.Assemblies.Any(a => a.Configuration.DiagnosticMessagesOrDefault);
			globalInternalDiagnosticMessages = project.Assemblies.Any(a => a.Configuration.InternalDiagnosticMessagesOrDefault);
			noColor = project.Configuration.NoColorOrDefault;
			logger = new ConsoleRunnerLogger(!noColor, consoleLock);
			var globalDiagnosticMessageSink = ConsoleDiagnosticMessageSink.TryCreate(consoleLock, noColor, globalDiagnosticMessages, globalInternalDiagnosticMessages);
			var shouldReturnFailErrorCode = false;

			if (project.Configuration.TcpPort.HasValue)
			{
				var engineID = Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()?.Location) ?? "<unknown assembly>";
				await using var engine = new TcpExecutionEngine(engineID, project, globalDiagnosticMessageSink);
				await engine.Start();
				await engine.WaitForQuit();
			}
			else
			{
				var reporterMessageHandler = await project.RunnerReporter.CreateMessageHandler(logger, globalDiagnosticMessageSink);

				if (!project.RunnerReporter.ForceNoLogo && !project.Configuration.NoLogoOrDefault)
					PrintHeader();

				var failCount = 0;

				if (project.Configuration.List != null)
					await ListProject(project);
				else
					failCount = await RunProject(project, reporterMessageHandler);

				shouldReturnFailErrorCode = failCount > 0;
			}

			if (cancel)
				return -1073741510;    // 0xC000013A: The application terminated as a result of a CTRL+C

			if (project.Configuration.WaitOrDefault)
			{
				Console.WriteLine();
				Console.Write("Press any key to continue...");
				Console.ReadKey();
				Console.WriteLine();
			}

			return project.Configuration.IgnoreFailures == true || !shouldReturnFailErrorCode ? 0 : 1;
		}
		catch (Exception ex)
		{
			if (!noColor)
				ConsoleHelper.SetForegroundColor(ConsoleColor.Red);

			Console.WriteLine($"error: {ex.Message}");

			if (globalInternalDiagnosticMessages)
			{
				if (!noColor)
					ConsoleHelper.SetForegroundColor(ConsoleColor.DarkGray);

				Console.WriteLine(ex.StackTrace);
			}

			return ex is ArgumentException ? 3 : 4;
		}
		finally
		{
			if (!noColor)
				ConsoleHelper.ResetColor();
		}
	}

	void OnUnhandledException(
		object sender,
		UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
			Console.WriteLine(ex.ToString());
		else
			Console.WriteLine("Error of unknown type thrown in application domain");

		Environment.Exit(1);
	}

	void PrintHeader() =>
		Console.WriteLine($"xUnit.net v3 In-Process Runner v{ThisAssembly.AssemblyInformationalVersion} ({IntPtr.Size * 8}-bit {RuntimeInformation.FrameworkDescription})");

	/// <summary>
	/// Creates a new <see cref="ConsoleRunner"/> instance and runs it via <see cref="EntryPoint"/>.
	/// </summary>
	/// <param name="args">The arguments passed to the application; typically pulled from the Main method.</param>
	/// <param name="testAssembly">The (optional) assembly to test; defaults to <see cref="Assembly.GetEntryAssembly"/>.</param>
	/// <param name="runnerReporters">The (optional) list of runner reporters.</param>
	/// <param name="consoleLock">The (optional) lock used around all console output to ensure there are no write collisions.</param>
	/// <returns>The return value intended to be returned by the Main method.</returns>
	public static ValueTask<int> Run(
		string[] args,
		Assembly? testAssembly = null,
		IEnumerable<IRunnerReporter>? runnerReporters = null,
		object? consoleLock = null) =>
			new ConsoleRunner(args, testAssembly, runnerReporters, consoleLock).EntryPoint();

	async ValueTask ListProject(XunitProject project)
	{
		var (listOption, listFormat) = project.Configuration.List!.Value;
		var testCasesByAssembly = new Dictionary<string, List<_ITestCase>>();

		foreach (var assembly in project.Assemblies)
		{
			var assemblyFileName = Guard.ArgumentNotNull(assembly.AssemblyFileName);

			// Default to false for console runners
			assembly.Configuration.PreEnumerateTheories ??= false;

			// Setup discovery options with command line overrides
			var discoveryOptions = _TestFrameworkOptions.ForDiscovery(assembly.Configuration);

			var noColor = assembly.Project.Configuration.NoColorOrDefault;
			var diagnosticMessages = assembly.Configuration.DiagnosticMessagesOrDefault;
			var internalDiagnosticMessages = assembly.Configuration.InternalDiagnosticMessagesOrDefault;
			var diagnosticMessageSink = ConsoleDiagnosticMessageSink.TryCreate(consoleLock, noColor, diagnosticMessages, internalDiagnosticMessages);

			TestContext.SetForInitialization(diagnosticMessageSink, diagnosticMessages, internalDiagnosticMessages);

			var assemblyInfo = new ReflectionAssemblyInfo(testAssembly);

			await using var disposalTracker = new DisposalTracker();
			var testFramework = ExtensibilityPointFactory.GetTestFramework(assemblyInfo);
			disposalTracker.Add(testFramework);

			// Discover & filter the tests
			var testCases = new List<_ITestCase>();
			var testDiscoverer = testFramework.GetDiscoverer(assemblyInfo);
			await testDiscoverer.Find(testCase => { testCases.Add(testCase); return new(!cancel); }, discoveryOptions);

			var testCasesDiscovered = testCases.Count;
			var filteredTestCases = testCases.Where(assembly.Configuration.Filters.Filter).ToList();

			testCasesByAssembly.Add(assemblyFileName, filteredTestCases);
		}

		ConsoleProjectLister.List(testCasesByAssembly, listOption, listFormat);
	}

	async ValueTask<int> RunProject(
		XunitProject project,
		_IMessageSink reporterMessageHandler)
	{
		XElement? assembliesElement = null;
		var clockTime = Stopwatch.StartNew();
		var xmlTransformers = TransformFactory.GetXmlTransformers(project);
		var needsXml = xmlTransformers.Count > 0;

		if (needsXml)
			assembliesElement = TransformFactory.CreateAssembliesElement();

		var originalWorkingFolder = Directory.GetCurrentDirectory();

		var assembly = project.Assemblies.Single();
		var assemblyElement = await RunProjectAssembly(
			assembly,
			needsXml,
			reporterMessageHandler
		);

		if (assemblyElement != null)
			assembliesElement?.Add(assemblyElement);

		clockTime.Stop();

		testExecutionSummaries.ElapsedClockTime = clockTime.Elapsed;
		reporterMessageHandler.OnMessage(testExecutionSummaries);

		Directory.SetCurrentDirectory(originalWorkingFolder);

		if (assembliesElement != null)
		{
			TransformFactory.FinishAssembliesElement(assembliesElement);
			xmlTransformers.ForEach(transformer => transformer(assembliesElement));
		}

		return failed ? 1 : testExecutionSummaries.SummariesByAssemblyUniqueID.Sum(s => s.Summary.Failed + s.Summary.Errors);
	}

	async ValueTask<XElement?> RunProjectAssembly(
		XunitProjectAssembly assembly,
		bool needsXml,
		_IMessageSink reporterMessageHandler)
	{
		if (cancel)
			return null;

		var assemblyElement = needsXml ? new XElement("assembly") : null;

		try
		{
			// Default to false for console runners
			assembly.Configuration.PreEnumerateTheories ??= false;

			// Setup discovery and execution options with command-line overrides
			var discoveryOptions = _TestFrameworkOptions.ForDiscovery(assembly.Configuration);
			var executionOptions = _TestFrameworkOptions.ForExecution(assembly.Configuration);

			var noColor = assembly.Project.Configuration.NoColorOrDefault;
			var diagnosticMessages = assembly.Configuration.DiagnosticMessagesOrDefault;
			var internalDiagnosticMessages = assembly.Configuration.InternalDiagnosticMessagesOrDefault;
			var diagnosticMessageSink = ConsoleDiagnosticMessageSink.TryCreate(consoleLock, noColor, diagnosticMessages, internalDiagnosticMessages);
			var longRunningSeconds = assembly.Configuration.LongRunningTestSecondsOrDefault;

			TestContext.SetForInitialization(diagnosticMessageSink, diagnosticMessages, internalDiagnosticMessages);

			var assemblyInfo = new ReflectionAssemblyInfo(testAssembly);

			await using var disposalTracker = new DisposalTracker();
			var testFramework = ExtensibilityPointFactory.GetTestFramework(assemblyInfo);
			disposalTracker.Add(testFramework);

			var frontController = new InProcessFrontController(testFramework, assemblyInfo, assembly.ConfigFileName);

			IExecutionSink resultsSink = new DelegatingSummarySink(
				assembly,
				discoveryOptions,
				executionOptions,
				AppDomainOption.NotAvailable,
				shadowCopy: false,
				reporterMessageHandler,
				() => cancel
			);

			if (assemblyElement != null)
				resultsSink = new DelegatingXmlCreationSink(resultsSink, assemblyElement);
			if (longRunningSeconds > 0 && diagnosticMessageSink != null)
				resultsSink = new DelegatingLongRunningTestDetectionSink(resultsSink, TimeSpan.FromSeconds(longRunningSeconds), diagnosticMessageSink);
			if (assembly.Configuration.FailSkipsOrDefault)
				resultsSink = new DelegatingFailSkipSink(resultsSink);

			using (resultsSink)
			{
				await frontController.FindAndRun(resultsSink, discoveryOptions, executionOptions, assembly.Configuration.Filters.Filter);

				testExecutionSummaries.Add(frontController.TestAssemblyUniqueID, resultsSink.ExecutionSummary);

				if (assembly.Configuration.StopOnFailOrDefault && resultsSink.ExecutionSummary.Failed != 0)
				{
					Console.WriteLine("Canceling due to test failure...");
					cancel = true;
				}
			}
		}
		catch (Exception ex)
		{
			failed = true;

			var e = ex;
			while (e != null)
			{
				Console.WriteLine($"{e.GetType().FullName}: {e.Message}");

				if (assembly.Configuration.InternalDiagnosticMessagesOrDefault)
					Console.WriteLine(e.StackTrace);

				e = e.InnerException;
			}
		}

		return assemblyElement;
	}
}
