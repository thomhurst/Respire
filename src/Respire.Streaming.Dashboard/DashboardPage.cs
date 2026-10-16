using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace Respire.Streaming.Dashboard;

internal static class DashboardPage
{
    internal static string Render(DashboardSnapshot snapshot, string path, string tokenFieldName, string requestToken,
        DashboardJobLogs? logs = null, DashboardJobGroups? jobGroups = null)
    {
        var html = new StringBuilder(8192);
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Queues and streams · Respire</title><style>
            :root{color-scheme:light;--ink:#203447;--navy:#173957;--blue:#135ea8;--paper:#fff;--surface:#edf2f6;--line:#cbd6e0;--muted:#516577;--amber:#865400;--red:#9a223c}
            *{box-sizing:border-box}body{margin:0;background:var(--surface);color:var(--ink);font:15px/1.55 'Segoe UI',system-ui,sans-serif}
            a{color:var(--blue);text-underline-offset:3px}a:hover{text-decoration-thickness:2px}
            :focus-visible{outline:3px solid #1473c5;outline-offset:4px}header{background:var(--navy);color:white;padding:24px max(24px,calc((100vw - 1240px)/2))}
            .brand{font-size:18px;font-weight:700;letter-spacing:-.4px}.heading{display:flex;align-items:center;justify-content:space-between;gap:24px;flex-wrap:wrap}
            h1{font-size:clamp(26px,4vw,36px);line-height:1.2;letter-spacing:-1px;margin:12px 0}header p{color:#c9d9e6;margin:0;max-width:70ch}
            .refresh{color:white;border:1px solid #94b0c5;border-radius:5px;padding:8px 16px;text-decoration:none;white-space:nowrap}
            .layout{display:grid;grid-template-columns:200px minmax(0,1fr);gap:28px;max-width:1288px;margin:auto;padding:28px 24px}
            nav{align-self:start;position:sticky;top:20px}nav p{margin:0 0 12px;font-weight:600}nav a{display:block;padding:8px 10px;border-left:3px solid var(--line);overflow-wrap:anywhere;text-decoration:none}nav a:hover{border-color:var(--blue);background:#e0e9f1}
            main{min-width:0}.stream{background:var(--paper);border:1px solid var(--line);border-radius:8px;margin-bottom:28px;overflow:hidden;scroll-margin-top:20px}
            .stream-head{padding:22px 24px 16px;border-bottom:1px solid var(--line)}h2{font-size:24px;line-height:1.25;margin:4px 0 8px;overflow-wrap:anywhere}h3{font-size:17px;margin:0 0 12px}
            .kind{color:var(--muted);font-size:13px;font-weight:600}.key{font-family:Consolas,monospace;font-size:13px;overflow-wrap:anywhere}.muted{color:var(--muted)}
            .stats{display:flex;flex-wrap:wrap;gap:12px 36px;margin:18px 0 0}.stats div{min-width:100px}.stats dt{font-size:13px;color:var(--muted)}.stats dd{font-size:25px;font-weight:600;font-variant-numeric:tabular-nums;margin:0}
            .section{padding:20px 24px}.section+.section{border-top:1px solid var(--line)}.table-wrap{overflow-x:auto}table{border-collapse:collapse;width:100%;text-align:left;font-size:14px}caption{text-align:left;color:var(--muted);margin-bottom:10px}th{font-weight:600;color:var(--muted);background:#f3f6f9}th,td{padding:10px 12px;border-bottom:1px solid #e2e8ee;vertical-align:top}td{font-variant-numeric:tabular-nums;overflow-wrap:anywhere}td:first-child{max-width:300px}tr:last-child td{border-bottom:0}
            .empty{color:var(--muted);padding:12px 0;margin:0}.notice{padding:14px 18px;background:#fff5df;border-left:4px solid var(--amber);margin:14px 0;overflow-wrap:anywhere}.error{background:#fff0f2;border-color:var(--red)}
            .dead{border-left:4px solid #b87b1a;padding:16px 18px;background:#fdfaf4;margin:12px 0}.dead-head{display:flex;align-items:baseline;gap:10px 20px;flex-wrap:wrap}.entry-id{font-family:Consolas,monospace;font-size:14px;font-weight:600}.reason{color:var(--amber)}
            details{margin-top:12px}summary{cursor:pointer;color:var(--blue);width:fit-content}.fields{margin:12px 0;display:grid;grid-template-columns:minmax(100px,1fr) minmax(0,3fr);gap:8px 18px;font-size:13px}.fields dt{overflow-wrap:anywhere;font-weight:600}.fields dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere;font-family:Consolas,monospace}
            .actions{display:flex;align-items:flex-start;flex-wrap:wrap;gap:12px 20px;margin-top:16px}.actions details{margin:0}.actions p{max-width:60ch}.actions form{margin:0}button{font:inherit;cursor:pointer;border:1px solid var(--blue);border-radius:5px;background:var(--blue);color:white;padding:7px 14px}button:hover{filter:brightness(.9)}.delete{background:var(--paper);border-color:var(--red);color:var(--red)}
            .note{font-size:13px;color:var(--muted);max-width:75ch;margin:12px 0 0}footer{font-size:13px;color:var(--muted);padding:0 0 24px}
            .charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,260px),1fr));gap:24px}.charts figure{margin:0}.charts figcaption{font-weight:600}.chart{display:block;width:100%;height:auto;margin:8px 0}.chart-times{display:flex;justify-content:space-between;font-size:12px;color:var(--muted)}
            @media(max-width:760px){.layout{grid-template-columns:1fr;padding:18px 12px;gap:18px}nav{position:static}nav a{display:inline-block;margin:0 12px 4px 0}.stream-head,.section{padding:18px 16px}.stats{gap:12px 22px}.fields{grid-template-columns:1fr;gap:4px}.fields dd{margin-bottom:8px}header{padding:20px}.dead{padding:12px}}
            </style></head><body><header><div class="brand">Respire</div><div class="heading"><h1>Queues and streams</h1><a class="refresh" href="
            """);
        html.Append(E(path)).Append("\">").Append(logs is null && jobGroups is null ? "Refresh snapshot" : "Return to dashboard")
            .Append("</a></div><p>Inspect deliveries and return failed work to its stream.</p></header><div class=\"layout\"><nav aria-label=\"Configured streams\"><p>Configured streams</p>");
        foreach (var stream in snapshot.Streams)
            html.Append("<a href=\"#stream-").Append(N(stream.Index)).Append("\">").Append(E(stream.Configuration.Name)).Append("</a>");
        if (logs is not null)
            html.Append("<a href=\"#job-logs\">").Append(E(logs.QueueName)).Append("</a>");
        html.Append("</nav><main id=\"content\">");
        if (logs is not null)
        {
            html.Append("<article class=\"stream\" id=\"job-logs\"><div class=\"stream-head\"><span class=\"kind\">Job logs</span><h2>")
                .Append(E(logs.QueueName)).Append("</h2><div class=\"key\">").Append(E(logs.JobId))
                .Append("</div></div><section class=\"section\"><h3>Retained log lines</h3>");
            if (logs.Lines.Length == 0)
                html.Append("<p class=\"empty\">No retained logs for this job ID. It may not have logged yet, or its logs may have expired.</p>");
            else
            {
                html.Append("<ol>");
                foreach (var line in logs.Lines)
                    html.Append("<li><pre style=\"white-space:pre-wrap;overflow-wrap:anywhere\">").Append(E(line)).Append("</pre></li>");
                html.Append("</ol>");
            }
            html.Append("<p class=\"note\">Newest retained lines in append order, up to the dashboard entry limit. Logs share the job ID across attempts and expire according to the worker policy.</p></section></article>");
        }
        else if (jobGroups is not null)
        {
            html.Append("<article class=\"stream\"><div class=\"stream-head\"><h2>").Append(E(jobGroups.QueueName)).Append("</h2></div>");
            RenderJobGroups(html, jobGroups.Index, jobGroups.Groups, jobGroups.NextCursor, path, tokenFieldName, requestToken);
            html.Append("</article>");
        }
        else if (snapshot.Streams.Count == 0)
            html.Append("<p class=\"notice\">No streams are configured. Add a stream to the dashboard registration to inspect its workers.</p>");
        foreach (var stream in snapshot.Streams)
            RenderStream(html, stream, path, tokenFieldName, requestToken);
        if (logs is null && jobGroups is null)
            html.Append("<footer>Snapshots are shared for two seconds. Refresh to update counts and delivery rate. Retained entries include acknowledged work.</footer>");
        html.Append("</main></div></body></html>");
        return html.ToString();
    }

    private static void RenderStream(StringBuilder html, DashboardStreamSnapshot stream, string path, string tokenFieldName, string requestToken)
    {
        var config = stream.Configuration;
        html.Append("<article class=\"stream\" id=\"stream-").Append(N(stream.Index)).Append("\"><div class=\"stream-head\"><span class=\"kind\">")
            .Append(config.IsJobQueue ? "Job queue" : "Stream worker").Append("</span><h2>").Append(E(config.Name))
            .Append("</h2><div class=\"key\">").Append(E(config.Stream)).Append("</div>");
        if (stream.Error is not null)
        {
            html.Append("<p class=\"notice error\" role=\"status\">").Append(E(stream.Error)).Append("</p>");
        }
        html.Append("<dl class=\"stats\"><div><dt>Retained entries</dt><dd>").Append(stream.SourceUnavailable ? "—" : N(stream.Length))
            .Append("</dd></div><div><dt>Dead letters</dt><dd>").Append(config.DeadLetterStream is null || stream.DeadLettersUnavailable ? "—" : N(stream.DeadLetterLength))
            .Append("</dd></div><div><dt>Deliveries / second</dt><dd>").Append(stream.DeliveriesPerSecond?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—")
            .Append("</dd></div></dl><p class=\"note\">Delivery rate for ").Append(E(config.Group))
            .Append(" over samples at least five seconds apart. It counts new group deliveries, not successful completions. Refresh after five seconds to obtain a rate.</p></div>");
        if (config.IsJobQueue)
        {
            html.Append("<section class=\"section\"><h3>Queue controls</h3><dl class=\"stats\"><div><dt>State</dt><dd>")
                .Append(stream.IsPaused is { } paused ? paused ? "Paused" : "Running" : "—")
                .Append("</dd></div><div><dt>Limiter hits</dt><dd>")
                .Append(stream.RateLimitHits is { } hits ? N(hits) : "—")
                .Append("</dd></div></dl><p class=\"note\">Cumulative denied reader admissions. Jobs remain queued and retain their retry budget.</p></section>");
            RenderMetrics(html, stream.Metrics);
            RenderBatches(html, stream.Batches);
            RenderJobGroups(html, stream.Index, stream.JobGroups?.Groups, stream.JobGroups?.NextCursor,
                path, tokenFieldName, requestToken);
        }
        html.Append("<section class=\"section\"><h3>Consumer groups</h3>");
        if (stream.SourceUnavailable) html.Append("<p class=\"notice\">Source inspection unavailable.</p>");
        else if (stream.Groups.Length == 0) html.Append("<p class=\"empty\">No consumer groups exist yet. Start a worker to create its group.</p>");
        else
        {
            html.Append("<div class=\"table-wrap\"><table><thead><tr><th scope=\"col\">Group</th><th scope=\"col\">Consumers</th><th scope=\"col\">Pending</th><th scope=\"col\">Lag</th><th scope=\"col\">Last delivered</th></tr></thead><tbody>");
            foreach (var group in stream.Groups)
                html.Append("<tr><td>").Append(E(group.Name)).Append("</td><td>").Append(N(group.Consumers)).Append("</td><td>").Append(N(group.Pending))
                    .Append("</td><td>").Append(group.Lag is { } lag ? N(lag) : "—").Append("</td><td class=\"key\">").Append(E(group.LastDeliveredId.ToString())).Append("</td></tr>");
            html.Append("</tbody></table></div>");
        }
        html.Append("</section><section class=\"section\"><h3>Pending deliveries</h3><p class=\"note\">Group: ").Append(E(config.Group)).Append(". Oldest entries first; the configured entry limit applies.</p>");
        if (stream.SourceUnavailable)
            html.Append("<p class=\"notice\">Pending deliveries unavailable. Restore source access before retrying entries.</p>");
        else if (!stream.Groups.Any(group => group.Name == config.Group))
            html.Append("<p class=\"notice\">The configured group does not exist. Start its worker before retrying entries.</p>");
        else if (stream.Pending.Length == 0) html.Append("<p class=\"empty\">No pending deliveries in this group.</p>");
        else
        {
            html.Append("<div class=\"table-wrap\"><table><thead><tr><th scope=\"col\">Entry</th><th scope=\"col\">Consumer</th><th scope=\"col\">Idle seconds</th><th scope=\"col\">Deliveries</th></tr></thead><tbody>");
            foreach (var entry in stream.Pending)
                html.Append("<tr><td class=\"key\">").Append(E(entry.Id.ToString())).Append("</td><td>").Append(E(entry.Consumer))
                    .Append("</td><td>").Append(entry.IdleTime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append("</td><td>").Append(N(entry.DeliveryCount)).Append("</td></tr>");
            html.Append("</tbody></table></div>");
        }
        html.Append("</section><section class=\"section\"><h3>Dead letters</h3>");
        if (config.DeadLetterStream is null) html.Append("<p class=\"empty\">No dead-letter stream is configured for this worker.</p>");
        else
        {
            html.Append("<div class=\"key muted\">").Append(E(config.DeadLetterStream)).Append("</div><p class=\"note\">Oldest entries first; the configured entry limit applies. Retry moves one entry back to the source stream for all its consumer groups.");
            if (config.IsJobQueue) html.Append(" Job retries keep the job ID and payload, reset attempts, and remove the original expiry. Retried batch members run independently without changing the original batch or repeating its callbacks.");
            html.Append("</p>");
            if (stream.DeadLettersUnavailable) html.Append("<p class=\"notice\">Dead-letter inspection unavailable.</p>");
            else if (stream.DeadLetters.Length == 0) html.Append("<p class=\"empty\">No dead letters. Failed entries will appear here when workers move them to this stream.</p>");
            foreach (var entry in stream.DeadLetters)
            {
                html.Append("<div class=\"dead\"><div class=\"dead-head\"><span class=\"entry-id\">").Append(E(entry.Id.ToString()))
                    .Append("</span><span class=\"reason\">").Append(Preview(entry["_respire.reason"])).Append("</span></div><details><summary>Inspect fields</summary><dl class=\"fields\">");
                var count = 0;
                foreach (var field in entry.Fields)
                {
                    if (++count > 32) break;
                    html.Append("<dt>").Append(E(field.Key.Length > 256 ? field.Key[..256] + "…" : field.Key)).Append("</dt><dd>").Append(Preview(field.Value)).Append("</dd>");
                }
                html.Append("</dl><p class=\"note\">Preview shows at most 32 fields and 512 bytes per value. Actions use the complete stored entry.</p></details><div class=\"actions\">");
                if (!stream.SourceUnavailable && stream.Groups.Any(group => group.Name == config.Group))
                    Form(html, stream.Index, entry.Id, "retry", "Retry entry", path, tokenFieldName, requestToken);
                html.Append("<details><summary>Delete entry…</summary><p>Delete this dead letter permanently? Its payload will no longer be available here.</p>");
                Form(html, stream.Index, entry.Id, "delete", "Permanently delete", path, tokenFieldName, requestToken);
                html.Append("</details></div></div>");
            }
        }
        html.Append("</section>");
        if (config.IsJobQueue)
        {
            html.Append("<section class=\"section\"><h3>Job logs</h3><form method=\"get\" action=\"")
                .Append(E(path.TrimEnd('/') + "/" + N(stream.Index) + "/logs"))
                .Append("\"><label for=\"job-id-").Append(N(stream.Index)).Append("\">Application job ID</label> <input id=\"job-id-")
                .Append(N(stream.Index)).Append("\" name=\"jobId\" maxlength=\"4096\" required> <button type=\"submit\">View logs</button></form>")
                .Append("<p class=\"note\">Look up retained log lines from any attempt, including completed and failed jobs.</p></section>");
        }
        html.Append("</article>");
    }

    private static void RenderBatches(StringBuilder html, IReadOnlyList<DashboardJobBatch>? batches)
    {
        html.Append("""
            <section class="section"><h3>Batch progress</h3>
            """);
        if (batches is null || batches.Count == 0)
            html.Append("""<p class="empty">No batch IDs configured. Add BatchIds to this job queue's dashboard registration.</p>""");
        else
        {
            html.Append("""
                <div class="table-wrap"><table><thead><tr><th scope="col">Batch ID</th><th scope="col">Pending</th><th scope="col">Succeeded</th><th scope="col">Failed</th><th scope="col">Cancelled</th><th scope="col">Membership</th><th scope="col">Terminal</th><th scope="col">Cancellation requested</th></tr></thead><tbody>
                """);
            foreach (var batch in batches)
            {
                html.Append("<tr><td>").Append(E(batch.Id)).Append("</td>");
                if (batch.Status is not { } status)
                    html.Append("""<td colspan="7" class="notice">""").Append(batch.Unavailable
                        ? "Batch data unavailable. Check Redis permissions and the configured queue."
                        : "No retained batch data. The ID may be unknown or its state may have been removed.").Append("</td>");
                else
                    html.Append("<td>").Append(N(status.Pending)).Append("</td><td>").Append(N(status.Succeeded))
                        .Append("</td><td>").Append(N(status.Failed)).Append("</td><td>").Append(N(status.Cancelled))
                        .Append("</td><td>").Append(status.IsSealed ? "Sealed" : "Open")
                        .Append("</td><td>").Append(status.IsCompleted ? "Yes" : "No")
                        .Append("</td><td>").Append(status.CancellationRequested ? "Yes" : "No").Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table></div>");
        }
        html.Append("""
            <p class="note">Configured IDs only, in configuration order. Pending includes retries; callback jobs are excluded from counts. Terminal means sealed with no pending members and eligible callbacks published, not executed. Cancellation leaves running handlers uninterrupted. Batch state is retained until explicitly removed.</p></section>
            """);
    }

    private static void RenderJobGroups(StringBuilder html, int index, IReadOnlyList<RespireJobGroupInfo>? groups,
        string? cursor, string path, string tokenFieldName, string requestToken)
    {
        var route = path.TrimEnd('/') + "/" + N(index);
        html.Append("<section class=\"section\"><h3>Application job groups</h3><p class=\"note\">Application groups partition jobs for scheduling and admission. Redis consumer groups track stream deliveries separately.</p>");
        if (groups is null) html.Append("<p class=\"notice\">Application job groups unavailable.</p>");
        else if (groups.Count == 0)
            html.Append("<p class=\"empty\">No grouped jobs or retained leases. This queue may be empty or contain only ungrouped jobs.</p>");
        else
        {
            html.Append("<div class=\"table-wrap\"><table><thead><tr><th scope=\"col\">Application group</th><th scope=\"col\">Queued / due</th><th scope=\"col\">Active leases</th><th scope=\"col\">Group admission</th><th scope=\"col\">Policy</th><th scope=\"col\">Control</th></tr></thead><tbody>");
            foreach (var group in groups)
            {
                html.Append("<tr><td>").Append(E(group.Name)).Append("</td><td>").Append(N(group.QueuedCount)).Append(" / ")
                    .Append(N(group.ReadyCount)).Append("</td><td>").Append(N(group.ActiveCount)).Append("</td><td>");
                if (group.IsPaused) html.Append("Paused. ");
                if (group.IsRateLimited) html.Append("Rate limited. ");
                if (group.IsConcurrencyLimited) html.Append("Concurrency limited. ");
                if (group.IsEligible) html.Append("Eligible");
                else if (!group.IsPaused && !group.IsRateLimited && !group.IsConcurrencyLimited) html.Append("No due work");
                html.Append("</td><td>Concurrency: ").Append(group.MaxConcurrency is { } max ? N(max) : "unlimited")
                    .Append("<br>Rate: ");
                if (group.RateLimit is { } rate)
                    html.Append(N(rate.PermitLimit)).Append(" / ").Append(rate.Window.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(" seconds (burst)");
                else html.Append("unlimited");
                html.Append("<br>Lease: ").Append(group.LeaseDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(" seconds</td><td>");
                html.Append("<form method=\"post\" action=\"").Append(E(route + (group.IsPaused ? "/resume-group" : "/pause-group")))
                    .Append("\"><input type=\"hidden\" name=\"groupToken\" value=\"")
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(group.Name)))
                    .Append("\"><input type=\"hidden\" name=\"").Append(E(tokenFieldName)).Append("\" value=\"").Append(E(requestToken))
                    .Append("\"><button type=\"submit\">").Append(group.IsPaused ? "Resume group" : "Pause group").Append("</button></form></td></tr>");
            }
            html.Append("</tbody></table></div>");
        }
        if (cursor is not null)
            html.Append("<form method=\"post\" action=\"").Append(E(route + "/groups"))
                .Append("\"><input type=\"hidden\" name=\"cursor\" value=\"").Append(E(cursor))
                .Append("\"><input type=\"hidden\" name=\"").Append(E(tokenFieldName)).Append("\" value=\"").Append(E(requestToken))
                .Append("\"><button type=\"submit\">Next groups</button></form>");
        html.Append("<p class=\"note\">Queued counts include delayed jobs and entries awaiting expiry cleanup. Active counts include only live leases. Eligibility is for group admission; queue-wide controls and job expiry still apply. Pause leaves in-flight work running.</p>")
            .Append("<form method=\"post\" action=\"").Append(E(route + "/groups"))
            .Append("\"><input type=\"hidden\" name=\"").Append(E(tokenFieldName)).Append("\" value=\"").Append(E(requestToken))
            .Append("\"><label for=\"group-name-")
            .Append(N(index)).Append("\">Inspect application group</label> <input id=\"group-name-").Append(N(index))
            .Append("\" name=\"group\" maxlength=\"4096\" required> <button type=\"submit\">View group</button></form>")
            .Append("<p class=\"note\">Lists contain groups with queued work or retained leases. Look up an idle group by name to inspect its policy. Missing state shows zero counts and unlimited admission.</p></section>");
    }

    private static void RenderMetrics(StringBuilder html, IReadOnlyList<RespireJobMetricBucket>? buckets)
    {
        html.Append("<section class=\"section\"><h3>Completion history</h3>");
        if (buckets is null)
            html.Append("<p class=\"notice\">Metrics history unavailable.</p>");
        else if (buckets.Count == 0)
            html.Append("<p class=\"empty\">No retained metrics. Enable metrics on the queue workers to record terminal deliveries.</p>");
        else
        {
            html.Append("<div class=\"charts\">");
            Chart(html, buckets, false);
            Chart(html, buckets, true);
            html.Append("</div><p class=\"note\">UTC minutes; the latest minute is partial. Completed counts acknowledged jobs; failed counts terminal dead letters. Retry attempts, expired jobs and dependency failures without a delivery are excluded. Missing buckets show zero, including before recording began or while metrics were disabled.</p><details><summary>View minute counts</summary><div class=\"table-wrap\"><table><thead><tr><th scope=\"col\">Minute (UTC)</th><th scope=\"col\">Completed</th><th scope=\"col\">Failed</th></tr></thead><tbody>");
            foreach (var bucket in buckets)
                html.Append("<tr><td>").Append(bucket.Minute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(N(bucket.Completed)).Append("</td><td>").Append(N(bucket.Failed)).Append("</td></tr>");
            html.Append("</tbody></table></div></details>");
        }
        html.Append("</section>");
    }

    private static void Chart(StringBuilder html, IReadOnlyList<RespireJobMetricBucket> buckets, bool failures)
    {
        var title = failures ? "Failed jobs / minute" : "Completed jobs / minute";
        var max = Math.Max(1, buckets.Max(bucket => failures ? bucket.Failed : bucket.Completed));
        html.Append("<figure><figcaption>").Append(title).Append("</figcaption><svg class=\"chart\" viewBox=\"0 0 480 150\" role=\"img\" aria-label=\"")
            .Append(title).Append("; exact counts in the table below\"><title>").Append(title)
            .Append("</title><path d=\"M36 10V126H474\" fill=\"none\" stroke=\"#cbd6e0\"/><text x=\"0\" y=\"18\" font-size=\"11\" fill=\"#516577\">")
            .Append(N(max)).Append("</text><text x=\"20\" y=\"130\" font-size=\"11\" fill=\"#516577\">0</text><polyline fill=\"none\" stroke=\"")
            .Append(failures ? "#9a223c" : "#135ea8").Append("\" stroke-width=\"2\" points=\"");
        for (var i = 0; i < buckets.Count; i++)
        {
            var value = failures ? buckets[i].Failed : buckets[i].Completed;
            html.Append((36 + 438.0 * i / Math.Max(1, buckets.Count - 1)).ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append((126 - 110.0 * value / max).ToString("0.##", CultureInfo.InvariantCulture)).Append(' ');
        }
        if (buckets.Count == 1)
            html.Append("474,").Append((126 - 110.0 * (failures ? buckets[0].Failed : buckets[0].Completed) / max).ToString("0.##", CultureInfo.InvariantCulture));
        html.Append("\"/></svg><div class=\"chart-times\"><span>").Append(buckets[0].Minute.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC</span><span>").Append(buckets[^1].Minute.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC</span></div></figure>");
    }

    private static void Form(StringBuilder html, int index, RespireStreamId id, string action, string label, string path, string tokenFieldName, string requestToken)
    {
        html.Append("<form method=\"post\" action=\"").Append(E(path.TrimEnd('/') + "/" + N(index) + "/" + action))
            .Append("\"><input type=\"hidden\" name=\"").Append(E(tokenFieldName)).Append("\" value=\"").Append(E(requestToken))
            .Append("\"><input type=\"hidden\" name=\"id\" value=\"").Append(E(id.ToString())).Append("\"><button type=\"submit\"");
        if (action == "delete") html.Append(" class=\"delete\"");
        html.Append('>').Append(label).Append("</button></form>");
    }

    private static string Preview(byte[]? bytes)
    {
        if (bytes is null) return "—";
        var length = Math.Min(bytes.Length, 512);
        return E(Encoding.UTF8.GetString(bytes, 0, length)) + (bytes.Length > length ? "…" : "");
    }

    private static string E(string value) => HtmlEncoder.Default.Encode(value);
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
