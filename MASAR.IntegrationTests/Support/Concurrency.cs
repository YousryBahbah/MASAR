namespace Masar.IntegrationTests.Support;

internal static class Concurrency
{
    // Releases all operations at the same instant, each on its own thread-pool
    // task, so they genuinely overlap instead of running back to back.
    public static async Task<T[]> RunAsync<T>(params Func<Task<T>>[] operations)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = operations
            .Select(operation => Task.Run(async () =>
            {
                await gate.Task;
                return await operation();
            }))
            .ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    public static async Task RunAllAsync(params Func<Task>[] operations)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = operations
            .Select(operation => Task.Run(async () =>
            {
                await gate.Task;
                await operation();
            }))
            .ToArray();

        gate.SetResult();
        await Task.WhenAll(tasks);
    }
}
