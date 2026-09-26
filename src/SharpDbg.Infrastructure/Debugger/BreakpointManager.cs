using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Manages breakpoint tracking and mapping
/// </summary>
public class BreakpointManager
{
	private int _nextBreakpointId = 1;
	private readonly Dictionary<int, BreakpointInfo> _breakpoints = new();
	private readonly Dictionary<string, List<int>> _breakpointsByFile = new();
	private readonly Dictionary<string, List<int>> _breakpointsByMethod = new();
	private readonly Lock _lock = new();

	public class BreakpointInfo
	{
		public int Id { get; set; }
		public string FilePath { get; set; } = string.Empty;
		public int Line { get; set; }
		public int? Column { get; set; }
		public int EndLine { get; set; }
		public int? EndColumn { get; set; }
		public bool IsIlBreakpoint { get; set; }
		public string MethodName { get; set; } = string.Empty;
		public string MethodKey { get; set; } = string.Empty;
		public string? ModuleName { get; set; }
		public int IlOffset { get; set; }
		public bool Verified { get; set; }
		public ICorDebugFunctionBreakpoint? CorBreakpoint { get; set; }
		public List<FunctionBreakpointBinding> FunctionBindings { get; } = [];
		public string? FunctionName { get; set; }
		public bool IsFunctionBreakpoint => FunctionName is not null;
		public string? Message { get; set; }
		public ModuleMetadataReader.ResolvedBreakpoint? ResolvedBreakpointFromPdb { get; set; }
		public CORDB_ADDRESS? ModuleBaseAddress { get; set; }

		/// <summary>Conditional expression to evaluate when breakpoint is hit</summary>
		public string? Condition { get; set; }

		/// <summary>Hit count condition (e.g., ">=10", "==5", "%3")</summary>
		public string? HitCondition { get; set; }

		/// <summary>Current hit count for this breakpoint</summary>
		public int HitCount { get; set; }
	}

	public record FunctionBreakpointBinding(
		ICorDebugFunctionBreakpoint CorBreakpoint,
		CORDB_ADDRESS ModuleBaseAddress,
		int MethodToken,
		ModuleMetadataReader.ResolvedBreakpoint Source);

	/// <summary>
	/// Create a new source breakpoint
	/// </summary>
	public BreakpointInfo CreateBreakpoint(string filePath, int line, int? column = null, string? condition = null, string? hitCondition = null)
	{
		lock (_lock)
		{
			var id = _nextBreakpointId++;
			if (string.IsNullOrWhiteSpace(condition)) condition = null;
			if (string.IsNullOrWhiteSpace(hitCondition)) hitCondition = null;
			var bp = new BreakpointInfo
			{
				Id = id,
				FilePath = filePath,
				Line = line,
				Column = column,
				Verified = false,
				Condition = condition,
				HitCondition = hitCondition,
				HitCount = 0
			};

			_breakpoints[id] = bp;

			if (!_breakpointsByFile.ContainsKey(filePath))
			{
				_breakpointsByFile[filePath] = [];
			}
			_breakpointsByFile[filePath].Add(id);

			return bp;
		}
	}

	public BreakpointInfo CreateFunctionBreakpoint(string functionName, string? condition = null, string? hitCondition = null)
	{
		lock (_lock)
		{
			var bp = new BreakpointInfo
			{
				Id = _nextBreakpointId++,
				FunctionName = functionName,
				Condition = string.IsNullOrWhiteSpace(condition) ? null : condition,
				HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition
			};
			_breakpoints[bp.Id] = bp;
			return bp;
		}
	}

	public List<BreakpointInfo> GetFunctionBreakpoints()
	{
		lock (_lock)
		{
			return _breakpoints.Values.Where(bp => bp.IsFunctionBreakpoint).ToList();
		}
	}

	public void ClearFunctionBreakpoints()
	{
		lock (_lock)
		{
			foreach (var id in _breakpoints.Values.Where(bp => bp.IsFunctionBreakpoint).Select(bp => bp.Id).ToList())
			{
				_breakpoints.Remove(id);
			}
		}
	}

