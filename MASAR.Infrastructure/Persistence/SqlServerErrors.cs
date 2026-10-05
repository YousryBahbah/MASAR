using Microsoft.Data.SqlClient;

namespace Masar.Infrastructure.Persistence;

// One place that knows SQL Server error numbers, so the API layer can
// classify failures without referencing SqlClient itself. Walks the whole
// InnerException chain: EF Core wraps the provider exception in
// DbUpdateException / InvalidOperationException, and the depth is not
// something to hardcode.
//
// History worth keeping: a real run surfaced a third wrapper layer — EF Core's
// default (non-retrying) execution strategy wraps a transient-looking failure,
// deadlocks included, in its own InvalidOperationException on top of the
// DbUpdateException on top of the SqlException. Matching a fixed nesting shape
// missed it, which is why this walks the whole chain.
//
// Do NOT "fix" deadlocks by adding EnableRetryOnFailure() to UseSqlServer, even
// though the wrapped exception's own message suggests it: EF Core's retrying
// execution strategy refuses to run inside a manually-managed transaction like
// the SERIALIZABLE one in BookingService, and automatic retries were
// deliberately kept out of v1's scope (Step 3E).
public static class SqlServerErrors
{
    public const int DeadlockVictim = 1205;
    public const int UniqueIndexViolation = 2601;
    public const int UniqueConstraintViolation = 2627;

    public static bool IsDeadlock(Exception exception) => HasSqlError(exception, DeadlockVictim);

    public static bool IsUniqueViolation(Exception exception) =>
        HasSqlError(exception, UniqueIndexViolation) || HasSqlError(exception, UniqueConstraintViolation);

    private static bool HasSqlError(Exception exception, int number)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException && sqlException.Number == number)
            {
                return true;
            }
        }

        return false;
    }
}
