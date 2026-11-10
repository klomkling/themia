namespace Themia.Exceptional.Alerts;

/// <summary>SQL flavors the alert statements are rendered for.</summary>
internal enum AlertSqlFlavor
{
    PostgreSql = 1,
    MySql = 2,
    SqlServer = 3,
}

/// <summary>
/// Renders the alert statements once for the three engines, so the trigger logic exists in one place and only the
/// quoting, the UTC clock, interval arithmetic and row limiting differ. See <see cref="IExceptionAlertSqlDialect"/>
/// for the parameter contract of each statement.
/// </summary>
internal sealed class AlertSqlBuilder
{
    private const int Claimed = 1;
    private const int Failed = 3;
    private const int Sent = 2;
    private const int OneHourSeconds = 3600;

    private readonly AlertSqlFlavor flavor;

    public AlertSqlBuilder(AlertSqlFlavor flavor) => this.flavor = flavor;

    public string Candidates => $"""
        SELECT {Top("@Limit")} * FROM (
            {CandidateBranch(ExceptionAlertTrigger.New, NewOnlyFilter())}
            UNION ALL
            {CandidateBranch(ExceptionAlertTrigger.Reappeared, ReappearedFilter())}
            UNION ALL
            {CandidateBranch(ExceptionAlertTrigger.Threshold, ThresholdFilter())}
        ) {Q("C")}
        ORDER BY {Q("C")}.{Q("AlertTrigger")}, {Q("C")}.{Q("LastSeenUtc")} DESC{LimitSuffix("@Limit")};
        """;

    public string Claim => flavor switch
    {
        AlertSqlFlavor.SqlServer => $"""
            INSERT INTO {State} ({ClaimColumns})
            SELECT @ExceptionId, @AlertTrigger, {Claimed}, @BatchId, @ApplicationName, @ErrorHash, 1, {Now}
            WHERE NOT EXISTS (
                SELECT 1 FROM {State} WITH (UPDLOCK, HOLDLOCK)
                WHERE {Q("ExceptionId")} = @ExceptionId AND {Q("AlertTrigger")} = @AlertTrigger);
            """,
        AlertSqlFlavor.PostgreSql => $"""
            INSERT INTO {State} ({ClaimColumns})
            VALUES (@ExceptionId, @AlertTrigger, {Claimed}, @BatchId, @ApplicationName, @ErrorHash, 1, {Now})
            ON CONFLICT ({Q("ExceptionId")}, {Q("AlertTrigger")}) DO NOTHING;
            """,
        _ => $"""
            INSERT INTO {State} ({ClaimColumns})
            VALUES (@ExceptionId, @AlertTrigger, {Claimed}, @BatchId, @ApplicationName, @ErrorHash, 1, {Now});
            """,
    };

    public string ClaimedRows => $"""
        SELECT {Q("E")}.{Q("Id")} AS {Q("ExceptionId")}, {Q("E")}.{Q("Guid")}, {Q("E")}.{Q("ApplicationName")},
               {Q("E")}.{Q("ErrorHash")}, {Q("S")}.{Q("AlertTrigger")}, {Q("E")}.{Q("Type")}, {Q("E")}.{Q("Message")},
               {Q("E")}.{Q("DuplicateCount")}, {Q("E")}.{Q("CreationDate")} AS {Q("FirstSeenUtc")},
               {Q("E")}.{Q("LastLogDate")} AS {Q("LastSeenUtc")}
        FROM {State} {Q("S")} JOIN {Exceptions} {Q("E")} ON {Q("E")}.{Q("Id")} = {Q("S")}.{Q("ExceptionId")}
        WHERE {Q("S")}.{Q("ApplicationName")} = @ApplicationName AND {Q("S")}.{Q("BatchId")} = @BatchId AND {Q("S")}.{Q("Status")} = {Claimed}
        ORDER BY {Q("S")}.{Q("AlertTrigger")}, {Q("E")}.{Q("LastLogDate")} DESC;
        """;

    public string StaleBatch => $"""
        SELECT {Top("1")} {Q("BatchId")} FROM {State}
        WHERE {Q("Status")} = {Claimed} AND {Q("ClaimedAt")} < {Ago("@LeaseSeconds")} AND {Q("Attempts")} < @MaxAttempts AND {Q("ApplicationName")} = @ApplicationName
        ORDER BY {Q("ClaimedAt")}{LimitSuffix("1")};
        """;

    public string Reclaim => $"""
        UPDATE {State}
        SET {Q("BatchId")} = @NewBatchId, {Q("ClaimedAt")} = {Now}, {Q("Attempts")} = {Q("Attempts")} + 1
        WHERE {Q("BatchId")} = @OldBatchId AND {Q("Status")} = {Claimed} AND {Q("ClaimedAt")} < {Ago("@LeaseSeconds")} AND {Q("ApplicationName")} = @ApplicationName;
        """;

    public string FailExhausted => $"""
        UPDATE {State} SET {Q("Status")} = {Failed}
        WHERE {Q("Status")} = {Claimed} AND {Q("ClaimedAt")} < {Ago("@LeaseSeconds")} AND {Q("Attempts")} >= @MaxAttempts AND {Q("ApplicationName")} = @ApplicationName;
        """;

    public string MarkSent => $"""
        UPDATE {State} SET {Q("Status")} = {Sent}, {Q("SentAt")} = {Now}
        WHERE {Q("BatchId")} = @BatchId AND {Q("Status")} = {Claimed};
        """;

    public string MarkFailed => $"""
        UPDATE {State} SET {Q("Status")} = {Failed}
        WHERE {Q("BatchId")} = @BatchId AND {Q("Status")} = {Claimed};
        """;

