using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class PrometheusCheck
{
    // These require deployments/events beyond the standalone Redis smoke workload.
    private static readonly HashSet<string> FeatureMetrics =
    ["redis_client_geofailover_failovers_total", "redis_client_connection_relaxed_timeout", "redis_client_connection_handoff_total"];
    private static readonly HashSet<string> UnsupportedMetrics =
    ["redis_client_csc_network_saved_bytes_total", "redis_client_csc_items"];

    internal static bool HasPositive(string text, string metric, params string[] labels) => Samples(text, metric)
        .Any(line => labels.All(label => line.Contains(label, StringComparison.Ordinal))
            && double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture) > 0);

    private static IEnumerable<string> Samples(string text, string metric) => text.Split('\n')
        .Where(line => line.StartsWith(metric + "{", StringComparison.Ordinal)
            || line.StartsWith(metric + " ", StringComparison.Ordinal));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Verify(string output, string dashboardPath, string dedicatedPoolLabel)
    {
        var defaults = File.ReadAllText(Path.Combine(output, "default.prom"));
        var optional = File.ReadAllText(Path.Combine(output, "optional.prom"));
        var closed = File.ReadAllText(Path.Combine(output, "closed.prom"));
        var busy = File.ReadAllText(Path.Combine(output, "busy.prom"));
        foreach (var scrape in new[] { defaults, busy, optional, closed })
        {
            foreach (var metric in UnsupportedMetrics)
                Require(!Samples(scrape, metric).Any(), "Unsupported dashboard metric is now exported; update its classification: " + metric);
            foreach (var metric in FeatureMetrics)
                // Observable gauges can export zero before their feature event occurs.
                Require(!HasPositive(scrape, metric), "Feature event is now exercised; update its dashboard classification: " + metric);
        }
        foreach (var family in new[] { "db_client_connection_count", "db_client_connection_create_time_seconds", "redis_client_errors_total" })
            Require(defaults.Contains("# TYPE " + family + " ", StringComparison.Ordinal), "Missing default family: " + family);
        foreach (var family in new[] { "db_client_operation_duration_seconds", "redis_client_csc_requests_total", "redis_client_csc_evictions_total", "redis_client_pubsub_messages_total", "redis_client_stream_lag_seconds", "redis_client_connection_closed_total", "db_client_connection_wait_time_seconds", "db_client_connection_pending_requests" })
            Require(!defaults.Contains("# TYPE " + family + " ", StringComparison.Ordinal), "Opt-in family leaked into defaults: " + family);

        foreach (var family in new[] { "redis_client_errors", "redis_client_csc_requests", "redis_client_csc_evictions", "redis_client_pubsub_messages", "redis_client_connection_closed" })
        {
            Require(closed.Contains("# TYPE " + family + "_total counter", StringComparison.Ordinal), "Not a counter: " + family);
            Require(HasPositive(closed, family + "_total"), "Counter did not increase: " + family);
        }
        foreach (var family in new[] { "db_client_operation_duration_seconds", "db_client_connection_create_time_seconds", "db_client_connection_wait_time_seconds", "redis_client_stream_lag_seconds" })
        {
            Require(optional.Contains("# TYPE " + family + " histogram", StringComparison.Ordinal), "Not a seconds histogram: " + family);
            Require(HasPositive(optional, family + "_count"), "Histogram has no observations: " + family);
            Require(Samples(optional, family + "_sum").Any(), "Missing histogram sum: " + family);
            Require(Samples(optional, family + "_bucket").Any(line => line.Contains("le=\"+Inf\"", StringComparison.Ordinal)), "Missing infinite bucket: " + family);
        }
        Require(HasPositive(optional, "redis_client_csc_requests_total", "redis_client_csc_result=\"hit\""), "No cache hit.");
        Require(HasPositive(optional, "redis_client_csc_requests_total", "redis_client_csc_result=\"miss\""), "No cache miss.");
        foreach (var direction in new[] { "in", "out" })
            Require(HasPositive(optional, "redis_client_pubsub_messages_total", $"redis_client_pubsub_message_direction=\"{direction}\""), "Missing pub/sub direction: " + direction);
        Require(HasPositive(closed, "redis_client_connection_closed_total", "redis_client_connection_close_reason=\"application_close\""), "No application close.");
        Require(HasPositive(busy, "db_client_connection_pending_requests", dedicatedPoolLabel), "No pending dedicated reply.");
        Require(HasPositive(busy, "db_client_connection_count", dedicatedPoolLabel, "db_client_connection_state=\"used\""), "No used dedicated socket.");
        Require(HasPositive(optional, "db_client_connection_count", dedicatedPoolLabel, "db_client_connection_state=\"idle\""), "Dedicated socket did not return idle.");
        Require(!HasPositive(optional, "db_client_connection_count", dedicatedPoolLabel, "db_client_connection_state=\"used\""), "Dedicated socket remains used after its reply.");
        foreach (var metric in new[] { "redis_client_errors_total", "redis_client_csc_requests_total", "redis_client_pubsub_messages_total", "redis_client_stream_lag_seconds_count" })
            Require(Samples(optional, metric).All(line => line.Contains("redis_client_library=\"Respire:", StringComparison.Ordinal)
                && line.Contains("db_system_name=\"redis\"", StringComparison.Ordinal)), "Missing standard labels: " + metric);
        Require(optional.Contains("redis_client_errors_category=", StringComparison.Ordinal) && optional.Contains("error_type=", StringComparison.Ordinal), "Missing error panel labels.");
        Require(optional.Contains("db_operation_name=", StringComparison.Ordinal) && optional.Contains("db_client_connection_pool_name=", StringComparison.Ordinal), "Missing command/pool panel labels.");
        foreach (var family in new[] { "db_client_connection_count", "db_client_connection_pending_requests", "redis_client_connection_relaxed_timeout" })
            Require(optional.Contains("# TYPE " + family + " gauge", StringComparison.Ordinal), "Not a gauge: " + family);
        foreach (var scrape in new[] { defaults, busy, optional, closed })
            Require(!scrape.Contains("optional:", StringComparison.Ordinal) && !scrape.Contains("default:", StringComparison.Ordinal),
                "Private key/channel/stream name leaked into export.");

        VerifyDashboard(output, dashboardPath, closed);
    }

    private static void VerifyDashboard(string output, string dashboardPath, string closed)
    {
        var dashboard = JsonNode.Parse(File.ReadAllText(dashboardPath))!;
        var report = new List<object>();
        var verifiedQueries = new List<string>();
        var parseOnlyQueries = new List<string>();
        var seen = new HashSet<string>();
        foreach (var panel in Panels(dashboard["panels"]!.AsArray()))
        {
            if (panel["targets"] is not JsonArray targets) continue;
            var originalTitle = panel["title"]!.GetValue<string>();
            foreach (var target in targets)
            {
                if (target?["expr"] is not JsonValue expression) continue;
                var original = expression.GetValue<string>();
                // A direct scrape has no Collector-generated exported_job. Scope by Redis instrument labels.
                var adapted = original.Replace("exported_job=~\"$service_name\"", "db_system_name=~\"$service_name\"", StringComparison.Ordinal)
                    .Replace("redis_client_pubsub_direction", "redis_client_pubsub_message_direction", StringComparison.Ordinal)
                    .Replace("redis_client_pubsub_channel", "redis_client_pubsub_message_direction", StringComparison.Ordinal)
                    .Replace("le, redis_client_stream_consumer_name, redis_client_stream_name", "le", StringComparison.Ordinal)
                    .Replace("le, redis_client_stream_name", "le", StringComparison.Ordinal);
                target!["expr"] = adapted;
                var metrics = Regex.Matches(adapted, @"\b(?:db_client|redis_client)_[a-z_]+(?=\{)")
                    .Select(match => match.Value).Distinct().ToArray();
                Require(metrics.Length != 0, "Unrecognized dashboard query: " + original);
                var status = "verified";
                if (metrics.Any(UnsupportedMetrics.Contains)) status = "unsupported";
                else if (metrics.Any(FeatureMetrics.Contains)) status = "requires-feature-event";
                foreach (var metric in metrics)
                {
                    seen.Add(metric);
                    if (!UnsupportedMetrics.Contains(metric) && !FeatureMetrics.Contains(metric))
                        Require(Samples(closed, metric).Any(), "Dashboard series missing from real export: " + metric);
                }
                var query = adapted.Replace("$pool_name", ".*", StringComparison.Ordinal)
                    .Replace("$service_name", "redis", StringComparison.Ordinal)
                    .Replace("$__rate_interval", "1m", StringComparison.Ordinal);
                // Every remaining query label is either a selector/group label actually exported or a PromQL keyword.
                if (status == "verified")
                {
                    var labels = Regex.Matches(adapted, @"\b(?:db_[a-z_]+|redis_[a-z_]+|error_type|le)\b")
                        .Select(match => match.Value).Where(name => !metrics.Contains(name)).Distinct();
                    foreach (var label in labels)
                        Require(closed.Contains(label + "=", StringComparison.Ordinal), "Dashboard label missing: " + label);
                    verifiedQueries.Add(query);
                }
                else parseOnlyQueries.Add(query);
                report.Add(new { panel = originalTitle, original, adapted, metrics, status });
            }
            // Do not present aggregate data as a channel/stream/consumer breakdown.
            panel["title"] = originalTitle switch
            {
                "Message Rate by Channel (Top 10)" => "Message Rate by Direction",
                "Stream Lag by Stream" => "Aggregate Stream Lag",
                "Consumer Performance (Lag)" => "Aggregate Processing Start Lag",
                _ => originalTitle,
            };
        }
        Require(FeatureMetrics.IsSubsetOf(seen) && UnsupportedMetrics.IsSubsetOf(seen), "Pinned dashboard exception inventory changed.");
        // The selector variables need the same direct-scrape adaptation as panel queries.
        foreach (var variable in dashboard["templating"]!["list"]!.AsArray())
        {
            if (variable?["name"]?.GetValue<string>() != "service_name") continue;
            variable["query"]!["query"] = "label_values(db_client_connection_count, db_system_name)";
            variable["definition"] = "label_values(db_client_connection_count, db_system_name)";
            variable["label"] = "Database system (direct scrape)";
        }
        File.WriteAllText(Path.Combine(output, "dashboard-adapted.json"), dashboard.ToJsonString(new() { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "dashboard-report.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        WritePromqlTests(output, closed, verifiedQueries, parseOnlyQueries);
    }

    private static void WritePromqlTests(string output, string export, IEnumerable<string> queries, IEnumerable<string> parseOnlyQueries)
    {
        // Inputs are real exported series. Constant samples test PromQL shape/labels, not workload rates.
        static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
        var yaml = new System.Text.StringBuilder("rule_files: []\nevaluation_interval: 15s\ntests:\n  - interval: 15s\n    input_series:\n");
        foreach (var line in export.Split('\n').Where(line => line.Length != 0 && line[0] != '#'))
        {
            var split = line.LastIndexOf(' ');
            yaml.Append("      - series: ").AppendLine(Quote(line[..split]));
            yaml.Append("        values: ").AppendLine(Quote(line[(split + 1)..].Trim() + "+0x8"));
        }
        yaml.AppendLine("    promql_expr_test:");
        foreach (var query in queries.Distinct())
        {
            yaml.Append("      - expr: ").AppendLine(Quote("count(" + query + ") > bool 0"));
            yaml.AppendLine("        eval_time: 2m\n        exp_samples:\n          - labels: '{}'\n            value: 1");
        }
        // Exercise the same regex matchers as the dashboard variables, including values that cannot match.
        var pool = Regex.Match(Samples(export, "db_client_connection_count").First(), "db_client_connection_pool_name=(\"[^\"]+\")");
        Require(pool.Success, "No exported pool for selector controls.");
        var poolValue = System.Text.Json.JsonSerializer.Deserialize<string>(pool.Groups[1].Value)!;
        string Selector(string service, string poolPattern) => "db_client_connection_count{db_system_name=~"
            + Quote(service) + ",db_client_connection_pool_name=~" + Quote(poolPattern) + "}";
        yaml.Append("      - expr: ").AppendLine(Quote("count(" + Selector("redis", Regex.Escape(poolValue)) + ") > bool 0"));
        yaml.AppendLine("        eval_time: 2m\n        exp_samples:\n          - labels: '{}'\n            value: 1");
        foreach (var selector in new[] { Selector("__missing_service__", Regex.Escape(poolValue)), Selector("redis", "__missing_pool__") })
        {
            yaml.Append("      - expr: ").AppendLine(Quote(selector));
            yaml.AppendLine("        eval_time: 2m\n        exp_samples: []");
        }
        // Empty inputs validate syntax without inventing unsupported measurements or feature events.
        yaml.AppendLine("  - interval: 15s\n    input_series: []\n    promql_expr_test:");
        foreach (var query in parseOnlyQueries.Distinct())
        {
            yaml.Append("      - expr: ").AppendLine(Quote(query));
            yaml.AppendLine("        eval_time: 2m\n        exp_samples: []");
        }
        File.WriteAllText(Path.Combine(output, "dashboard-promql-tests.yml"), yaml.ToString());
    }

    private static IEnumerable<JsonNode> Panels(JsonArray panels)
    {
        foreach (var panel in panels)
        {
            if (panel is null) continue;
            yield return panel;
            if (panel["panels"] is JsonArray children)
                foreach (var child in Panels(children)) yield return child;
        }
    }
}
