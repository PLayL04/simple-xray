using Xunit.Sdk;
using Xunit.v3;

// Loc holds the UI culture in static state; tests that switch it must not run in parallel.
[assembly: Parallelization(Mode = ParallelMode.None)]