    public string SentBatchesLastHour => $"""
        SELECT COUNT(DISTINCT {Q("BatchId")}) FROM {State}
        WHERE {Q("Status")} = {Sent} AND {Q("SentAt")} >= {Ago(OneHourSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))} AND {Q("ApplicationName")} = @ApplicationName;
        """;

    public string Cleanup => $"""
        DELETE FROM {State}
        WHERE {Q("Status")} <> {Claimed} AND COALESCE({Q("SentAt")}, {Q("ClaimedAt")}) < {Ago("@RetentionSeconds")} AND {Q("ApplicationName")} = @ApplicationName;
        """;

    private string State => Q("ExceptionAlertState");

    private string Exceptions => Q("Exceptions");

    private string ClaimColumns => string.Join(", ",
        new[] { "ExceptionId", "AlertTrigger", "Status", "BatchId", "ApplicationName", "ErrorHash", "Attempts", "ClaimedAt" }
            .Select(Q));

    private string Now => flavor switch
    {
        AlertSqlFlavor.PostgreSql => "now()",
        AlertSqlFlavor.MySql => "UTC_TIMESTAMP(6)",
        _ => "SYSUTCDATETIME()",
    };

    private string Q(string name) => flavor switch
    {
        AlertSqlFlavor.PostgreSql => $"\"{name}\"",
        AlertSqlFlavor.MySql => $"`{name}`",
        _ => $"[{name}]",
    };

    private string Top(string count) => flavor == AlertSqlFlavor.SqlServer ? $"TOP ({count})" : string.Empty;

    private string LimitSuffix(string count) => flavor == AlertSqlFlavor.SqlServer ? string.Empty : $" LIMIT {count}";

    private string Ago(string seconds) => Minus(Now, seconds);

    private string Minus(string timestamp, string seconds) => flavor switch
    {
        AlertSqlFlavor.PostgreSql => $"({timestamp} - ({seconds} * interval '1 second'))",
        AlertSqlFlavor.MySql => $"DATE_SUB({timestamp}, INTERVAL {seconds} SECOND)",
        _ => $"DATEADD(SECOND, -({seconds}), {timestamp})",
    };

    private string CandidateBranch(ExceptionAlertTrigger trigger, string extraFilter) => $"""
        SELECT {Q("E")}.{Q("Id")} AS {Q("ExceptionId")}, {Q("E")}.{Q("Guid")}, {Q("E")}.{Q("ApplicationName")},
               {Q("E")}.{Q("ErrorHash")}, {(int)trigger} AS {Q("AlertTrigger")}, {Q("E")}.{Q("Type")}, {Q("E")}.{Q("Message")},
               {Q("E")}.{Q("DuplicateCount")}, {Q("E")}.{Q("CreationDate")} AS {Q("FirstSeenUtc")},
               {Q("E")}.{Q("LastLogDate")} AS {Q("LastSeenUtc")}
        FROM {Exceptions} {Q("E")}
        WHERE {Q("E")}.{Q("ApplicationName")} = @ApplicationName AND {Q("E")}.{Q("DeletionDate")} IS NULL
          AND {Q("E")}.{Q("LastLogDate")} >= {Ago("@LookbackSeconds")}
          {extraFilter}
        """;

    // An earlier row of the same application + hash, deleted or not: an admin delete must not turn the next
    // occurrence into "New".
    private string PriorRow(string extra = "") => $"""
        EXISTS (SELECT 1 FROM {Exceptions} {Q("P")}
                WHERE {Q("P")}.{Q("ApplicationName")} = {Q("E")}.{Q("ApplicationName")}
                  AND {Q("P")}.{Q("ErrorHash")} = {Q("E")}.{Q("ErrorHash")} AND {Q("P")}.{Q("Id")} < {Q("E")}.{Q("Id")}{extra})
        """;

    private string NoStateFor(ExceptionAlertTrigger? trigger) => $"""
        NOT EXISTS (SELECT 1 FROM {State} {Q("S")} WHERE {Q("S")}.{Q("ExceptionId")} = {Q("E")}.{Q("Id")}{
            (trigger is null ? string.Empty : $" AND {Q("S")}.{Q("AlertTrigger")} = {(int)trigger}")})
        """;

    private string NewOnlyFilter() =>
        $"AND NOT {PriorRow()} AND {NoStateFor(ExceptionAlertTrigger.New)}";

    private string ReappearedFilter() =>
        $"AND {PriorRow()} AND NOT " +
        PriorRow($" AND {Q("P")}.{Q("LastLogDate")} >= {Minus($"{Q("E")}.{Q("CreationDate")}", "@QuietSeconds")}") +
        $" AND {NoStateFor(ExceptionAlertTrigger.Reappeared)}";

    // Once a row has any alert, it never also alerts on Threshold; and one Threshold per application + hash per quiet period.
    private string ThresholdFilter() => $"""
        AND {Q("E")}.{Q("DuplicateCount")} >= @Threshold AND {NoStateFor(null)}
          AND NOT EXISTS (SELECT 1 FROM {State} {Q("T")}
                          WHERE {Q("T")}.{Q("ApplicationName")} = {Q("E")}.{Q("ApplicationName")}
                            AND {Q("T")}.{Q("ErrorHash")} = {Q("E")}.{Q("ErrorHash")}
                            AND {Q("T")}.{Q("AlertTrigger")} = {(int)ExceptionAlertTrigger.Threshold}
                            AND {Q("T")}.{Q("Status")} <> {Failed}
                            AND {Q("T")}.{Q("ClaimedAt")} >= {Ago("@QuietSeconds")})
        """;
}
