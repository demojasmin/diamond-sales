using System.IO;
using System.Text.Json;

namespace DiamondDesktop.Data;

/// <summary>
/// The Sales entry screen, kept on disk between runs.
///
/// WHY IT HAS TO EXIST. Since 0043 a line holds stock the moment it is typed, and the hold is keyed
/// by the entry's client_ref and the line's own key. Both live in memory. Close the app and a fresh
/// InvoiceEntry is built with a NEW client_ref, so the reservation in the database belongs to an
/// entry nothing on screen can reach any more -- the bucket reads short and there is nothing to
/// press to give it back.
///
/// Saving the entry closes that hole from the other side: the same client_ref and the same line
/// keys come back, so the screen you left is the screen you return to, still holding exactly what
/// it was holding.
///
/// RESTORING DOES NOT DOUBLE-RESERVE. reserve_line upserts on (client_ref, line_key) -- the same
/// keys, so re-sending them corrects the existing hold rather than adding a second. That is what
/// makes it safe to re-reserve on load rather than trusting a cache that may have gone stale while
/// the app was shut.
///
/// One file per workspace, beside the outbox and named the same way: Demo and Priya are different
/// databases, and an entry holding stock in one must never be restored against the other.
/// </summary>
public static class EntryStore
{
    private static string Path_ =>
        Environment.GetEnvironmentVariable("SOLITAIREDESK_ENTRY") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolitaireDesk", $"entry-{Db.Active.Ref}.json");

    /// One line, as it was typed. Grade and size travel as IDS, which is what the reservation is
    /// keyed on and what the catalogue can be looked up by -- and the file is per database, so an
    /// id restored here always means the same bucket it meant when it was written.
    /// <param name="BuyerId">
    /// The line's own deal, since the buyer, broker, percentage, terms and type stopped being one
    /// set per screen. Without these a restored entry came back with every row sold to nobody --
    /// which is not merely a blank field: an entry that wrote two invoices before it was closed
    /// would come back as one, and the row that differed would have silently joined the other.
    ///
    /// IDS for the parties, as grade and size already travel: the file is per database, so an id
    /// restored here always means the party it meant when it was written, and a party renamed in
    /// the meantime still resolves. Nullable because a broker is optional and a buyer may not have
    /// been chosen yet.
    /// </param>
    public sealed record StoredLine(
        Guid LineKey, long? GradeId, long? SizeId,
        decimal GrossWeightCt, decimal SelectionCt, decimal PricePerCt,
        decimal ExRate, decimal Less1Pct, decimal Less2Pct, string? Remark,
        long? BuyerId = null, long? BrokerId = null, decimal DealBrokerPct = 0m,
        int DealTermsDays = 0, string? DealDocType = null);

    public sealed record StoredEntry(
        Guid ClientRef, DateTime InvoiceDate, string? Buyer, string? Broker,
        decimal BrokerPct, int TermsDays, string? DocType, List<StoredLine> Lines);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Writes the entry, or removes the file when there is nothing left worth restoring.
    ///
    /// Failures are SWALLOWED. This runs on every line change; a locked file or a full disk must
    /// not interrupt somebody typing, and the worst it costs is a stranded reservation -- which is
    /// exactly the state the app was in before this existed.
    /// </summary>
    public static void Save(StoredEntry entry)
    {
        try
        {
            string path = Path_;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            if (entry.Lines.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }

            // Written beside and moved over, so a crash mid-write cannot leave half a file that
            // parses as an entry holding the wrong carats.
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch { /* see the summary: a save that fails costs a stranded hold, not a lost keystroke */ }
    }

    /// <summary>What was on screen when the app last closed, or null if nothing was.</summary>
    public static StoredEntry? Load()
    {
        try
        {
            string path = Path_;
            if (!File.Exists(path)) return null;

            var entry = JsonSerializer.Deserialize<StoredEntry>(File.ReadAllText(path), Json);
            return entry?.Lines.Count > 0 ? entry : null;
        }
        catch { return null; }   // a corrupt file is a blank screen, not a crash on startup
    }

    /// <summary>Forgets the entry: it has been confirmed, cleared, or its holds returned.</summary>
    public static void Clear()
    {
        try { if (File.Exists(Path_)) File.Delete(Path_); } catch { }
    }
}
