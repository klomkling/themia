using Xunit;

// Quartz's LogContext is a process-global static, and the engine suites share one Testcontainer each. Run the
// assembly serially so the Postgres/SQL Server scheduler classes don't overwrite each other's log provider or
// contend on the shared containers (mirrors Themia.Framework.Data.EFCore.SqlServer.IntegrationTests).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
