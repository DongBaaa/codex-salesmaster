using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace 거래플랜.Desktop.App.Data;

internal sealed class RuntimeMutationCommandInterceptor : DbCommandInterceptor, IMaterializationInterceptor
{
    private readonly ConcurrentDictionary<Guid, IDisposable> _syncLeases = new();
    private readonly ConcurrentDictionary<Guid, IAsyncDisposable> _asyncLeases = new();

    private static readonly HashSet<string> ReadOnlyPragmasWithoutArguments = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "analysis_limit",
        "application_id",
        "auto_vacuum",
        "automatic_index",
        "busy_timeout",
        "cache_size",
        "cache_spill",
        "case_sensitive_like",
        "cell_size_check",
        "checkpoint_fullfsync",
        "collation_list",
        "compile_options",
        "data_store_directory",
        "data_version",
        "database_list",
        "default_cache_size",
        "defer_foreign_keys",
        "empty_result_callbacks",
        "encoding",
        "foreign_key_check",
        "foreign_keys",
        "freelist_count",
        "full_column_names",
        "fullfsync",
        "function_list",
        "hard_heap_limit",
        "ignore_check_constraints",
        "integrity_check",
        "journal_mode",
        "journal_size_limit",
        "legacy_alter_table",
        "legacy_file_format",
        "locking_mode",
        "max_page_count",
        "mmap_size",
        "module_list",
        "page_count",
        "page_size",
        "parser_trace",
        "pragma_list",
        "query_only",
        "quick_check",
        "read_uncommitted",
        "recursive_triggers",
        "reverse_unordered_selects",
        "schema_version",
        "secure_delete",
        "short_column_names",
        "soft_heap_limit",
        "stats",
        "synchronous",
        "table_list",
        "temp_store",
        "temp_store_directory",
        "threads",
        "trusted_schema",
        "user_version",
        "wal_autocheckpoint",
        "writable_schema"
    };

    private static readonly HashSet<string> ReadOnlyPragmasWithArguments = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "foreign_key_check",
        "foreign_key_list",
        "index_info",
        "index_list",
        "index_xinfo",
        "integrity_check",
        "quick_check",
        "table_info",
        "table_list",
        "table_xinfo"
    };

    private RuntimeMutationCommandInterceptor()
    {
    }

    internal static RuntimeMutationCommandInterceptor Instance { get; } = new();

    public object InitializedInstance(
        MaterializationInterceptionData materializationData,
        object entity)
        => materializationData.Context is LocalDbContext db
            ? db.StampMaterializedRuntimeMutationEntity(entity)
            : entity;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ThrowIfUnsupportedResultCommandMutation(command, eventData);
        if (eventData.Context is LocalDbContext db)
            db.ObserveCurrentRuntimeMutationEpoch();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnsupportedResultCommandMutation(command, eventData);
        if (eventData.Context is LocalDbContext db)
            db.ObserveCurrentRuntimeMutationEpoch();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        ThrowIfUnsupportedResultCommandMutation(command, eventData);
        if (eventData.Context is LocalDbContext db)
            db.ObserveCurrentRuntimeMutationEpoch();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnsupportedResultCommandMutation(command, eventData);
        if (eventData.Context is LocalDbContext db)
            db.ObserveCurrentRuntimeMutationEpoch();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is not LocalDbContext db)
            return result;

        var lease = db.AcquireRuntimeMutationCommandGate();
        if (!_syncLeases.TryAdd(eventData.CommandId, lease))
        {
            lease.Dispose();
            throw new InvalidOperationException("A runtime mutation command lease already exists for this command.");
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not LocalDbContext db)
            return result;

        var lease = await db.AcquireRuntimeMutationCommandGateAsync(cancellationToken);
        if (!_asyncLeases.TryAdd(eventData.CommandId, lease))
        {
            await lease.DisposeAsync();
            throw new InvalidOperationException("A runtime mutation command lease already exists for this command.");
        }

        return result;
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        ReleaseSync(eventData.CommandId);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await ReleaseAsync(eventData.CommandId);
        return result;
    }

    public override void CommandFailed(
        DbCommand command,
        CommandErrorEventData eventData)
        => ReleaseSync(eventData.CommandId);

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
        => ReleaseAsync(eventData.CommandId).AsTask();

    public override void CommandCanceled(
        DbCommand command,
        CommandEndEventData eventData)
        => ReleaseSync(eventData.CommandId);

    public override Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
        => ReleaseAsync(eventData.CommandId).AsTask();

    private void ReleaseSync(Guid commandId)
    {
        if (_syncLeases.TryRemove(commandId, out var lease))
            lease.Dispose();
    }

    private async ValueTask ReleaseAsync(Guid commandId)
    {
        if (_asyncLeases.TryRemove(commandId, out var lease))
            await lease.DisposeAsync();
    }

    private static void ThrowIfUnsupportedResultCommandMutation(
        DbCommand command,
        CommandEventData eventData)
    {
        if (
            eventData.Context is LocalDbContext &&
            eventData.CommandSource != CommandSource.SaveChanges &&
            IsPotentialResultCommandMutation(command.CommandText)
        ) {
            throw new InvalidOperationException(
                "Reader-based SQL mutations and scalar SQL mutations are not supported " +
                "for LocalDbContext. " +
                "Use SaveChanges, ExecuteUpdate/Delete, or a non-query runtime mutation command.");
        }
    }

    private static bool IsPotentialResultCommandMutation(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return false;

        if (!TryTokenizeSingleStatement(commandText, out var tokens))
            return true;
        if (tokens.Count == 0)
            return false;
        if (tokens[0].Kind != SqlTokenKind.Word)
            return true;

        var keyword = tokens[0].Text;
        if (keyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (keyword.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase))
            return !IsReadOnlyPragma(tokens);

        if (keyword.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return !IsReadOnlyCommonTableExpression(tokens);

        return true;
    }

    private static bool IsReadOnlyCommonTableExpression(IReadOnlyList<SqlToken> tokens)
    {
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Depth != 0 || token.Kind != SqlTokenKind.Word)
                continue;

            if (token.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                return true;
            if (
                token.Text.Equals("INSERT", StringComparison.OrdinalIgnoreCase) ||
                token.Text.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                token.Text.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
                token.Text.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
            ) {
                return false;
            }
        }

        return false;
    }

    private static bool IsReadOnlyPragma(IReadOnlyList<SqlToken> tokens)
    {
        var index = 1;
        if (index >= tokens.Count || !IsSqlIdentifier(tokens[index]))
            return false;

        var pragmaName = tokens[index].Text;
        index++;
        if (
            index + 1 < tokens.Count &&
            tokens[index].Kind == SqlTokenKind.Dot &&
            IsSqlIdentifier(tokens[index + 1])
        ) {
            pragmaName = tokens[index + 1].Text;
            index += 2;
        }

        if (index == tokens.Count)
            return ReadOnlyPragmasWithoutArguments.Contains(pragmaName);

        return
            ReadOnlyPragmasWithArguments.Contains(pragmaName) &&
            tokens[index].Kind == SqlTokenKind.OpenParenthesis &&
            tokens[index].Depth == 0 &&
            tokens[^1].Kind == SqlTokenKind.CloseParenthesis &&
            tokens[^1].Depth == 0;
    }

    private static bool TryTokenizeSingleStatement(
        string commandText,
        out List<SqlToken> tokens)
    {
        tokens = new List<SqlToken>();
        var span = commandText.AsSpan();
        var index = 0;
        var depth = 0;
        var statementTerminated = false;
        while (index < span.Length)
        {
            while (
                index < span.Length &&
                (char.IsWhiteSpace(span[index]) || span[index] == '\uFEFF')
            ) {
                index++;
            }

            if (index + 1 < span.Length && span[index] == '-' && span[index + 1] == '-')
            {
                index += 2;
                while (index < span.Length && span[index] is not '\r' and not '\n')
                    index++;
                continue;
            }

            if (index + 1 < span.Length && span[index] == '/' && span[index + 1] == '*')
            {
                var closeOffset = span[(index + 2)..].IndexOf("*/".AsSpan());
                if (closeOffset < 0)
                    return false;
                index += closeOffset + 4;
                continue;
            }

            if (index >= span.Length)
                break;

            var current = span[index];
            if (current == ';' && depth == 0)
            {
                statementTerminated = true;
                index++;
                continue;
            }
            if (statementTerminated)
                return false;

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (
                    index < span.Length &&
                    (char.IsLetterOrDigit(span[index]) || span[index] == '_')
                ) {
                    index++;
                }

                tokens.Add(new SqlToken(
                    SqlTokenKind.Word,
                    span[start..index].ToString(),
                    depth));
                continue;
            }

            if (current is '\'' or '"' or '`' or '[')
            {
                var close = current == '[' ? ']' : current;
                var kind = current == '\'' ? SqlTokenKind.Literal : SqlTokenKind.Identifier;
                var start = ++index;
                var closed = false;
                while (index < span.Length)
                {
                    if (span[index] != close)
                    {
                        index++;
                        continue;
                    }

                    if (index + 1 < span.Length && span[index + 1] == close)
                    {
                        index += 2;
                        continue;
                    }

                    var text = span[start..index].ToString()
                        .Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal);
                    tokens.Add(new SqlToken(kind, text, depth));
                    index++;
                    closed = true;
                    break;
                }

                if (!closed)
                    return false;
                continue;
            }

            switch (current)
            {
                case '(':
                    tokens.Add(new SqlToken(SqlTokenKind.OpenParenthesis, "(", depth));
                    depth++;
                    break;
                case ')':
                    if (depth == 0)
                        return false;
                    depth--;
                    tokens.Add(new SqlToken(SqlTokenKind.CloseParenthesis, ")", depth));
                    break;
                case '.':
                    tokens.Add(new SqlToken(SqlTokenKind.Dot, ".", depth));
                    break;
                case '=':
                    tokens.Add(new SqlToken(SqlTokenKind.Equals, "=", depth));
                    break;
                default:
                    tokens.Add(new SqlToken(SqlTokenKind.Other, current.ToString(), depth));
                    break;
            }

            index++;
        }

        return depth == 0;
    }

    private readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Depth);

    private static bool IsSqlIdentifier(SqlToken token)
        => token.Kind is SqlTokenKind.Word or SqlTokenKind.Identifier;

    private enum SqlTokenKind
    {
        Word,
        Identifier,
        Literal,
        Dot,
        Equals,
        OpenParenthesis,
        CloseParenthesis,
        Other
    }
}
