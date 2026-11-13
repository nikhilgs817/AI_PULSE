using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AiPulse;

public sealed record BackupPolicy(int DailyCopies=3,int WeeklyCopies=4,int BudgetMiB=512)
{
    public BackupPolicy Bounded()=>new(Math.Clamp(DailyCopies,1,14),Math.Clamp(WeeklyCopies,0,12),Math.Clamp(BudgetMiB,64,8192));
}
public sealed record BackupEntry(string File,long Bytes,DateTime At);

public sealed partial class Storage
{
    // Large intermediate SQLite files stay in the OS temporary area, outside the selected sync folder.
    static string Scratch()
    {
        string folder=Path.Combine(Path.GetTempPath(),"aipulse-backup-"+Guid.NewGuid().ToString("N"));
        if(OperatingSystem.IsWindows())Directory.CreateDirectory(folder);else Directory.CreateDirectory(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        return Path.Combine(folder,"pulse.db");
    }
    static void CleanScratch(string path)
    {
        foreach(var suffix in new[]{"","-wal","-shm","-journal"})File.Delete(path+suffix);
        string? folder=Path.GetDirectoryName(Path.GetFullPath(path));
        if(folder!=null&&Path.GetFileName(folder).StartsWith("aipulse-backup-",StringComparison.Ordinal)&&Path.GetFullPath(Path.GetDirectoryName(folder)!)==Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))&&!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);
    }
    static void CheckDatabase(string file)
    {
        using var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());database.Open();
        using var c=database.CreateCommand();c.CommandText="PRAGMA integrity_check";if((string?)c.ExecuteScalar()!="ok")throw new InvalidDataException("Database integrity failed");
        c.CommandText="PRAGMA user_version";if(Convert.ToInt32(c.ExecuteScalar())!=1)throw new InvalidDataException("Unsupported schema");
    }
    static FileStream CheckedStream(string file)
    {
        if(!file.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)&&File.Exists(file+"-wal")&&new FileInfo(file+"-wal").Length>0)throw new InvalidDataException("Backup has uncheckpointed changes; close its writer before using this snapshot");
        if(!File.Exists(file+".sha256"))throw new InvalidDataException("Checksum missing");
        var stream=File.OpenRead(file);
        try{if(!string.Equals(Convert.ToHexString(SHA256.HashData(stream)),File.ReadAllText(file+".sha256").Trim(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Checksum mismatch");stream.Position=0;return stream;}
        catch{stream.Dispose();throw;}
    }
    static void CopyChecked(Stream input,Stream output,CancellationToken ct)
    {
        var buffer=new byte[1024*1024];int count;
        while((count=input.Read(buffer))>0){ct.ThrowIfCancellationRequested();output.Write(buffer,0,count);}
    }
    static void Unpack(Stream source,string file,string destination,CancellationToken ct)
    {
        using var target=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        if(file.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)){using var zip=new GZipStream(source,CompressionMode.Decompress,true);CopyChecked(zip,target,ct);}
        else CopyChecked(source,target,ct);
    }
    public static void VerifyBackup(string file)
    {
        using var input=CheckedStream(file);
        if(!file.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)){CheckDatabase(file);return;}
        string temp=Scratch();try{Unpack(input,file,temp,CancellationToken.None);CheckDatabase(temp);}finally{CleanScratch(temp);}
    }
    // A verified, decompressed file is staged beside the destination before publishing it atomically.
    public static void MaterializeBackup(string file,string destination)
    {
        string target=Path.GetFullPath(destination);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp=target+"."+Guid.NewGuid().ToString("N")+".partial";
        try{using(var input=CheckedStream(file))Unpack(input,file,temp,CancellationToken.None);CheckDatabase(temp);File.Move(temp,target,false);}
        finally{CleanScratch(temp);}
    }
    internal static void RestoreClosedDatabase(string backup,string database)
    {
        var staged=database+".restore-"+Guid.NewGuid().ToString("N");
        var quarantine=database+".before-restore-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
        var moved=new List<(string Original,string Saved)>();
        try
        {
            MaterializeBackup(backup,staged);
            if(File.Exists(database))
                using(new FileStream(database,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
            foreach(var suffix in new[]{"","-wal","-shm","-journal"})
            {
                if(!File.Exists(database+suffix))continue;
                File.Move(database+suffix,quarantine+suffix);
                moved.Add((database+suffix,quarantine+suffix));
            }
            File.Move(staged,database);
        }
        catch
        {
            foreach(var (original,saved) in moved.AsEnumerable().Reverse())File.Move(saved,original);
            throw;
        }
        finally{CleanScratch(staged);}
    }
    static void PublishCompressed(string raw,string target,CancellationToken ct)
    {
        string compressed=Scratch()+".gz",partial=target+".partial";
        try
        {
            using(var input=File.OpenRead(raw))using(var output=File.Create(compressed))using(var zip=new GZipStream(output,CompressionLevel.SmallestSize))CopyChecked(input,zip,ct);
            ct.ThrowIfCancellationRequested();using(var input=File.OpenRead(compressed))using(var output=File.Create(partial))CopyChecked(input,output,ct);
            File.Move(partial,target,false);
            using var result=File.OpenRead(target);File.WriteAllText(target+".sha256",Convert.ToHexString(SHA256.HashData(result)));
        }
        finally{CleanScratch(compressed);File.Delete(partial);}
    }
    public string Backup(string folder,BackupPolicy? policy=null,CancellationToken ct=default)
    {
        lock(backupGate)
        {
            Directory.CreateDirectory(folder);string raw=Scratch();
            string file=Path.Combine(folder,"pulse-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")[..8]+".db.gz");
            try
            {
                ct.ThrowIfCancellationRequested();
                using(var dest=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=raw,Pooling=false}.ToString()))
                {
                    dest.Open();
                    lock(gate)db.BackupDatabase(dest);
                    ct.ThrowIfCancellationRequested();
                    using var vacuum=dest.CreateCommand();
                    vacuum.CommandText="VACUUM";
                    vacuum.ExecuteNonQuery();
                }
                CheckDatabase(raw);PublishCompressed(raw,file,ct);PruneBackups(folder,policy??new());return file;
            }
            finally{CleanScratch(raw);}
        }
    }
    public static HashSet<string> SelectBackups(IEnumerable<BackupEntry> files,BackupPolicy policy)
    {
        var ordered=files.OrderByDescending(f=>f.At).ThenByDescending(f=>f.File,StringComparer.Ordinal).ToList();var keep=new HashSet<string>();if(ordered.Count==0)return keep;
        var bounded=policy.Bounded();var selected=ordered.GroupBy(f=>f.At.Date).Take(bounded.DailyCopies).Select(g=>g.First()).ToList();
        selected.AddRange(ordered.GroupBy(f=>f.At.Date.AddDays(-((int)f.At.DayOfWeek+6)%7)).Take(bounded.WeeklyCopies).Select(g=>g.First()));
        long size=0,budget=bounded.BudgetMiB*1024L*1024;keep.Add(ordered[0].File);size=ordered[0].Bytes;
        foreach(var entry in selected.DistinctBy(f=>f.File).OrderByDescending(f=>f.At))if(!keep.Contains(entry.File)&&size+entry.Bytes<=budget){keep.Add(entry.File);size+=entry.Bytes;}
        return keep; // The newest verified snapshot survives even if it alone exceeds the budget.
    }
    static List<BackupEntry> ManagedBackups(string folder)=>Directory.EnumerateFiles(folder,"pulse-*")
        .Where(f=>Regex.IsMatch(Path.GetFileName(f),@"^pulse-\d{8}-\d{6}(?:-\d{3}-[a-f0-9]{8})?\.db(?:\.gz)?$")&&File.Exists(f+".sha256"))
        .Select(f=>new BackupEntry(f,new FileInfo(f).Length,File.GetLastWriteTimeUtc(f))).ToList();
    public static void PruneBackups(string folder,BackupPolicy policy)
    {
        var files=ManagedBackups(folder);var keep=SelectBackups(files,policy);
        foreach(var entry in files.Where(f=>!keep.Contains(f.File))){File.Delete(entry.File);File.Delete(entry.File+".sha256");}
    }
    public static (long Before,long After,int Converted) OptimizeBackups(string folder,BackupPolicy policy)
    {
        Directory.CreateDirectory(folder);var files=ManagedBackups(folder);long before=files.Sum(f=>f.Bytes);int converted=0;
        foreach(var entry in files.Where(f=>!f.File.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)))
        {
            string target=entry.File+".gz";
            VerifyBackup(entry.File);
            if(!File.Exists(target))PublishCompressed(entry.File,target,CancellationToken.None);
            // The decoded archive must be byte-for-byte identical to the checksum-verified, valid source DB.
            using(var archive=CheckedStream(target))using(var decoded=new GZipStream(archive,CompressionMode.Decompress))
                if(!string.Equals(Convert.ToHexString(SHA256.HashData(decoded)),File.ReadAllText(entry.File+".sha256").Trim(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Compressed copy differs from the original backup");
            File.SetLastWriteTimeUtc(target,entry.At);File.Delete(entry.File);File.Delete(entry.File+".sha256");File.Delete(entry.File+"-wal");File.Delete(entry.File+"-shm");converted++;
        }
        PruneBackups(folder,policy);return(before,ManagedBackups(folder).Sum(f=>f.Bytes),converted);
    }
    public (long Before,long After,int Converted) OptimizeStoredBackups(string folder,BackupPolicy policy)
    {lock(backupGate)return OptimizeBackups(folder,policy);}
}
