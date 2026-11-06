using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentic.Orchestration.Observability;

/// <summary>
/// Append-only, hash-chained JSONL audit trail. Each entry embeds the previous entry's hash, so any
/// edit, deletion or reordering is detectable by <see cref="Verify"/>.
/// </summary>
public sealed class AuditLog : IRunObserver, IDisposable
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly StreamWriter _writer;
    private string _previousHash = GenesisHash;
    private long _sequence;

    public AuditLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public string Path { get; }

    public void OnEvent(RunEvent e)
    {
        var entry = new JsonObject
        {
            ["seq"] = ++_sequence,
            ["at"] = e.At.ToString("O"),
            ["runId"] = e.RunId,
            ["type"] = e.Type,
            ["stage"] = e.StageId,
            ["attempt"] = e.Attempt,
            ["actor"] = e.Actor,
            ["message"] = e.Message,
            ["data"] = JsonSerializer.SerializeToNode(e.Data, Json.Compact),
            ["prevHash"] = _previousHash,
        };
        var hash = ComputeHash(entry);
        entry["hash"] = hash;
        _writer.WriteLine(entry.ToJsonString(Json.Compact));
        _previousHash = hash;
    }

    public static AuditVerification Verify(string path)
    {
        var previous = GenesisHash;
        var lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            var node = JsonNode.Parse(line)!.AsObject();
            var recorded = node["hash"]?.GetValue<string>();
            node.Remove("hash");
            if (node["prevHash"]?.GetValue<string>() != previous)
                return new AuditVerification(false, lineNo, "prevHash does not match previous entry (entry removed or reordered).");
            var actual = ComputeHash(node);
            if (actual != recorded)
                return new AuditVerification(false, lineNo, "entry content does not match its hash (entry modified).");
            previous = actual;
        }
        return new AuditVerification(true, lineNo, $"{lineNo} entries verified; chain intact.");
    }

    private static string ComputeHash(JsonObject entryWithoutHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entryWithoutHash.ToJsonString(Json.Compact)))).ToLowerInvariant();

    public void Dispose() => _writer.Dispose();
}

public sealed record AuditVerification(bool Valid, int Entries, string Message);
