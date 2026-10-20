using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TokenIsland;

static class AntigravityTests {
    static byte[] Join(params byte[][] parts) { return parts.SelectMany(x => x).ToArray(); }
    static byte[] Var(ulong n) { var bytes = new List<byte>(); do { byte b = (byte)(n & 127); n >>= 7; bytes.Add((byte)(b | (n > 0 ? 128 : 0))); } while (n > 0); return bytes.ToArray(); }
    static byte[] N(int field, ulong n) { return Join(Var((ulong)(field * 8)), Var(n)); }
    static byte[] B(int field, byte[] bytes) { return Join(Var((ulong)(field * 8 + 2)), Var((ulong)bytes.Length), bytes); }
    static byte[] Meta(ulong input, ulong output, ulong cache) { return Join(B(8, N(1, 2000000000)), B(9, Join(N(2, input), N(3, output), N(5, cache), N(9, output / 2), N(10, output / 2)))); }
    static byte[] Payload(bool tool, ulong reason) { return B(20, Join(N(12, reason), tool ? B(7, new byte[0]) : new byte[0])); }
    static AntigravityStep User(long index) { return new AntigravityStep { Index = index, Type = 14, Status = 3 }; }
    static AntigravityStep Reply(long index, bool tool = false, int status = 3, ulong reason = 2) { return new AntigravityStep { Index = index, Type = 15, Status = status, Metadata = Meta(100, 30, 50), Payload = Payload(tool, reason) }; }
    static AntigravityUsageResult Parse(params AntigravityStep[] steps) { return AntigravityUsage.Parse(steps, "fixture", 0); }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }
    static void Sql(IntPtr db, string sql) { if (sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("Fixture SQL failed"); }
    static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
    public static int Main() {
        try {
            Check(Parse(User(0), Reply(1, true)).Completions.Count == 0, "Completed tool-call response is not turn completion");
            Check(Parse(User(0), Reply(1, false, 2)).Completions.Count == 0, "Streaming final reply stays silent");
            var tool = new AntigravityStep { Index = 2, Type = 132, Status = 3 };
            Check(Parse(User(0), Reply(1, true), tool).Completions.Count == 0, "Long tool pause stays silent");
            var done = Parse(User(0), Reply(1, true), tool, Reply(3));
            Check(done.Completions.Count == 1 && done.Completions[0].Tokens == 260, "Final summary sums new input and output across the turn without multiplying static cache");
            Check(done.LastCallTokens == 150 && done.NextIndex == 4, "Latest call reflects live context window size");
            Check(Parse(User(0), Reply(1), tool).Completions.Count == 0, "Later tool activity prevents premature summary");
            Check(Parse(User(0), Reply(1, false, 3, 16)).Completions.Count == 0, "Canceled reply is not a completed turn");
            Check(Parse(User(0), Reply(1, false, 3, 3)).Completions.Count == 0, "Token-limited continuation is not final");
            Check(Parse(User(0), Reply(1, false, 7)).Completions.Count == 0, "Failed step stays silent");
            var missing = Reply(1); missing.Metadata = B(8, N(1, 2000000000));
            Check(Parse(User(0), missing, Reply(2)).Completions.Count == 0, "Missing usage never yields a partial total");
            var malformed = Reply(1); malformed.Metadata = new byte[] { 0x4A, 0xFF };
            Check(Parse(User(0), malformed).Completions.Count == 0, "Truncated metadata is safely ignored");
            var overflow = Reply(1); overflow.Metadata = Meta(ulong.MaxValue, 1, 0);
            Check(Parse(User(0), overflow).Completions.Count == 0, "Overflow cannot fabricate a total");
            Check(Parse(User(0), Reply(1), User(2), Reply(3)).Completions.Select(x => x.Tokens).SequenceEqual(new long[] { 130, 130 }), "Consecutive user turns stay separate");
            Check(AntigravityUsage.Parse(new[] { User(4), Reply(5) }, "fixture", done.NextIndex).Completions[0].Tokens == 130, "Incremental read does not include previous turn");
            var gate = new CompletionNotifications(done.Completions[0].Finished.AddSeconds(-1));
            Check(gate.Take(done.Completions[0]) && !gate.Take(done.Completions[0]), "Polling never repeats a summary");
            Check(!new CompletionNotifications(done.Completions[0].Finished.AddSeconds(1)).Take(done.Completions[0]), "Startup does not replay old turns");
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "用量-" + Guid.NewGuid().ToString("N") + ".db");
            IntPtr db;
            if (sqlite3_open_v2(Utf8(path), out db, 6, IntPtr.Zero) != 0) throw new Exception("Fixture database failed");
            try {
                Sql(db, "PRAGMA journal_mode=WAL; CREATE TABLE steps(idx INTEGER PRIMARY KEY,step_type INTEGER,status INTEGER,metadata BLOB,step_payload BLOB); INSERT INTO steps VALUES(0,14,3,NULL,NULL); INSERT INTO steps VALUES(1,15,2,X'" + Hex(Meta(100,30,50)) + "',X'" + Hex(Payload(false,2)) + "');");
                var pending = AntigravityUsage.ReadDatabase(path, 0);
                Check(pending.Completions.Count == 0 && pending.NextIndex == 0, "Read-only WAL reader retains an unfinished turn");
                Sql(db, "UPDATE steps SET status=3 WHERE idx=1;");
                var final = AntigravityUsage.ReadDatabase(path, pending.NextIndex);
                Check(final.Completions.Count == 1 && final.Completions[0].Tokens == 130, "WAL-only completion and Unicode paths work");
                Check(AntigravityUsage.ReadDatabase(path, final.NextIndex).Completions.Count == 0, "Database cursor skips delivered completion");
                Sql(db, "CREATE TABLE trajectory_metadata_blob(data BLOB); INSERT INTO trajectory_metadata_blob VALUES(X'" + Hex(B(5, Encoding.UTF8.GetBytes("parent"))) + "');");
                Check(AntigravityUsage.ReadDatabase(path, 0).Completions.Count == 0, "Child agent completion does not interrupt the parent turn");
            } finally { sqlite3_close(db); File.Delete(path); }
            return 0;
        } catch (Exception e) { Console.WriteLine("FAIL " + e.GetType().Name + ": " + e.Message); return 1; }
    }
}
