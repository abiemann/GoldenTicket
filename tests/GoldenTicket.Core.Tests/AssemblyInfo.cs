using Xunit;

// Several SQLite tests clear the process-wide connection pool while removing their temporary
// database. Running another SQLite test at the same time can dispose its active native handle.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
