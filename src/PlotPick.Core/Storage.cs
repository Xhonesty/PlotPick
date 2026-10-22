using System.IO.Compression;
using System.Text;
namespace PlotPick.Core;

public static class ProjectStore
{
    public static void Save(Project project,string path)
    {
        SeriesOptions.Initialize(project);
        var absolute=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string temp=absolute+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
            {
                using(var writer=new StreamWriter(zip.CreateEntry("project.json").Open(),Encoding.UTF8))writer.Write(Json.Serialize(project));
                var needed=project.Documents.Select(d=>d.SourceAsset).Concat(project.Plots.SelectMany(p=>new[]{p.ImageAsset,p.OriginalAsset})).Where(k=>!string.IsNullOrEmpty(k)).Distinct();
                foreach(var key in needed)
                {
                    if(!project.Assets.TryGetValue(key,out var bytes))throw new InvalidDataException("项目缺少源文件："+key);
                    using var entry=zip.CreateEntry(key,CompressionLevel.Fastest).Open();entry.Write(bytes);
                }
            }
            // Flush completed ZIP before committing. File.Replace attempts ACL/metadata merges
            // that are denied on some user directories; same-directory rename avoids those merges.
            using(var completed=new FileStream(temp,FileMode.Open,FileAccess.ReadWrite,FileShare.None))completed.Flush(true);
            if(File.Exists(absolute))
            {
                string backupTemp=temp+".bak";
                try{File.Copy(absolute,backupTemp);File.Move(backupTemp,absolute+".bak",true);}
                finally{if(File.Exists(backupTemp))File.Delete(backupTemp);}
            }
            File.Move(temp,absolute,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static Project Load(string path)
    {
        using var zip=ZipFile.OpenRead(path);
        var manifest=zip.GetEntry("project.json")??throw new InvalidDataException("不是有效的 PlotPick 项目。");
        if(manifest.Length>100_000_000)throw new InvalidDataException("项目描述文件过大。");
        using var reader=new StreamReader(manifest.Open());var project=Json.Deserialize<Project>(reader.ReadToEnd());
        if(project.Version is not (1 or 2))throw new InvalidDataException($"项目格式 v{project.Version} 不受支持，原文件未修改。");
        project.Version=2;SeriesOptions.Initialize(project);
        long total=0;
        foreach(var entry in zip.Entries.Where(e=>e.FullName.StartsWith("assets/",StringComparison.Ordinal)))
        {
            total+=entry.Length;if(total>2_000_000_000)throw new InvalidDataException("项目解压内容超过 2 GB。");
            using var source=entry.Open();using var dest=new MemoryStream();source.CopyTo(dest);project.Assets[entry.FullName]=dest.ToArray();
        }
        foreach(var p in project.Plots)if(!project.Assets.ContainsKey(p.ImageAsset))throw new InvalidDataException("项目的图像资源不完整。");
        return project;
    }
}
public class BatchTemplate
{
    public int Version {get;set;}=1;
    public int Width {get;set;} public int Height {get;set;}
    public AxisSystem Axes {get;set;}=new();
    public ExtractionOptions Options {get;set;}=new();
    public SeriesKind Kind {get;set;}
    public bool RightAxis {get;set;}
    public static BatchTemplate From(Plot plot,Series series)=>new(){Width=plot.Width,Height=plot.Height,Axes=Json.Clone(plot.Axes),Options=SeriesOptions.Get(plot,series),Kind=series.Kind,RightAxis=series.RightAxis};
    public (AxisSystem Axes,ExtractionOptions Options) ForSize(int width,int height,bool sameScale)
    {
        if(Version!=1)throw new InvalidDataException("不支持的模板版本。");
        if(Math.Abs((double)width/height-(double)Width/Height)>0.01)throw new ArgumentException("图像宽高比与模板不同，需人工复核。");
        double sx=(double)width/Width,sy=(double)height/Height;
        Vec Map(Vec p)=>new(p.X*sx,p.Y*sy);
        Region MapR(Region r)=>new(r.X*sx,r.Y*sy,r.Width*sx,r.Height*sy);
        var axes=Json.Clone(Axes);foreach(var a in new[]{axes.X,axes.Y,axes.RightY}.OfType<Axis>()){a.A=Map(a.A);a.B=Map(a.B);}axes.Checks.Clear();axes.Calibrated=sameScale;
        var o=Json.Clone(Options);if(o.Roi is Region roi)o.Roi=MapR(roi);o.Exclusions=o.Exclusions.Select(MapR).ToList();if(o.Seed is Vec seed)o.Seed=Map(seed);if(o.Baseline is double b)o.Baseline=b*(Kind==SeriesKind.HorizontalBars?sx:sy);
        return(axes,o);
    }
}
