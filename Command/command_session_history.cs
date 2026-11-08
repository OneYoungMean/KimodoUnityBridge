using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static readonly object SessionHistoryLock = new object();

        internal static void EnsureSessionHistoryHeader(TimelineSessionRecord session)
        {
            if (session == null) return;
            string path = GetSessionHistoryAbsolutePath(session);
            lock (SessionHistoryLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    JObject header = ReadHistoryHeader(path);
                    ValidateHistoryCompatibility(header, session.Id);
                    session.CreatedAtUtc = header.Value<string>("created_at_utc") ?? session.CreatedAtUtc;
                    return;
                }

                if (string.IsNullOrWhiteSpace(session.CreatedAtUtc))
                    session.CreatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var headerEntry = new JObject
                {
                    ["event"] = "history.header",
                    ["format_version"] = SessionHistoryFormatVersion,
                    ["package_version"] = PackageVersion,
                    ["session_id"] = session.Id.ToString("D"),
                    ["timeline_asset_guid"] = session.Id.ToString("D"),
                    ["session_name"] = session.Name,
                    ["timeline_asset_path"] = session.TimelineAssetPath ?? string.Empty,
                    ["created_at_utc"] = session.CreatedAtUtc
                };
                AppendJsonLine(path, headerEntry);
            }
        }

        internal static void EnsureSessionHistoryHeaderIfMissing(TimelineSessionRecord session)
        {
            if (session == null) return;
            string path = GetSessionHistoryAbsolutePath(session);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                EnsureSessionHistoryHeader(session);
        }

        private static void RestoreSessionCreationTime(TimelineSessionRecord session)
        {
            if (session == null) return;
            try
            {
                string path = GetSessionHistoryAbsolutePath(session);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
                JObject header = ReadHistoryHeader(path);
                if (Guid.TryParse(header.Value<string>("session_id"), out Guid id) && id == session.Id)
                    session.CreatedAtUtc = header.Value<string>("created_at_utc") ?? session.CreatedAtUtc;
            }
            catch
            {
                // Context is a live Timeline projection. A damaged or incompatible
                // history file must not make the current scene unreadable.
            }
        }

        private static JObject ReadHistoryHeader(string path)
        {
            using (var reader = new StreamReader(path))
            {
                string firstLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(firstLine))
                    throw new CommandException("history_corrupt", $"History file '{path}' has no header.");
                try
                {
                    JObject header = JObject.Parse(firstLine);
                    if (!string.Equals(header.Value<string>("event"), "history.header", StringComparison.Ordinal))
                        throw new CommandException("history_corrupt", $"History file '{path}' does not start with a history.header record.");
                    return header;
                }
                catch (JsonException exception)
                {
                    throw new CommandException("history_corrupt", $"History file '{path}' has an invalid header: {exception.Message}");
                }
            }
        }

        private static void ValidateHistoryIdentity(JObject header, Guid expectedSessionId)
        {
            string headerSessionId = header.Value<string>("session_id");
            if (!Guid.TryParse(headerSessionId, out Guid actualId) || actualId != expectedSessionId)
                throw new CommandException("history_corrupt", $"History header Session ID '{headerSessionId}' does not match '{expectedSessionId:D}'.");
        }

        private static void ValidateHistoryCompatibility(JObject header, Guid expectedSessionId)
        {
            ValidateHistoryIdentity(header, expectedSessionId);
            if (!string.Equals(header.Value<string>("format_version"), SessionHistoryFormatVersion, StringComparison.Ordinal))
                throw new CommandException("history_incompatible", $"History format '{header.Value<string>("format_version") ?? "unknown"}' is incompatible with format '{SessionHistoryFormatVersion}'.");
            string storedVersion = header.Value<string>("package_version") ?? string.Empty;
            if (!TryMajorMinor(storedVersion, out int storedMajor, out int storedMinor) ||
                !TryMajorMinor(PackageVersion, out int currentMajor, out int currentMinor) ||
                storedMajor != currentMajor || storedMinor != currentMinor)
                throw new CommandException("history_incompatible", $"History package version '{storedVersion}' is incompatible with '{PackageVersion}'. Only patch-level differences within the same MAJOR.MINOR are supported.");
        }

        private static bool TryMajorMinor(string version, out int major, out int minor)
        {
            major = 0;
            minor = 0;
            string coreVersion = (version ?? string.Empty).Split(new[] { '-', '+' }, 2)[0];
            string[] parts = coreVersion.Split('.');
            return parts.Length == 3 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
                int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor) &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        internal static void RecordCommandHistory(string command, JObject input, JObject response)
        {
            if (command != GenerateAnimationCommand && command != RetargetAnimationCommand && command != AnimationAnalysisCommand)
                return;
            try
            {
                TimelineSessionRecord session = BuildTimelineSessionView(SceneManager.GetActiveScene()) ?? GetOrCreateSession();
                EnsureSessionHistoryHeader(session);
                string requestId = response?.Value<string>("request_id");
                bool accepted = command == GenerateAnimationCommand &&
                    (string.Equals(response?.Value<string>("status"), "accepted", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(response?.Value<string>("status"), "running", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(requestId);
                string operationId = accepted ? requestId : Guid.NewGuid().ToString("D");
                long operationSeq;
                lock (SessionHistoryLock)
                {
                    List<JObject> entries = ReadHistoryEntries(GetSessionHistoryAbsolutePath(session), session.Id);
                    JObject racedTerminal = accepted
                        ? entries.LastOrDefault(entry =>
                            string.Equals(entry.Value<string>("operation_id"), operationId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Value<string>("event"), "operation.terminal", StringComparison.Ordinal))
                        : null;
                    operationSeq = racedTerminal?.Value<long?>("operation_seq") ?? entries
                        .Where(entry => entry.Value<long?>("operation_seq").HasValue)
                        .Select(entry => entry.Value<long>("operation_seq")).DefaultIfEmpty(0L).Max() + 1L;
                    if (accepted && entries.Any(entry =>
                            string.Equals(entry.Value<string>("operation_id"), operationId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Value<string>("event"), "operation.accepted", StringComparison.Ordinal)))
                        return;
                    var entry = new JObject
                    {
                        ["event"] = accepted ? "operation.accepted" : response?.Value<bool?>("ok") == false ? "operation.failed" : "operation.completed",
                        ["operation_seq"] = operationSeq,
                        ["operation_id"] = operationId,
                        ["command"] = command,
                        ["created_at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        ["session_id"] = session.Id.ToString("D"),
                        ["input"] = input?.DeepClone() ?? new JObject(),
                        ["response"] = response?.DeepClone() ?? new JObject()
                    };
                    AppendJsonLine(GetSessionHistoryAbsolutePath(session), entry);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Kimodo][SessionHistory] Could not append {command} history: {exception}");
            }
        }

        internal static void RecordGenerationTerminalHistory(TimelineSessionRecord session, JObject response)
        {
            if (session == null || response == null) return;
            string status = response.Value<string>("status");
            if (!IsTerminalStatus(status)) return;
            string requestId = response.Value<string>("request_id");
            if (string.IsNullOrWhiteSpace(requestId)) return;
            try
            {
                EnsureSessionHistoryHeader(session);
                string path = GetSessionHistoryAbsolutePath(session);
                lock (SessionHistoryLock)
                {
                    List<JObject> entries = ReadHistoryEntries(path, session.Id);
                    JObject accepted = entries.LastOrDefault(entry =>
                        string.Equals(entry.Value<string>("operation_id"), requestId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.Value<string>("event"), "operation.accepted", StringComparison.Ordinal));
                    if (entries.Any(entry =>
                            string.Equals(entry.Value<string>("operation_id"), requestId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Value<string>("event"), "operation.terminal", StringComparison.Ordinal)))
                        return;
                    long operationSeq = accepted?.Value<long?>("operation_seq") ?? entries
                        .Where(entry => entry.Value<long?>("operation_seq").HasValue)
                        .Select(entry => entry.Value<long>("operation_seq")).DefaultIfEmpty(0L).Max() + 1L;
                    AppendJsonLine(path, new JObject
                    {
                        ["event"] = "operation.terminal",
                        ["operation_seq"] = operationSeq,
                        ["operation_id"] = requestId,
                        ["command"] = GenerateAnimationCommand,
                        ["created_at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        ["session_id"] = session.Id.ToString("D"),
                        ["response"] = response.DeepClone()
                    });
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Kimodo][SessionHistory] Could not append generation terminal event: {exception}");
            }
        }

        private static string ReadSessionHistory(JObject arguments)
        {
            EnsureEditorIsIdle("session.history");
            string scope = arguments.Value<string>("scope")?.Trim();
            if (!string.IsNullOrWhiteSpace(scope) &&
                !string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
                throw new CommandException("invalid_argument", "scope must be 'session' or 'global'.");
            if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments["session_id"] != null)
                    throw new CommandException("invalid_argument", "session_id cannot be combined with scope='global'.");
                if (arguments["historybegin"] != null || arguments["historyend"] != null)
                    throw new CommandException("invalid_argument", "historybegin and historyend cannot be combined with scope='global'.");
                return Ok(new JObject { ["history"] = new JObject { ["scope"] = "global", ["sessions"] = ReadGlobalSessionDirectory() } });
            }

            Guid sessionId;
            TimelineSessionRecord session = null;
            string requestedId = arguments.Value<string>("session_id")?.Trim();
            if (string.IsNullOrWhiteSpace(requestedId))
            {
                session = GetOrCreateSession();
                sessionId = session.Id;
            }
            else if (!Guid.TryParse(requestedId, out sessionId))
            {
                throw new CommandException("invalid_argument", "session_id must be a GUID.");
            }

            if (session != null) EnsureSessionHistoryHeader(session);

            string path = GetSessionHistoryAbsolutePath(sessionId);
            if (!File.Exists(path)) throw new CommandException("session_not_found", $"Session '{sessionId:D}' has no History directory.");
            JObject header = ReadHistoryHeader(path);
            ValidateHistoryCompatibility(header, sessionId);
            if (session != null) session.CreatedAtUtc = header.Value<string>("created_at_utc") ?? session.CreatedAtUtc;
            List<JObject> entries = ReadHistoryEntries(path, sessionId);
            JArray operations = BuildHistoryOperations(entries, arguments);
            return Ok(new JObject
            {
                ["session"] = new JObject
                {
                    ["session_id"] = sessionId.ToString("D"),
                    ["name"] = header.Value<string>("session_name") ?? session?.Name ?? string.Empty,
                    ["created_at_utc"] = header.Value<string>("created_at_utc") ?? string.Empty
                },
                ["history"] = new JObject { ["scope"] = "session", ["operations"] = operations }
            });
        }

        private static JArray ReadGlobalSessionDirectory()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Library", "KimodoData", "Sessions");
            var sessions = new JArray();
            if (!Directory.Exists(root)) return sessions;
            foreach (string directory in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string leaf = Path.GetFileName(directory);
                if (!Guid.TryParse(leaf, out Guid sessionId)) continue;
                string path = Path.Combine(directory, "history.jsonl");
                if (!File.Exists(path)) continue;
                JObject header = ReadHistoryHeader(path);
                ValidateHistoryIdentity(header, sessionId);
                List<JObject> entries = ReadHistoryEntries(path, sessionId, validateCompatibility: false);
                sessions.Add(new JObject
                {
                    ["session_id"] = sessionId.ToString("D"),
                    ["name"] = header.Value<string>("session_name") ?? string.Empty,
                    ["created_at_utc"] = header.Value<string>("created_at_utc") ?? string.Empty,
                    ["operation_count"] = entries.Where(entry => entry.Value<long?>("operation_seq").HasValue)
                        .Select(entry => entry.Value<long>("operation_seq")).Distinct().Count()
                });
            }
            return sessions;
        }

        private static string GetSessionHistoryAbsolutePath(Guid sessionId) => Path.Combine(
            Directory.GetCurrentDirectory(), "Library", "KimodoData", "Sessions", sessionId.ToString("D"), "history.jsonl");

        private static List<JObject> ReadHistoryEntries(string path, Guid expectedSessionId, bool validateCompatibility = true)
        {
            JObject header = ReadHistoryHeader(path);
            if (validateCompatibility) ValidateHistoryCompatibility(header, expectedSessionId);
            else ValidateHistoryIdentity(header, expectedSessionId);
            var entries = new List<JObject>();
            int lineNumber = 1;
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    JObject entry = JObject.Parse(line);
                    if (!Guid.TryParse(entry.Value<string>("session_id"), out Guid eventSessionId) || eventSessionId != expectedSessionId)
                        throw new CommandException("history_corrupt", $"History event at line {lineNumber} belongs to a different Session.");
                    entries.Add(entry);
                }
                catch (JsonException exception)
                {
                    throw new CommandException("history_corrupt", $"History event at line {lineNumber} is invalid JSON: {exception.Message}");
                }
            }
            return entries;
        }

        private static JArray BuildHistoryOperations(List<JObject> entries, JObject arguments)
        {
            bool hasBegin = arguments["historybegin"] != null;
            bool hasEnd = arguments["historyend"] != null;
            if (hasBegin != hasEnd)
                throw new CommandException("invalid_argument", "historybegin and historyend must be provided together.");
            long? requestedBegin = hasBegin ? arguments.Value<long?>("historybegin") : null;
            long? requestedEnd = hasEnd ? arguments.Value<long?>("historyend") : null;
            if (requestedBegin.HasValue && (requestedBegin <= 0 || requestedEnd <= 0 || requestedBegin > requestedEnd))
                throw new CommandException("invalid_argument", "historybegin and historyend must be positive operation sequence numbers with historybegin <= historyend.");

            var operations = new List<JObject>();
            foreach (IGrouping<long, JObject> group in entries
                         .Where(entry => entry.Value<long?>("operation_seq").HasValue)
                         .GroupBy(entry => entry.Value<long>("operation_seq")))
            {
                JObject accepted = group.FirstOrDefault(entry => entry.Value<string>("event") == "operation.accepted");
                JObject terminal = group.LastOrDefault(entry => entry.Value<string>("event") == "operation.terminal");
                JObject complete = group.LastOrDefault(entry => entry.Value<string>("event") == "operation.completed" || entry.Value<string>("event") == "operation.failed");
                JObject first = accepted ?? complete ?? group.OrderBy(entry => entry.Value<string>("created_at_utc"), StringComparer.Ordinal).First();
                JObject last = group.OrderBy(entry => entry.Value<string>("created_at_utc"), StringComparer.Ordinal).Last();
                JObject acceptedResponse = accepted != null ? accepted["response"] as JObject : null;
                JObject result = new JObject
                {
                    ["operation_seq"] = group.Key,
                    ["operation_id"] = first.Value<string>("operation_id") ?? string.Empty,
                    ["command"] = first.Value<string>("command") ?? string.Empty,
                    ["created_at_utc"] = first.Value<string>("created_at_utc") ?? string.Empty,
                    ["updated_at_utc"] = last.Value<string>("created_at_utc") ?? string.Empty,
                    ["status"] = terminal? ["response"]?.Value<string>("status") ??
                        complete?.Value<string>("event")?.Replace("operation.", string.Empty) ??
                        acceptedResponse?.Value<string>("status") ?? "unknown"
                };
                if (accepted?["input"] != null) result["input"] = accepted["input"].DeepClone();
                else if (complete?["input"] != null) result["input"] = complete["input"].DeepClone();
                if (accepted?["response"] != null) result["accepted"] = accepted["response"].DeepClone();
                JObject finalResponse = terminal?["response"] as JObject ?? complete?["response"] as JObject;
                if (finalResponse != null) result["response"] = finalResponse.DeepClone();
                operations.Add(result);
            }

            IEnumerable<JObject> selected = operations.OrderByDescending(item => item.Value<long>("operation_seq"));
            if (requestedBegin.HasValue)
                selected = selected.Where(item => item.Value<long>("operation_seq") >= requestedBegin.Value && item.Value<long>("operation_seq") <= requestedEnd.Value);
            else
                selected = selected.Take(20);
            return new JArray(selected);
        }

        private static void AppendJsonLine(string path, JObject entry)
        {
            File.AppendAllText(path, entry.ToString(Formatting.None) + "\n");
        }
    }
}
