using System.Data;
using System.Globalization;

namespace POC.AURA.Api.Infrastructure;

/// <summary>
/// A zero-allocation <see cref="IDataReader"/> that streams CSV lines directly
/// into <c>SqlBulkCopy.WriteToServerAsync</c> without any intermediate buffer.
///
/// Pipeline (all concurrent at OS/CPU level):
/// <code>
///   Disk → FileStream (64 KB buffer) → StreamReader
///                                           ↓ Read() pulls one line
///                                      SqlBulkCopy internal thread
///                                           ↓ GetValue() parses column on demand
///                                      TDS packet → SQL Server (minimal logging)
/// </code>
///
/// Memory per row: one <c>string[]</c> of split parts (~4 refs) + stack-allocated
/// decimal/DateTime values inside <c>GetValue</c>. No <c>DataTable</c>, no <c>List</c>.
///
/// Column layout (0-indexed):
/// <code>
///   0  BatchId    string   (injected, same for all rows)
///   1  Name       string   (CSV col 0)
///   2  Category   string   (CSV col 1)
///   3  Value      decimal  (CSV col 2)
///   4  Timestamp  DateTime (CSV col 3)
///   5  ImportedAt DateTime (set once at reader creation)
/// </code>
/// </summary>
public sealed class CsvDataReader(StreamReader reader, string batchId) : IDataReader
{
    private readonly DateTime _importedAt = DateTime.UtcNow;
    private string[] _parts = [];

    public int FieldCount => 6;

    /// <summary>
    /// Advances to the next valid CSV row.
    /// Skips blank lines and malformed lines (fewer than 4 columns).
    /// Called by <c>SqlBulkCopy</c> on its internal thread — uses sync <c>ReadLine</c>.
    /// </summary>
    public bool Read()
    {
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null) return false;
            var p = line.Split(',');
            if (p.Length >= 4) { _parts = p; return true; }
        }
    }

    /// <summary>
    /// Returns the column value for the current row.
    /// decimal and DateTime are parsed here as stack-allocated value types —
    /// they are sent to SQL and immediately eligible for collection.
    /// </summary>
    public object GetValue(int i) => i switch
    {
        0 => batchId,
        1 => _parts[0].Trim(),
        2 => _parts[1].Trim(),
        3 => decimal.TryParse(_parts[2].Trim(), NumberStyles.Any,
                 CultureInfo.InvariantCulture, out var v) ? v : 0m,
        4 => DateTime.TryParse(_parts[3].Trim(), out var ts) ? ts : DateTime.UtcNow,
        5 => _importedAt,
        _ => throw new IndexOutOfRangeException($"Column index {i} out of range (0–5).")
    };

    // ── IDataReader boilerplate ───────────────────────────────────────────
    // SqlBulkCopy only calls Read(), GetValue(), FieldCount, and IsDBNull().
    // All other members are not used in this scenario.
    public bool      IsDBNull(int i)      => false;
    public object    this[int i]           => GetValue(i);
    public object    this[string name]     => throw new NotSupportedException();
    public void      Dispose()             { }
    public void      Close()               { }
    public bool      IsClosed              => false;
    public int       Depth                 => 0;
    public int       RecordsAffected       => -1;
    public bool      NextResult()          => false;
    public DataTable? GetSchemaTable()     => null;
    public string    GetName(int i)            => throw new NotSupportedException();
    public int       GetOrdinal(string n)      => throw new NotSupportedException();
    public string    GetDataTypeName(int i)    => throw new NotSupportedException();
    public Type      GetFieldType(int i)       => throw new NotSupportedException();
    public int       GetValues(object[] v)     => throw new NotSupportedException();
    public bool      GetBoolean(int i)         => throw new NotSupportedException();
    public byte      GetByte(int i)            => throw new NotSupportedException();
    public long      GetBytes(int i, long fo, byte[]? b, int bo, int l) => throw new NotSupportedException();
    public char      GetChar(int i)            => throw new NotSupportedException();
    public long      GetChars(int i, long fo, char[]? b, int bo, int l) => throw new NotSupportedException();
    public Guid      GetGuid(int i)            => throw new NotSupportedException();
    public short     GetInt16(int i)           => throw new NotSupportedException();
    public int       GetInt32(int i)           => throw new NotSupportedException();
    public long      GetInt64(int i)           => throw new NotSupportedException();
    public float     GetFloat(int i)           => throw new NotSupportedException();
    public double    GetDouble(int i)          => throw new NotSupportedException();
    public decimal   GetDecimal(int i)         => (decimal)GetValue(i);
    public DateTime  GetDateTime(int i)        => (DateTime)GetValue(i);
    public string    GetString(int i)          => (string)GetValue(i);
    public IDataReader GetData(int i)          => throw new NotSupportedException();
}
