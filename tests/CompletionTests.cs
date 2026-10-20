using System;
using System.Linq;
using System.IO;
using System.Text;
using TokenIsland;

static class CompletionTests {
    static string Event(string payload) { return "{\"type\":\"event_msg\",\"timestamp\":\"2026-09-29T08:00:00Z\",\"payload\":" + payload + "}"; }
    static string Start(string id) { return Event("{\"type\":\"task_started\",\"turn_id\":\"" + id + "\"}"); }
    static string End(string id) { return Event("{\"type\":\"task_complete\",\"turn_id\":\"" + id + "\"}"); }
    static string Tokens(int value) { return Event("{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":" + value + "}}}"); }
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
    public static int Main() {
        try {
            var ongoing = new[] { Tokens(100), Start("a"), Tokens(130), Tokens(180) };
            Check(Providers.ParseCompletedUsage(ongoing, "s", true).Count == 0, "No notification during generation or pauses");
            var finished = ongoing.Concat(new[] { End("a") }).ToArray();
            var result = Providers.ParseCompletedUsage(finished, "s", true);
            Check(result.Count == 1 && result[0].Tokens == 80, "One summary contains full turn delta");
            Check(Providers.ParseCompletedUsage(finished.Concat(new[] { End("a") }).ToArray(), "s", true).Count == 1, "Duplicate completion ignored");
            Check(Providers.ParseCompletedUsage(new[] { Start("first"), Tokens(30), End("first") }, "s", true)[0].Tokens == 30, "First turn counted from zero");
            Check(Providers.ParseCompletedUsage(new[] { Start("a"), Tokens(30), End("a") }, "s", false).Count == 0, "Missing baseline never fabricates usage");
            Check(Providers.ParseCompletedUsage(ongoing.Concat(new[] { Event("{\"type\":\"turn_aborted\"}"), End("a") }).ToArray(), "s", true).Count == 0, "Aborted turn is not completed");
            Check(Providers.ParseCompletedUsage(ongoing.Concat(new[] { End("other") }).ToArray(), "s", true).Count == 0, "Unrelated turn completion ignored");
            Check(Providers.ParseCompletedUsage(finished.Concat(new[] { Start("b"), Tokens(250), End("b") }).ToArray(), "s", true).Select(x => x.Tokens).SequenceEqual(new long[] { 80, 70 }), "Consecutive turns stay separate");
            var gate = new CompletionNotifications(result[0].Finished.AddSeconds(-1));
            Check(gate.Take(result[0]) && !gate.Take(result[0]), "Polling does not replay completed turns");
            Check(!new CompletionNotifications(result[0].Finished.AddSeconds(1)).Take(result[0]), "Startup does not replay history");
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "completion-fixture.jsonl");
            File.WriteAllText(log, String.Join("\n", ongoing) + "\n", new UTF8Encoding(true));
            Check(Providers.ReadCompletedUsage(log).Count == 0, "Incremental reader waits for completion");
            File.AppendAllText(log, new string(' ', 3 * 1024 * 1024) + "\n" + End("a"));
            Check(Providers.ReadCompletedUsage(log).Count == 0, "Partial final line waits for newline");
            File.AppendAllText(log, "\n");
            var incremental = Providers.ReadCompletedUsage(log);
            Check(incremental.Count == 1 && incremental[0].Tokens == 80, "Long turn survives tail boundary and partial writes");
            Check(Providers.ReadCompletedUsage(log).Count == 0, "Unchanged file produces no events");
            File.Delete(log);
            return 0;
        } catch (Exception e) { Console.WriteLine("FAIL " + e.Message); return 1; }
    }
}