	/// <summary>
	/// Create a new IL-level method breakpoint
	/// </summary>
	public BreakpointInfo CreateIlBreakpoint(string methodKey, string methodName, int ilOffset, string? moduleName = null, string? condition = null, string? hitCondition = null)
	{
		lock (_lock)
		{
			var id = _nextBreakpointId++;
			if (string.IsNullOrWhiteSpace(condition)) condition = null;
			if (string.IsNullOrWhiteSpace(hitCondition)) hitCondition = null;
			var bp = new BreakpointInfo
			{
				Id = id,
				FilePath = methodKey,
				MethodKey = methodKey,
				MethodName = methodName,
				ModuleName = moduleName,
				IsIlBreakpoint = true,
				IlOffset = ilOffset,
				Verified = false,
				Condition = condition,
				HitCondition = hitCondition,
				HitCount = 0
			};

			_breakpoints[id] = bp;

			if (!_breakpointsByMethod.ContainsKey(methodKey))
			{
				_breakpointsByMethod[methodKey] = [];
			}
			_breakpointsByMethod[methodKey].Add(id);

			return bp;
		}
	}

	/// <summary>
	/// Get breakpoint by ID
	/// </summary>
	public BreakpointInfo? GetBreakpoint(int id)
	{
		lock (_lock)
		{
			return _breakpoints.TryGetValue(id, out var bp) ? bp : null;
		}
	}

	/// <summary>
	/// Get all breakpoints for a file
	/// </summary>
	public List<BreakpointInfo> GetBreakpointsForFile(string filePath)
	{
		lock (_lock)
		{
			if (_breakpointsByFile.TryGetValue(filePath, out var ids))
			{
				return ids.Select(id => _breakpoints[id]).ToList();
			}
			return [];
		}
	}

	/// <summary>
	/// Clear all breakpoints for a file
	/// </summary>
	public void ClearBreakpointsForFile(string filePath)
	{
		lock (_lock)
		{
			if (_breakpointsByFile.TryGetValue(filePath, out var ids))
			{
				foreach (var id in ids)
				{
					_breakpoints.Remove(id);
				}
				_breakpointsByFile.Remove(filePath);
			}
		}
	}

	/// <summary>
	/// Get all IL breakpoints for a method key
	/// </summary>
	public List<BreakpointInfo> GetBreakpointsForMethod(string methodKey)
	{
		lock (_lock)
		{
			if (_breakpointsByMethod.TryGetValue(methodKey, out var ids))
			{
				return ids.Select(id => _breakpoints[id]).ToList();
			}
			return [];
		}
	}

	/// <summary>
	/// Clear all IL breakpoints for a method key
	/// </summary>
	public void ClearBreakpointsForMethod(string methodKey)
	{
		lock (_lock)
		{
			if (_breakpointsByMethod.TryGetValue(methodKey, out var ids))
			{
				foreach (var id in ids)
				{
					_breakpoints.Remove(id);
				}
				_breakpointsByMethod.Remove(methodKey);
			}
		}
	}

	/// <summary>
	/// Find breakpoint by ClrDebug breakpoint
	/// </summary>
	public BreakpointInfo? FindByCorBreakpoint(ICorDebugFunctionBreakpoint corBreakpoint)
	{
		lock (_lock)
		{
			return _breakpoints.Values.FirstOrDefault(bp =>
				bp.CorBreakpoint == corBreakpoint ||
				bp.FunctionBindings.Any(binding => binding.CorBreakpoint == corBreakpoint));
		}
	}

	/// <summary>
	/// Get all pending (unverified) breakpoints
	/// </summary>
	public List<BreakpointInfo> GetPendingBreakpoints()
	{
		lock (_lock)
		{
			return _breakpoints.Values.Where(bp => !bp.Verified).ToList();
		}
	}

	/// <summary>
	/// Get all breakpoints
	/// </summary>
	public List<BreakpointInfo> GetAllBreakpoints()
	{
		lock (_lock)
		{
			return _breakpoints.Values.ToList();
		}
	}

	/// <summary>
	/// Remove a breakpoint by id
	/// </summary>
	public bool RemoveBreakpoint(int id)
	{
		lock (_lock)
		{
			if (!_breakpoints.TryGetValue(id, out var bp)) return false;
			_breakpoints.Remove(id);
			if (_breakpointsByFile.TryGetValue(bp.FilePath, out var fileIds))
			{
				fileIds.Remove(id);
				if (fileIds.Count == 0) _breakpointsByFile.Remove(bp.FilePath);
			}
			if (_breakpointsByMethod.TryGetValue(bp.MethodKey, out var methodIds))
			{
				methodIds.Remove(id);
				if (methodIds.Count == 0) _breakpointsByMethod.Remove(bp.MethodKey);
			}
			return true;
		}
	}

	/// <summary>
	/// Clear all breakpoints
	/// </summary>
	public void Clear()
	{
		lock (_lock)
		{
			_breakpoints.Clear();
			_breakpointsByFile.Clear();
			_breakpointsByMethod.Clear();
			_nextBreakpointId = 1;
		}
	}
}
