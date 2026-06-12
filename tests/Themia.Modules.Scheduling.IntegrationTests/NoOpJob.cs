using Quartz;

namespace Themia.Modules.Scheduling.IntegrationTests;

[DisallowConcurrentExecution]
public sealed class NoOpJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}
