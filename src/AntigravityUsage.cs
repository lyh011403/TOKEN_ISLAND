using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace TokenIsland {
// Field numbers verified against the installed language server's protobuf descriptors.
// Read only numeric usage / state; conversation text is never decoded or persisted.
sealed class UsageProto {
    public sealed class Field { public int Number, Wire; public ulong Value; public byte[] Bytes; }
    public readonly List<Field> Fields = new List<Field>();
    static ulong Varint(byte[] bytes, ref int offset) {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7) {
            if (offset >= bytes.Length) throw new InvalidDataException("Truncated protobuf");
            byte b = bytes[offset++];
            if (shift == 63 && b > 1) throw new InvalidDataException("Invalid varint");
            result |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return result;
        }
        throw new InvalidDataException("Invalid varint");
    }
    public UsageProto(byte[] bytes) {
        if (bytes == null) return;
        int offset = 0;
        while (offset < bytes.Length) {
            ulong tag = Varint(bytes, ref offset);
            if (tag >> 3 == 0 || tag >> 3 > 536870911) throw new InvalidDataException("Invalid protobuf field");
            var field = new Field { Number = (int)(tag >> 3), Wire = (int)(tag & 7) };
            if (field.Wire == 0) field.Value = Varint(bytes, ref offset);
            else {
                ulong length = field.Wire == 2 ? Varint(bytes, ref offset) : field.Wire == 1 ? 8UL : field.Wire == 5 ? 4UL : ulong.MaxValue;
                if (length > (ulong)(bytes.Length - offset)) throw new InvalidDataException("Invalid protobuf length");
                if (field.Wire == 2) { field.Bytes = new byte[(int)length]; Buffer.BlockCopy(bytes, offset, field.Bytes, 0, (int)length); }
                offset += (int)length;
            }
            Fields.Add(field);
        }
    }
    public ulong Number(int number) { var f = Fields.LastOrDefault(x => x.Number == number && x.Wire == 0); return f == null ? 0 : f.Value; }
    public byte[] Bytes(int number) { var f = Fields.LastOrDefault(x => x.Number == number && x.Wire == 2); return f == null ? null : f.Bytes; }
    public bool Has(int number) { return Fields.Any(x => x.Number == number); }
}
sealed class AntigravityStep {
    public long Index;
    public int Type, Status;
    public byte[] Metadata, Payload;
}
sealed class AntigravityUsageResult {
    public long NextIndex;
    public long? LastCallTokens;
    public List<CompletedUsage> Completions = new List<CompletedUsage>();
}
static class AntigravityUsage {
    static readonly Dictionary<string, AntigravityUsageResult> cursors = new Dictionary<string, AntigravityUsageResult>();
    static readonly HashSet<string> childTrajectories = new HashSet<string>();
    public static long? ModelTokens(UsageProto metadata) {
        byte[] bytes = metadata.Bytes(9); // CortexStepMetadata.model_usage
        if (bytes == null || bytes.Length == 0) return null;
        var usage = new UsageProto(bytes);
        // Field 2: input_tokens (未快取之新增輸入)
        // Field 3: output_tokens (模型實際產出 token，含思考與文字)
        // 快取上下文 (Field 5) 屬於伺服器靜態快取，不可在每輪多步工具調用中重複乘以 step 數累加，否則會虛增數十倍天文數字。
        return checked((long)checked(usage.Number(2) + usage.Number(3)));
    }
    public static long? ContextTokens(UsageProto metadata) {
        byte[] bytes = metadata.Bytes(9);
        if (bytes == null || bytes.Length == 0) return null;
        var usage = new UsageProto(bytes);
        return checked((long)checked(usage.Number(2) + usage.Number(5)));
    }
    static DateTime? Finished(UsageProto metadata) {
        byte[] stamp = metadata.Bytes(8); // completed_at, not file modification time
        if (stamp == null) return null;
        var timestamp = new UsageProto(stamp);
        ulong seconds = timestamp.Number(1), nanos = timestamp.Number(2);
        if (seconds > 253402300799UL || nanos >= 1000000000UL) return null;
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).AddTicks((long)nanos / 100);
    }
    public static AntigravityUsageResult Parse(IEnumerable<AntigravityStep> steps, string session, long firstIndex) {
        var result = new AntigravityUsageResult { NextIndex = firstIndex };
        long? user = null; long total = 0; bool known = true;
        CompletedUsage candidate = null;
        long lastIndex = firstIndex;
        Action finish = delegate {
            if (candidate == null || !known || total <= 0) return;
            candidate.Tokens = total; result.Completions.Add(candidate); result.NextIndex = lastIndex + 1;
        };
        foreach (var step in steps) {
            if (step.Type == 14) { // USER_INPUT starts a new user turn.
                finish(); user = step.Index; total = 0; known = true; candidate = null; result.NextIndex = step.Index;
            }
            lastIndex = step.Index;
            candidate = null; // Any later tool / error / pending step means the turn is not done.
            if (!user.HasValue || step.Type != 15) continue;
            try {
                var metadata = new UsageProto(step.Metadata);
                long? tokens = ModelTokens(metadata);
                long? ctx = ContextTokens(metadata);
                if (tokens.HasValue) { total = checked(total + tokens.Value); result.LastCallTokens = ctx ?? tokens; }
                else known = false;
                var envelope = new UsageProto(step.Payload); // Stored CortexStep, not the response alone.
                var payload = new UsageProto(envelope.Bytes(20)); // CortexStep.planner_response
                DateTime? finished = Finished(metadata);
                // DONE + STOP_PATTERN + no tool_calls. A tool-call response is not a final reply.
                if (step.Status == 3 && payload.Number(12) == 2 && !payload.Has(7) && finished.HasValue)
                    candidate = new CompletedUsage { Id = "antigravity:" + session + ":" + user.Value, Finished = finished.Value };
            } catch (InvalidDataException) { known = false; }
              catch (OverflowException) { known = false; }
        }
        finish();
        return result;
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    static byte[] Utf8(string text) { return System.Text.Encoding.UTF8.GetBytes(text + "\0"); }
    static byte[] Blob(IntPtr stmt, int column) {
        int length = sqlite3_column_bytes(stmt, column);
        if (length == 0) return null;
        if (length > 16 * 1024 * 1024) throw new InvalidDataException("Usage record too large");
        byte[] bytes = new byte[length]; Marshal.Copy(sqlite3_column_blob(stmt, column), bytes, 0, length); return bytes;
    }
    static IEnumerable<AntigravityStep> Steps(IntPtr stmt) {
        int status;
        while ((status = sqlite3_step(stmt)) == 100)
            yield return new AntigravityStep { Index = sqlite3_column_int64(stmt, 0), Type = (int)sqlite3_column_int64(stmt, 1), Status = (int)sqlite3_column_int64(stmt, 2), Metadata = Blob(stmt, 3), Payload = Blob(stmt, 4) };
        if (status != 101) throw new IOException("Usage database temporarily unavailable");
    }
    static bool IsChildTrajectory(IntPtr db) {
        IntPtr stmt;
        if (sqlite3_prepare_v2(db, Utf8("SELECT data FROM trajectory_metadata_blob LIMIT 1"), -1, out stmt, IntPtr.Zero) != 0) return false;
        try {
            if (sqlite3_step(stmt) != 100) return false;
            var metadata = new UsageProto(Blob(stmt, 0));
            byte[] parent = metadata.Bytes(5);
            return (parent != null && parent.Length > 0) || metadata.Has(8) || metadata.Number(17) > 0;
        } finally { sqlite3_finalize(stmt); }
    }
    public static AntigravityUsageResult ReadDatabase(string path, long firstIndex) {
        if (childTrajectories.Contains(path)) return new AntigravityUsageResult { NextIndex = firstIndex };
        IntPtr db;
        int opened = sqlite3_open_v2(Utf8(path), out db, 1, IntPtr.Zero); // SQLITE_OPEN_READONLY, including live WAL.
        try {
            if (opened != 0) throw new IOException("Cannot open usage database");
            if (IsChildTrajectory(db)) {
                childTrajectories.Add(path);
                return new AntigravityUsageResult { NextIndex = firstIndex };
            }
            IntPtr stmt;
            string sql = "SELECT idx,step_type,status,CASE WHEN step_type=15 THEN metadata END,CASE WHEN step_type=15 THEN step_payload END FROM steps WHERE idx >= " + firstIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ORDER BY idx";
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out stmt, IntPtr.Zero) != 0) throw new IOException("Unsupported usage schema");
            try { return Parse(Steps(stmt), path, firstIndex); }
            finally { sqlite3_finalize(stmt); }
        } finally { if (db != IntPtr.Zero) sqlite3_close(db); }
    }
    static DateTime Activity(FileInfo db) {
        var wal = new FileInfo(db.FullName + "-wal");
        return wal.Exists && wal.LastWriteTimeUtc > db.LastWriteTimeUtc ? wal.LastWriteTimeUtc : db.LastWriteTimeUtc;
    }
    public static AntigravityUsageResult Read() {
        var result = new AntigravityUsageResult();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "conversations");
        if (!Directory.Exists(dir)) return result;
        foreach (var file in new DirectoryInfo(dir).GetFiles("*.db").OrderByDescending(Activity).Take(8)) {
            try {
                AntigravityUsageResult previous;
                cursors.TryGetValue(file.FullName, out previous);
                var current = ReadDatabase(file.FullName, previous == null ? 0 : previous.NextIndex);
                if (!current.LastCallTokens.HasValue && previous != null) current.LastCallTokens = previous.LastCallTokens;
                cursors[file.FullName] = current;
                if (!result.LastCallTokens.HasValue) result.LastCallTokens = current.LastCallTokens;
                result.Completions.AddRange(current.Completions);
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidDataException) { }
        }
        result.Completions = result.Completions.OrderBy(x => x.Finished).ToList();
        return result;
    }
}
}
