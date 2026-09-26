using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Ardalis.GuardClauses;
using ClrDebug;
using ICSharpCode.Decompiler.Metadata;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;
using SharpDbg.Infrastructure.Debugger.Models;
using ZLinq;

namespace SharpDbg.Infrastructure.Debugger;

// v1 of this class was AI generated, and could definitely do with some cleaning up
public partial class ManagedDebugger
{
	private CorDebug? _corDebug;
	private CorDebugProcess? _process;
	private readonly CorDebugManagedCallback _callbacks;
	private readonly BreakpointManager _breakpointManager;
	private readonly VariableManager _variableManager;
	private readonly FrameReferenceManager _frameReferenceManager;
	private readonly Action<string>? _logger;
	private readonly Dictionary<int, CorDebugThread> _threads = new();
	private readonly Dictionary<CORDB_ADDRESS, ModuleInfo> _modules = new();
	private bool _isAttached;
	private bool _isRemoteAttach;
	private int? _pendingAttachProcessId;
	private int _processId;
	private Process? _launchedProcess;
	private int? _launchedProcessExitCode;
	private int _exitReported;
	private bool _keepOutputReaders;
	public bool BreakOnThrownExceptions { get; set; } = true;
	private bool _justMyCode;
	private AsyncStepper? _asyncStepper;
	private CompiledExpressionInterpreter _expressionInterpreter = null!;

	public event Action<int, string>? OnStopped;
	// ThreadId, FilePath, Line, Column, Reason, BreakpointId
	public event Action<int, string, int, int, string, DecompiledSourceInfo?, int>? OnStopped2;
	public event Action<int>? OnContinued;
	public event Action<int?>? OnExited;
	public event Action? OnUnhandledException;
	public event Action<int, string>? OnThreadStarted;
	public event Action<int, string>? OnThreadExited;
	public event Action<string, string, string>? OnModuleLoaded;
	public event Action<string>? OnOutput;
	public event Action<string, string>? OnTargetOutput;
	public event Action<BreakpointManager.BreakpointInfo>? OnBreakpointChanged;
	public event Func<LaunchInfo, int> SendRunInTerminalRequest = null!;

	public EvalStatus EvalStatus { get; }

	public bool IsProcessAttached => _process is not null;

	public int ProcessId => _processId;

	public bool IsProcessRunning
	{
		get
		{
			if (_process is null)
				return false;
			try
			{
				return _process.IsRunning;
			}
			catch
			{
				return false;
			}
		}
	}

	public ManagedDebugger(Action<string>? logger = null)
	{
		_logger = logger;
		_breakpointManager = new BreakpointManager();
		_variableManager = new VariableManager();
		_frameReferenceManager = new FrameReferenceManager();
		_callbacks = new CorDebugManagedCallback();
		EvalStatus = new EvalStatus();
		_asyncStepper = new AsyncStepper(_modules, _callbacks, this);

		// Subscribe to callback events
		_callbacks.OnAnyEvent += OnAnyEvent;
	}

