using Xunit;

// Each test binds its own HttpListener and the concurrency tests deliberately saturate the process
// with blocked requests; running classes in parallel would let them measure each other's load.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
