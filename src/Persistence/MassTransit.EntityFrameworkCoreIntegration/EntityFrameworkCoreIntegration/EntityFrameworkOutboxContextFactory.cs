namespace MassTransit.EntityFrameworkCoreIntegration
{
    using System;
    using System.Data;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Storage;
    using Microsoft.Extensions.Options;
    using Middleware;


    public class EntityFrameworkOutboxContextFactory<TDbContext> :
        IOutboxContextFactory<TDbContext>
        where TDbContext : DbContext
    {
        readonly TDbContext _dbContext;
        readonly IsolationLevel _isolationLevel;
        readonly ILockStatementProvider _lockStatementProvider;
        string _lockStatement;

        public EntityFrameworkOutboxContextFactory(TDbContext dbContext, IOptions<EntityFrameworkOutboxOptions> options)
        {
            _dbContext = dbContext;
            _lockStatementProvider = options.Value.LockStatementProvider;
            _isolationLevel = options.Value.IsolationLevel;
        }

        public async Task Send<T>(ConsumeContext<T> context, OutboxConsumeOptions options, IPipe<OutboxConsumeContext<T>> next)
            where T : class
        {
            var messageId = context.GetOriginalMessageId() ?? throw new MessageException(typeof(T), "MessageId required to use the outbox");

            _lockStatement ??= _lockStatementProvider.GetRowLockStatement<InboxState>(_dbContext, nameof(InboxState.MessageId), nameof(InboxState.ConsumerId));

            async Task<bool> Execute()
            {
                var lockId = NewId.NextGuid();

                await using var transaction = await _dbContext.Database.BeginTransactionAsync(_isolationLevel, context.CancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    var inboxState = await _dbContext.Set<InboxState>()
                        .FromSqlRaw(_lockStatement, messageId, options.ConsumerId)
                        .SingleOrDefaultAsync(context.CancellationToken).ConfigureAwait(false);

                    bool continueProcessing;

                    if (inboxState == null)
                    {
                        inboxState = new InboxState
                        {
                            MessageId = messageId,
                            ConsumerId = options.ConsumerId,
                            Received = DateTime.UtcNow,
                            LockId = lockId,
                            ReceiveCount = 0
                        };

                        await _dbContext.AddAsync(inboxState).ConfigureAwait(false);
                        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

                        continueProcessing = true;
                    }
                    else
                    {
                        inboxState.LockId = lockId;
                        inboxState.ReceiveCount++;

                        _dbContext.Update(inboxState);
                        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

                        var outboxContext = new DbContextOutboxConsumeContext<TDbContext, T>(context, options, _dbContext, transaction, inboxState);

                        await next.Send(outboxContext).ConfigureAwait(false);

                        await _dbContext.SaveChangesAsync().ConfigureAwait(false);

                        continueProcessing = outboxContext.ContinueProcessing;
                    }

                    await transaction.CommitAsync(context.CancellationToken).ConfigureAwait(false);

                    return continueProcessing;
                }
                catch (DbUpdateConcurrencyException)
                {
                    await RollbackTransaction(transaction).ConfigureAwait(false);
                    throw;
                }
                catch (DbUpdateException)
                {
                    await RollbackTransaction(transaction).ConfigureAwait(false);
                    throw;
                }
                catch (Exception)
                {
                    await RollbackTransaction(transaction).ConfigureAwait(false);
                    throw;
                }
            }

            var continueProcessing = true;
            while (continueProcessing)
            {
                var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
                if (executionStrategy is ExecutionStrategy)
                    continueProcessing = await executionStrategy.ExecuteAsync(() => Execute()).ConfigureAwait(false);
                else
                    continueProcessing = await Execute().ConfigureAwait(false);
            }
        }

        public void Probe(ProbeContext context)
        {
            var scope = context.CreateFilterScope("outboxContextFactory");
            scope.Add("provider", "entityFrameworkCore");
        }

        static async Task RollbackTransaction(IDbContextTransaction transaction)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception innerException)
            {
                LogContext.Warning?.Log(innerException, "Transaction rollback failed");
            }
        }
    }
}
