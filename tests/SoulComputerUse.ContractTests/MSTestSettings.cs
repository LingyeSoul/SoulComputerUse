// The contract suite is pure decisions, string checks and source scans: it never takes the session
// lock and never writes machine state, so it may run in parallel within itself. It is not kept apart
// from the integration suite by any run configuration; each suite is self-contained.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]