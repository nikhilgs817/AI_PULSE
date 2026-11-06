using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AiPulse;

public static partial class SelfTests
{
    static void RunBackupChecks(string root,Action<bool,string> check)
    {
        string folder=Path.Combine(root,"backup-tests");Directory.CreateDirectory(folder);string source=Path.Combine(folder,"source.db");
        using(var db=new Storage(source))db.PutUsage(new("backup-fixture","Codex","test","s",J.Now,123,45,100,0,7,null,null,"Reported"));
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=source,Pooling=false}.ToString()))
        {connection.Open();using var command=connection.CreateCommand();command.CommandText="CREATE TABLE padding(value BLOB);INSERT INTO padding VALUES(zeroblob(2097152));DELETE FROM padding;";command.ExecuteNonQuery();}
        long bloated=new FileInfo(source).Length;string archive;
        using(var db=new Storage(source))archive=db.Backup(Path.Combine(folder,"snapshots"));
        Storage.VerifyBackup(archive);string restored=Path.Combine(folder,"restored.db");Storage.MaterializeBackup(archive,restored);
        using(var db=new Storage(restored))check(archive.EndsWith(".db.gz")&&new FileInfo(restored).Length<bloated/2&&new FileInfo(archive).Length<new FileInfo(restored).Length&&db.Totals(0).Input==123,"Compressed backups remove free pages and restore exact retained usage");
        string legacy=Path.Combine(folder,"legacy.db");File.Copy(restored,legacy);File.WriteAllText(legacy+".sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(legacy))));Storage.VerifyBackup(legacy);Storage.MaterializeBackup(legacy,Path.Combine(folder,"legacy-restored.db"));
        check(File.ReadAllBytes(legacy).SequenceEqual(File.ReadAllBytes(Path.Combine(folder,"legacy-restored.db"))),"Legacy uncompressed backups remain restorable with checksum verification");
        File.WriteAllBytes(legacy+"-wal",[1,2,3]);try{Storage.VerifyBackup(legacy);check(false,"Uncheckpointed backup rejected");}catch(InvalidDataException){}finally{File.Delete(legacy+"-wal");}
        check(true,"A legacy backup with uncheckpointed WAL changes cannot be compressed or restored as a standalone file");
        string corrupt=Path.Combine(folder,"corrupt.db.gz");var bytes=File.ReadAllBytes(archive);bytes[0]=0;File.WriteAllBytes(corrupt,bytes);File.WriteAllText(corrupt+".sha256",Convert.ToHexString(SHA256.HashData(bytes)));
        string rejected=Path.Combine(folder,"must-not-restore.db");try{Storage.MaterializeBackup(corrupt,rejected);check(false,"Corrupt compressed backup rejected");}catch(InvalidDataException){}
        check(!File.Exists(rejected)&&!Directory.EnumerateFiles(folder,"must-not-restore.db.*.partial").Any(),"Corrupt compressed input cannot publish a restore file and partial files are cleaned");
        File.WriteAllText(corrupt+".sha256",new string('0',64));try{Storage.VerifyBackup(corrupt);check(false,"Checksum mismatch rejected");}catch(InvalidDataException){}
        check(true,"Compressed-backup checksum mismatch is rejected before restore");
        var dates=new[]{new DateTime(2026,11,6),new DateTime(2026,11,5),new DateTime(2026,11,4),new DateTime(2026,11,3),new DateTime(2026,11,1),new DateTime(2026,10,25),new DateTime(2026,10,18),new DateTime(2026,10,11)};
        var entries=dates.Select((date,i)=>new BackupEntry("snapshot-"+i,20*1024*1024,date)).ToList();
        var planned=Storage.SelectBackups(entries,new(3,4,512));check(planned.Count==6&&planned.Contains("snapshot-0")&&!planned.Contains("snapshot-3"),"Backup rotation keeps daily and weekly restore points without duplicate roles");
        var bounded=Storage.SelectBackups(entries,new(3,4,64));check(bounded.Count==3&&bounded.All(f=>f is "snapshot-0" or "snapshot-1" or "snapshot-2"),"Backup byte budget prefers recent restore points independently of history retention");
        var oversized=Storage.SelectBackups([entries[0] with{Bytes=80*1024*1024},entries[1]],new(3,4,64));check(oversized.SetEquals(["snapshot-0"]),"The newest snapshot is retained even when it alone exceeds the budget");
        string migrate=Path.Combine(folder,"migration");Directory.CreateDirectory(migrate);
        for(int i=0;i<3;i++){string file=Path.Combine(migrate,"pulse-2026110"+(6-i)+"-010203.db");File.Copy(legacy,file);File.Copy(legacy+".sha256",file+".sha256");File.SetLastWriteTimeUtc(file,DateTime.SpecifyKind(dates[i],DateTimeKind.Utc));}
        File.WriteAllText(Path.Combine(migrate,"unrelated.db"),"leave this alone");File.WriteAllText(Path.Combine(migrate,"pulse-incomplete.db.partial"),"not a completed backup");
        var optimized=Storage.OptimizeBackups(migrate,new(14,12,8192));
        check(optimized.Converted==3&&optimized.After<optimized.Before&&!Directory.EnumerateFiles(migrate,"pulse-*.db").Any()&&Directory.EnumerateFiles(migrate,"*.db.gz").All(f=>File.GetLastWriteTimeUtc(f).Date==dates.First(d=>Path.GetFileName(f).Contains(d.ToString("yyyyMMdd"))))&&File.Exists(Path.Combine(migrate,"unrelated.db"))&&File.Exists(Path.Combine(migrate,"pulse-incomplete.db.partial")),"Legacy optimization verifies lossless replacements, preserves restore dates and ignores unrelated/incomplete files");
        string mismatch=Path.Combine(folder,"mismatch");Directory.CreateDirectory(mismatch);string original=Path.Combine(mismatch,"pulse-20261106-010203.db");File.Copy(legacy,original);File.Copy(legacy+".sha256",original+".sha256");File.Copy(archive,original+".gz");File.Copy(archive+".sha256",original+".gz.sha256");
        // Different valid content at the compressed target must not authorize removing the legacy source.
        using(var changed=new Storage(original)){changed.PutUsage(new("different","Codex","test","s",J.Now,1,0,0,0,0,null,null,"Reported"));}
        File.WriteAllText(original+".sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))));
        try{Storage.OptimizeBackups(mismatch,new());check(false,"Mismatched archive rejected");}catch(InvalidDataException){}
        check(File.Exists(original),"A pre-existing valid archive with different contents cannot replace an original backup");
        check(new AppSettings().RetentionDays==90&&new AppSettings().BackupPolicy==new BackupPolicy(3,4,512)&&new BackupPolicy(-1,99,1).Bounded()==new BackupPolicy(1,12,64),"Backup defaults are separately bounded without reducing the 90-day history default");
    }
}