	private async void OnAnyEvent(object? sender, CorDebugManagedCallbackEventArgs e)
	{
		try
		{
			_logger?.Invoke($"Event: {e.GetType().Name}");
			switch (e)
			{
				case LogMessageCorDebugManagedCallbackEventArgs a: HandleLogMessage(sender, a); break;
				case CreateProcessCorDebugManagedCallbackEventArgs a: HandleProcessCreated(sender, a); break;
				case ExitProcessCorDebugManagedCallbackEventArgs a: HandleProcessExited(sender, a); break;
				case CreateThreadCorDebugManagedCallbackEventArgs a: HandleThreadCreated(sender, a); break;
				case ExitThreadCorDebugManagedCallbackEventArgs a: HandleThreadExited(sender, a); break;
				case LoadModuleCorDebugManagedCallbackEventArgs a: HandleModuleLoaded(sender, a); break;
				case BreakpointCorDebugManagedCallbackEventArgs a: await HandleBreakpoint(sender, a).ConfigureAwait(false); break;
				case StepCompleteCorDebugManagedCallbackEventArgs a: HandleStepComplete(sender, a); break;
				case BreakCorDebugManagedCallbackEventArgs a: HandleBreak(sender, a); break;
				case ExceptionCorDebugManagedCallbackEventArgs a: HandleException(sender, a); break;
				case EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs: break; // don't continue on these, as they are being used for expression evaluation
				default: e.Controller.Continue(false); break;
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error handling event {e.GetType().Name}: {ex}");
		}
	}

	private void HandleLogMessage(object? sender, LogMessageCorDebugManagedCallbackEventArgs logMessageEvent)
	{
		_logger?.Invoke($"Log: {logMessageEvent.Message}");
		OnTargetOutput?.Invoke("debug", logMessageEvent.Message.TrimEnd('\r', '\n'));
		Continue();
	}

	private void ReportProcessExit(int? exitCode)
	{
		if (Interlocked.Exchange(ref _exitReported, 1) == 0)
			OnExited?.Invoke(exitCode);
	}

	/// <summary>
	/// Actually attach to an existing process
	/// </summary>
	private async Task PerformAttach(int processId)
	{
		_logger?.Invoke($"Attaching to process: {processId}");

		// Initialize the debugger
		var dbgshim = new DbgShim(NativeLibrary.Load("dbgshim", typeof(ManagedDebugger).Assembly, null));
		await Task.Run(() =>
		{
			_corDebug = ClrDebugExtensions.Automatic(dbgshim, processId);
			_corDebug.Initialize();
			_corDebug.SetManagedHandler(_callbacks);

			// Attach to the process
			_process = _corDebug.DebugActiveProcess(processId, false);
			_processId = processId;
			_isAttached = true;

			_logger?.Invoke($"Attached to process: {processId}");
			SendAllBreakpointEvents();
		}).ConfigureAwait(false);
	}

	private async Task PerformRemoteAttach(RemoteAttachInfo remoteAttachInfo)
	{
		_logger?.Invoke($"Attaching to remote process on {remoteAttachInfo.Address}:{remoteAttachInfo.Port}");

		var dbgshim = new DbgShim(NativeLibrary.Load("dbgshim", typeof(ManagedDebugger).Assembly, null));
		_corDebug = ClrDebugExtensions.Mobile(dbgshim, remoteAttachInfo);
		_corDebug.SetManagedHandler(_callbacks);
		try
		{
			// It is expected that this does not return a ICorDebugProcess in the remote scenario - it is obtained via the CreateProcess callback instead
			// ClrDebug throws because it does not expect to receive a null pointer
			_ = _corDebug.DebugActiveProcess(0, false);
		} catch { /* */ }

		_logger?.Invoke($"Debugger listening on port {remoteAttachInfo.Port}, awaiting connection from debuggee");
		await Task.Run(SendAllBreakpointEvents).ConfigureAwait(false);
	}

	private void SendAllBreakpointEvents()
	{
		// Send a breakpoint changed event with verified false for every breakpoint, so the IDE can mark the BP as unverified, until it receives our later BP events when we bind them
		foreach (var bp in _breakpointManager.GetAllBreakpoints())
		{
			OnBreakpointChanged?.Invoke(bp);
		}
	}

	private void Continue()
	{
		Guard.Against.Null(_process);
		_process.Continue(false);
	}

	private CorDebugStepper? _stepper;

	/// <summary>
	/// Setup a stepper without continuing execution
	/// </summary>
	internal CorDebugStepper SetupStepper(CorDebugThread thread, AsyncStepper.StepType stepType)
	{
		var frame = thread.ActiveFrame;
		if (frame is not CorDebugILFrame ilFrame) throw new InvalidOperationException("Active frame is not an IL frame");
		if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");

		CorDebugStepper stepper = frame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		//stepper.SetJMC(true);

		if (stepType == AsyncStepper.StepType.StepOut)
		{
			stepper.StepOut();
		}
		else // StepIn or StepOver
		{
			var symbolReader = _modules[frame.Function.Module.BaseAddress].SymbolReader;

			var currentIlOffset = ilFrame.IP.pnOffset;
			var nullableResult = symbolReader?.GetStartAndEndSequencePointIlOffsetsForIlOffset(frame.Function.Token, currentIlOffset);
			if (nullableResult is var (startIlOffset, endIlOffset))
			{
				if (startIlOffset == endIlOffset)
				{
					endIlOffset = frame.Function.ILCode.Size;
				}
				var stepRange = new COR_DEBUG_STEP_RANGE
				{
					startOffset = startIlOffset,
					endOffset = endIlOffset
				};
				var stepIn = stepType is AsyncStepper.StepType.StepIn;
				stepper.StepRange(stepIn, [stepRange], 1);
			}
			else
			{
				var stepIn = stepType is AsyncStepper.StepType.StepIn;
				stepper.Step(stepIn);
			}
		}

		_stepper = stepper;
		return stepper;
	}

	/// <summary>
	/// Try to bind a breakpoint to the actual code using symbol information
	/// </summary>
	private bool TryBindBreakpoint(BreakpointManager.BreakpointInfo bp)
	{
		if (bp.IsIlBreakpoint)
			return TryBindIlBreakpoint(bp);

		try
		{
			if (_process is null) return false;

			// Find a module that contains the source file
			ModuleInfo? targetModule = null;
			SymbolReader.ResolvedBreakpoint? resolved = null;

			foreach (var moduleInfo in _modules.Values)
			{
				if (moduleInfo.SymbolReader is null)
					continue;

				resolved = moduleInfo.SymbolReader.ResolveBreakpoint(bp.FilePath, bp.Line, bp.Column);
				if (resolved is not null)
				{
					targetModule = moduleInfo;
					break;
				}
			}

			if (targetModule is null || resolved is null)
			{
				// No module found with symbols for this file
				bp.Verified = false;
				bp.Message = "The breakpoint will not currently be hit. No symbols have been loaded for this document.";
				_logger?.Invoke($"Breakpoint at {bp.FilePath}:{bp.Line} - no symbols found");
				return false;
			}

			// Get the function from the method token
			var function = targetModule.Module.GetFunctionFromToken(resolved.MethodToken);
			var ilCode = function.ILCode;

			// Create a breakpoint at the resolved IL offset
			var corBreakpoint = ilCode.CreateBreakpoint(resolved.ILOffset);
			corBreakpoint.Activate(true);

			// Update breakpoint info
			bp.CorBreakpoint = corBreakpoint;
			bp.Verified = true;
			bp.Line = resolved.StartLine;
			bp.Column = resolved.StartColumn;
			bp.EndLine = resolved.EndLine;
			bp.EndColumn = resolved.EndColumn;
			bp.ResolvedBreakpointFromPdb = resolved;
			bp.ModuleBaseAddress = targetModule.BaseAddress;
			bp.Message = null;

			_logger?.Invoke($"Breakpoint bound at {bp.FilePath}:{bp.Line} -> resolved to line {resolved.StartLine}, IL offset {resolved.ILOffset} in method 0x{resolved.MethodToken:X}");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error binding breakpoint at {bp.FilePath}:{bp.Line}: {ex.Message}");
			bp.Verified = false;
			bp.Message = $"Error binding breakpoint: {ex.Message}";
			return false;
		}
	}

	/// <summary>
	/// Try to bind an IL-level method breakpoint
	/// </summary>
	private bool TryBindIlBreakpoint(BreakpointManager.BreakpointInfo bp)
	{
		try
		{
			if (_process is null) return false;

			var (targetModule, methodToken) = TryResolveMethodToken(bp.MethodName, bp.ModuleName);
			if (targetModule is null)
			{
				bp.Verified = false;
				bp.Message = "The breakpoint will not currently be hit. The method could not be found.";
				_logger?.Invoke($"IL breakpoint {bp.MethodName}:IL_{bp.IlOffset:X4} - method not found");
				return false;
			}

			// Get the function from the method token
			var function = targetModule.Module.GetFunctionFromToken(methodToken);
			var ilCode = function.ILCode;

			// Create a breakpoint at the IL offset
			var corBreakpoint = ilCode.CreateBreakpoint(bp.IlOffset);
			corBreakpoint.Activate(true);

			// Update breakpoint info
			bp.CorBreakpoint = corBreakpoint;
			bp.Verified = true;
			bp.ModuleBaseAddress = targetModule.BaseAddress;
			bp.ResolvedBreakpointFromPdb = new SymbolReader.ResolvedBreakpoint(
				methodToken,
				bp.IlOffset,
				bp.IlOffset,
				bp.IlOffset,
				0,
				0,
				bp.FilePath);
			bp.Message = null;

			_logger?.Invoke($"IL breakpoint bound at {bp.FilePath}:IL_{bp.IlOffset:X4} -> method 0x{methodToken:X}");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error binding IL breakpoint {bp.FilePath}:IL_{bp.IlOffset:X4}: {ex.Message}");
			bp.Verified = false;
			bp.Message = $"Error binding IL breakpoint: {ex.Message}";
			return false;
		}
	}

	/// <summary>
	/// Try to resolve a method token by name across loaded modules.
	/// </summary>
	private (ModuleInfo? Module, int MethodToken) TryResolveMethodToken(string methodName, string? moduleName)
	{
		var lastDot = methodName.LastIndexOf('.');
		if (lastDot <= 0)
			return (null, 0);

		var requestedType = methodName.Substring(0, lastDot);
		var requestedMethod = methodName.Substring(lastDot + 1);

		foreach (var moduleInfo in _modules.Values)
		{
			if (!string.IsNullOrWhiteSpace(moduleName))
			{
				var candidateName = moduleInfo.ModuleName;
				if (!string.Equals(candidateName, moduleName, StringComparison.OrdinalIgnoreCase)
				    && !string.Equals(Path.GetFileNameWithoutExtension(candidateName), moduleName, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
			}

			var (token, resolved) = TryResolveMethodTokenInModule(moduleInfo, requestedType, requestedMethod);
			if (resolved)
				return (moduleInfo, token);
		}

		return (null, 0);
	}

	/// <summary>
	/// Try to resolve a method token in a specific module by type and method name.
	/// </summary>
	private (int Token, bool Resolved) TryResolveMethodTokenInModule(ModuleInfo moduleInfo, string requestedType, string requestedMethod)
	{
		var assemblyPath = moduleInfo.ModulePath;
		if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
			return (0, false);

		try
		{
			using var file = new ICSharpCode.Decompiler.Metadata.PEFile(assemblyPath, PEStreamOptions.PrefetchMetadata);
			var reader = file.Metadata;
			var comparer = StringComparison.OrdinalIgnoreCase;

			foreach (var typeHandle in reader.TypeDefinitions)
			{
				var typeDef = reader.GetTypeDefinition(typeHandle);
				var typeName = reader.GetString(typeDef.Name);
				var ns = reader.GetString(typeDef.Namespace);
				var fullTypeName = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

				if (!string.Equals(fullTypeName, requestedType, comparer)
				    && !string.Equals(typeName, requestedType, comparer))
				{
					continue;
				}

				foreach (var methodHandle in typeDef.GetMethods())
				{
					var methodDef = reader.GetMethodDefinition(methodHandle);
					var name = reader.GetString(methodDef.Name);
					if (string.Equals(name, requestedMethod, comparer))
					{
						return (MetadataTokens.GetToken(methodHandle), true);
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error resolving method token in {assemblyPath}: {ex.Message}");
		}

		return (0, false);
	}

	/// <summary>
	/// Try to bind all pending breakpoints (called when a new module is loaded)
	/// </summary>
	private void TryBindPendingBreakpoints()
	{
		var pendingBreakpoints = _breakpointManager.GetPendingBreakpoints();

		foreach (var bp in pendingBreakpoints)
		{
			if (TryBindBreakpoint(bp))
			{
				// Notify that the breakpoint changed (became verified)
				OnBreakpointChanged?.Invoke(bp);
			}
		}
	}

	internal CorDebugILFrame GetFrameForThreadIdAndStackDepth(ThreadId threadId, FrameStackDepth stackDepth)
	{
		// We need to re-obtain the IlFrame in case it has been neutered
		var thread = _process!.Threads.Single(s => s.Id == threadId.Value);
		var frame = thread.ActiveChain.Frames[stackDepth.Value];
		if (frame is not CorDebugILFrame ilFrame) throw new InvalidOperationException("Frame is not an IL frame");
		return ilFrame;
	}

	private static string GetFunctionFormattedName(CorDebugFunction function)
	{
		try
		{
			var token = function.Token;
			var module = function.Module;
			var metadataImport = module.GetMetaDataInterface().MetaDataImport;
			var methodName = metadataImport.GetMethodProps(token).szMethod;

			var @class = function.Class;
			var classToken = @class.Token;
			var className = metadataImport.GetTypeDefProps(classToken).szTypeDef;

			return $"{Path.GetFileName(module.Name)}!{className}.{methodName}()";
		}
		catch
		{
			return "Unknown";
		}
	}

	/// <summary>
	/// Get a summary of all loaded modules.
	/// </summary>
	public List<(string Name, string Path, string BaseAddress, bool IsUserCode, bool HasSymbols)> GetModules()
	{
		return _modules.Values
			.Select(m => (m.ModuleName, m.ModulePath, m.BaseAddress.ToString(), m.IsUserCode, m.SymbolReader is not null))
			.ToList();
	}

	/// <summary>
	/// Get all source files referenced in the PDBs of user modules.
	/// </summary>
	public List<string> GetSourceFiles()
	{
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var module in _modules.Values.Where(m => m.IsUserCode))
		{
			if (module.SymbolReader is null)
				continue;

			foreach (var file in module.SymbolReader.GetSourceFiles())
			{
				if (IsGeneratedSourcePath(file))
					continue;

				files.Add(file);
			}
		}

		return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool IsGeneratedSourcePath(string path)
	{
		// Skip build-generated files under obj/bin and compiler-generated suffixes.
		var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (segments.Any(s =>
			s.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
			s.Equals("bin", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		var fileName = Path.GetFileName(path);
		return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
	}

	// Not intended to implement IDisposable - it is intended that this is called via Disconnect()
	private void Dispose()
	{
		// Dispose modules, which releases PDB files
		foreach (var moduleInfo in _modules.Values)
		{
			moduleInfo.Dispose();
		}
		_modules.Clear();

		// Deactivate all breakpoints
		foreach (var bp in _breakpointManager.GetAllBreakpoints().Where(b => b.CorBreakpoint is not null))
		{
			var hResult = bp.CorBreakpoint!.TryActivate(false);
			if (hResult is HRESULT.CORDBG_E_PROCESS_TERMINATED)
			{
				break;
			}
			if (hResult is not HRESULT.S_OK) _logger?.Invoke($"Failed to deactivate breakpoint during Dispose at {bp.FilePath}:{bp.Line}: {hResult}");
		}
		_breakpointManager.Clear();

		_asyncStepper?.Dispose();
		_asyncStepper = null;
		_stepper = null!;
		_threads.Clear();
		_variableManager.ClearAndTryDisposeHandleValues();
		_frameReferenceManager.Clear();

		// Unsubscribe from callbacks to avoid any further event dispatch
		_callbacks.OnAnyEvent -= OnAnyEvent;

		// Detach from the process
		_process?.TryDetach();
		if (!_keepOutputReaders)
		{
			_launchedProcess?.Dispose();
			_launchedProcess = null;
		}

		_isAttached = false;
		_process = null;
		_processId = 0;
		_corDebug = null;
	}
}

public class EvalStatus
{
	public bool IsRunning { get; set; }
}
