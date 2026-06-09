namespace Themia.Framework.Data.Dapper.UnitOfWork;

internal enum PendingKind { Add, Update, Remove }

internal sealed record PendingOperation(PendingKind Kind, object Entity, System.Type EntityType);

internal interface IPendingOperationSink
{
    void Enqueue(PendingOperation operation);
}
