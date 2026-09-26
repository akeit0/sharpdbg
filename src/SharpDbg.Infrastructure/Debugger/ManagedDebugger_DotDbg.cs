using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using SharpDbg.Infrastructure.Debugger.Decompilation;

namespace SharpDbg.Infrastructure.Debugger;

public record SharpDbgIlBreakpointRequest(
	string MethodName,
	int IlOffset,
	string? ModuleName = null,
	string? Condition = null,
	string? HitCondition = null
);

public partial class ManagedDebugger
{
	public List<BreakpointManager.BreakpointInfo> SetIlBreakpoints(
		string methodKey,
		SharpDbgIlBreakpointRequest[] breakpoints
	)
	{
		foreach (var existing in _breakpointManager.GetBreakpointsForMethod(methodKey))
		{
			try
			{
				existing.CorBreakpoint?.Activate(false);
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Error deactivating IL breakpoint: {ex.Message}");
			}
		}
		_breakpointManager.ClearBreakpointsForMethod(methodKey);
		var result = new List<BreakpointManager.BreakpointInfo>();
		foreach (var request in breakpoints)
		{
			var breakpoint = _breakpointManager.CreateIlBreakpoint(
				methodKey,
				request.MethodName,
				request.IlOffset,
				request.ModuleName,
				request.Condition,
				request.HitCondition
			);
			if (_process is null)
				breakpoint.Message = "Breakpoint has not been processed by the debugger.";
			else
				TryBindIlBreakpoint(breakpoint);
			result.Add(breakpoint);
		}
		return result;
	}

	private bool TryBindIlBreakpoint(BreakpointManager.BreakpointInfo breakpoint)
	{
		try
		{
			if (_process is null)
				return false;
			var (module, methodToken) = TryResolveMethodToken(breakpoint.MethodName, breakpoint.ModuleName);
			if (module is null)
			{
				breakpoint.Verified = false;
				breakpoint.Message = "The breakpoint will not currently be hit. The method could not be found.";
				return false;
			}
			var function = module.Module.GetFunctionFromToken(methodToken);
			var corBreakpoint = function.ILCode.CreateBreakpoint(breakpoint.IlOffset);
			corBreakpoint.Activate(true);
			breakpoint.CorBreakpoint = corBreakpoint;
			breakpoint.Verified = true;
			breakpoint.ModuleBaseAddress = module.BaseAddress;
			breakpoint.ResolvedBreakpointFromPdb = new ModuleMetadataReader.ResolvedBreakpoint(
				methodToken,
				breakpoint.IlOffset,
				breakpoint.IlOffset,
				breakpoint.IlOffset,
				0,
				0,
				breakpoint.FilePath
			);
			breakpoint.Message = null;
			return true;
		}
		catch (Exception ex)
		{
			_logger?.Invoke(
				$"Error binding IL breakpoint {breakpoint.FilePath}:IL_{breakpoint.IlOffset:X4}: {ex.Message}"
			);
			breakpoint.Verified = false;
			breakpoint.Message = $"Error binding IL breakpoint: {ex.Message}";
			return false;
		}
	}

	private (ModuleInfo? Module, int MethodToken) TryResolveMethodToken(string methodName, string? moduleName)
	{
		var lastDot = methodName.LastIndexOf('.');
		if (lastDot <= 0)
			return (null, 0);
		var typeName = methodName[..lastDot];
		var memberName = methodName[(lastDot + 1)..];
		foreach (var module in _modules.Values)
		{
			if (
				!string.IsNullOrWhiteSpace(moduleName)
				&& !string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(
					Path.GetFileNameWithoutExtension(module.ModuleName),
					moduleName,
					StringComparison.OrdinalIgnoreCase
				)
			)
				continue;
			if (string.IsNullOrEmpty(module.ModulePath) || !File.Exists(module.ModulePath))
				continue;
			try
			{
				using var file = new PEFile(module.ModulePath, PEStreamOptions.PrefetchMetadata);
				var reader = file.Metadata;
				foreach (var typeHandle in reader.TypeDefinitions)
				{
					var type = reader.GetTypeDefinition(typeHandle);
					var name = reader.GetString(type.Name);
					var ns = reader.GetString(type.Namespace);
					var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
					if (
						!string.Equals(fullName, typeName, StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(name, typeName, StringComparison.OrdinalIgnoreCase)
					)
						continue;
					foreach (var methodHandle in type.GetMethods())
					{
						var method = reader.GetMethodDefinition(methodHandle);
						if (
							string.Equals(reader.GetString(method.Name), memberName, StringComparison.OrdinalIgnoreCase)
						)
							return (module, MetadataTokens.GetToken(methodHandle));
					}
				}
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Error resolving method token in {module.ModulePath}: {ex.Message}");
			}
		}
		return (null, 0);
	}

	public List<(string Name, string Path, string BaseAddress, bool IsUserCode, bool HasSymbols)> GetModules() =>
		_modules
			.Values.Select(m =>
				(m.ModuleName, m.ModulePath, m.BaseAddress.ToString(), m.IsUserCode, m.MetadataReader.HasSymbols)
			)
			.ToList();

	public List<string> GetSourceFiles()
	{
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var module in _modules.Values.Where(m => m.IsUserCode && m.MetadataReader.HasSymbols))
		{
			foreach (var file in module.MetadataReader.GetSourceFiles())
			{
				if (!IsGeneratedSourcePath(file))
					files.Add(file);
			}
		}
		return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool IsGeneratedSourcePath(string path)
	{
		var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (
			segments.Any(s =>
				s.Equals("obj", StringComparison.OrdinalIgnoreCase)
				|| s.Equals("bin", StringComparison.OrdinalIgnoreCase)
			)
		)
			return true;
		var name = Path.GetFileName(path);
		return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
	}

	public string? DecompileFrame(int frameId, bool ilMode = false)
	{
		var frameInfo = _frameReferenceManager.GetFrameInfoById(frameId);
		if (frameInfo is null)
			return null;
		try
		{
			var frame = GetFrameForThreadIdAndStackDepth(frameInfo.Value.threadId, frameInfo.Value.frameStackDepth);
			var function = frame.Function;
			var module = _modules[function.Module.BaseAddress];
			var path = module.ModulePath;
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return null;
			using var file = new PEFile(path, PEStreamOptions.PrefetchEntireImage);
			var method = MetadataTokens.MethodDefinitionHandle(function.Token);
			if (ilMode)
			{
				var text = new StringBuilder();
				using var writer = new StringWriter(text);
				var output = new PlainTextOutput(writer) { IndentationString = "  " };
				var disassembler = new ReflectionDisassembler(output, CancellationToken.None)
				{
					ShowSequencePoints = false,
					ShowRawRVAOffsetAndBytes = false,
				};
				disassembler.DisassembleMethod(file, method);
				return text.ToString();
			}
			var paths = _modules.Values.Select(m => m.ModulePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
			var settings = new DecompilerSettings();
			var typeSystem = new DecompilerTypeSystem(file, new DebuggingAssemblyResolver(paths), settings);
			var decompiler = new CSharpDecompiler(typeSystem, settings);
			return PortablePdbWriter2.SyntaxTreeToString(decompiler.Decompile(method), settings);
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"DecompileFrame failed: {ex.Message}");
			return null;
		}
	}
}
